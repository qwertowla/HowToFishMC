using System;
using BepInEx.Logging;
using CrossMC.Bridge;
using UnityEngine;
using UnityEngine.Rendering;

namespace CrossMC.HowToFish
{
    /// <summary>
    /// Captures the How to Fish game camera into the CrossMC <b>host frame</b> channel
    /// (host -&gt; Minecraft).
    ///
    /// <p>A dedicated capture camera mirrors the game camera and renders into a private
    /// <see cref="RenderTexture"/>; the pixels are read back with <see cref="AsyncGPUReadback"/>
    /// (preferred) or, as a clearly-marked fallback, <see cref="Texture2D.ReadPixels"/>. The game's own
    /// camera is never retargeted, so the normal window keeps rendering.</p>
    ///
    /// <p>Independent of every other CrossMC channel: on any failure a frame is dropped, never
    /// retried synchronously, so player/state/entity/collider/damage are never affected.</p>
    /// </summary>
    public sealed class HostFrameExporter : MonoBehaviour
    {
        private static readonly ManualLogSource Log = BepInEx.Logging.Logger.CreateLogSource("CrossMC.HowToFish");

        private BridgeMemory _memory;
        private HostConfig _config;

        private Camera _captureCamera;
        private RenderTexture _renderTexture;
        private Texture2D _readback;
        private byte[] _bytes;

        private int _width;
        private int _height;
        private bool _async;
        private bool _loggedFallback;
        private float _timer;

        private AsyncGPUReadbackRequest _request;
        private bool _inFlight;

        private long _captured;
        private long _published;
        private long _dropped;
        private long _lastLogMs;

        public void Init(BridgeMemory memory, HostConfig config)
        {
            _memory = memory;
            _config = config;
        }

        private void Update()
        {
            if (_memory == null || _config == null || !_config.RenderEnabled)
            {
                return;
            }

            EnsureResources();

            if (_renderTexture == null)
            {
                return;
            }

            // 1) Finish a pending async readback (its data is only valid until the next request).
            if (_async && _inFlight)
            {
                if (!_request.done)
                {
                    return;
                }

                _inFlight = false;

                if (_request.hasError)
                {
                    _dropped++;
                }
                else
                {
                    PublishFromRequest();
                }
            }

            // 2) Throttle new captures to the configured FPS.
            _timer += Time.unscaledDeltaTime;
            float interval = _config.RenderFps > 0 ? 1f / _config.RenderFps : 0f;

            if (_timer < interval)
            {
                return;
            }

            _timer = 0f;

            // 3) Render the game camera once into the private RenderTexture.
            if (!RenderCamera())
            {
                return;
            }

            _captured++;

            // 4) Read back: async (issue a request) or the synchronous fallback.
            if (_async)
            {
                _request = AsyncGPUReadback.Request(_renderTexture, 0, TextureFormat.RGBA32);
                _inFlight = true;
            }
            else
            {
                ReadBackSync();
            }
        }

        private void EnsureResources()
        {
            _width = Mathf.Clamp(_config.RenderWidth, 16, Protocol.MaxHostFrameW);
            _height = Mathf.Clamp(_config.RenderHeight, 16, Protocol.MaxHostFrameH);
            _async = _config.RenderAsync && SystemInfo.supportsAsyncGPUReadback;

            if (!_async && !_loggedFallback)
            {
                _loggedFallback = true;
                Log.LogWarning("CrossMC HostFrame: AsyncGPUReadback unavailable (or disabled) — using the "
                        + "synchronous ReadPixels fallback; this may hitch. Set render.async=true on D3D11.");
            }

            if (_renderTexture == null || _renderTexture.width != _width || _renderTexture.height != _height)
            {
                if (_renderTexture != null)
                {
                    _renderTexture.Release();
                    Destroy(_renderTexture);
                }

                _renderTexture = new RenderTexture(_width, _height, 24, RenderTextureFormat.ARGB32)
                {
                    useMipMap = false,
                    autoGenerateMips = false,
                    name = "CrossMC.HostFrame",
                };
                _renderTexture.Create();
                Log.LogInfo("CrossMC HostFrame: render texture " + _width + "x" + _height
                        + " (async=" + _async + ")");
            }
        }

        private bool RenderCamera()
        {
            Camera source = Camera.main;

            if (source == null)
            {
                Camera[] cameras = Camera.allCameras;

                if (cameras != null && cameras.Length > 0)
                {
                    source = cameras[0];
                }
            }

            if (source == null)
            {
                return false;
            }

            if (_captureCamera == null)
            {
                var go = new GameObject("CrossMC.CaptureCamera");
                DontDestroyOnLoad(go);
                _captureCamera = go.AddComponent<Camera>();
                _captureCamera.enabled = false;
            }

            _captureCamera.CopyFrom(source);
            _captureCamera.enabled = false;
            _captureCamera.targetTexture = _renderTexture;
            _captureCamera.Render();
            return true;
        }

        private void PublishFromRequest()
        {
            var data = _request.GetData<byte>(0);
            int need = _width * _height * 4;

            if (_bytes == null || _bytes.Length != need)
            {
                _bytes = new byte[need];
            }

            if (data.Length < need)
            {
                _dropped++;
                return;
            }

            data.CopyTo(_bytes);
            // AsyncGPUReadback yields top-down rows (row 0 = top), matching Minecraft's NativeImage.
            Publish(_bytes, 0);
        }

        private void ReadBackSync()
        {
            RenderTexture previous = RenderTexture.active;
            RenderTexture.active = _renderTexture;

            if (_readback == null || _readback.width != _width || _readback.height != _height)
            {
                if (_readback != null)
                {
                    Destroy(_readback);
                }

                _readback = new Texture2D(_width, _height, TextureFormat.RGBA32, false);
            }

            try
            {
                _readback.ReadPixels(new Rect(0, 0, _width, _height), 0, 0, false);
                _readback.Apply(false, false);
            }
            finally
            {
                RenderTexture.active = previous;
            }

            var raw = _readback.GetRawTextureData<byte>();
            int need = _width * _height * 4;

            if (_bytes == null || _bytes.Length != need)
            {
                _bytes = new byte[need];
            }

            if (raw.Length < need)
            {
                _dropped++;
                return;
            }

            raw.CopyTo(_bytes);
            // ReadPixels yields bottom-up rows; flag it so the Minecraft consumer flips them.
            Publish(_bytes, Protocol.OverlayBottomUp);
        }

        private void Publish(byte[] pixels, int flags)
        {
            try
            {
                _memory.PublishHostFrame(pixels, _width, _height, Protocol.FormatRgba8, flags);
                _published++;

                long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

                if (now - _lastLogMs >= 2000)
                {
                    _lastLogMs = now;
                    Log.LogInfo("CrossMC HostFrame: captured=" + _captured + " published=" + _published
                            + " dropped=" + _dropped + " " + _width + "x" + _height + "@" + _config.RenderFps
                            + "fps async=" + _async);
                }
            }
            catch (Exception e)
            {
                _dropped++;
                Log.LogWarning("CrossMC HostFrame: publish failed: " + e.Message);
            }
        }

        private void OnDestroy()
        {
            if (_renderTexture != null)
            {
                _renderTexture.Release();
                Destroy(_renderTexture);
            }

            if (_readback != null)
            {
                Destroy(_readback);
            }
        }
    }
}
