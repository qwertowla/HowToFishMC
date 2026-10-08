using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using BepInEx;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.Controls;
using BridgeMemory = CrossMC.Bridge.BridgeMemory;
using BridgeProtocol = CrossMC.Bridge.Protocol;
using HostState = CrossMC.Bridge.HostState;
using McState = CrossMC.Bridge.McState;
using EntityMap = CrossMC.Bridge.EntityMap;
using DamageEvent = CrossMC.Bridge.DamageEvent;
using InputEvent = CrossMC.Bridge.InputEvent;
using BridgeCollider = CrossMC.Bridge.Collider;

namespace CrossMC.HowToFish
{
    /// <summary>
    /// CrossMC host adapter for <i>How to Fish</i>.
    ///
    /// <p><b>Player authority:</b> the Minecraft player is the primary player. This adapter only
    /// (a) captures keyboard/mouse into the CrossMC <c>InputRing</c>, (b) publishes the host
    /// environment + host world (colliders/entities) and (c) — when enabled — drives the How to Fish
    /// player to <b>follow</b> the authoritative <c>McState</c>. It never pushes a host transform
    /// back onto the Minecraft player.</p>
    ///
    /// <p>Reads Minecraft's frame (overlay), publishes host environment/world, applies Minecraft's
    /// damage events with this adapter's configured multipliers.</p>
    ///
    /// <p>All game access runs on the Unity main thread (Update/OnGUI). Only the shared-memory
    /// reads/writes are thread-agnostic.</p>
    /// </summary>
    [BepInPlugin("com.crossmc.howtofish", "CrossMC - How to Fish", "0.1.0")]
    [BepInProcess("How to Fish.exe")]
    public class Plugin : BaseUnityPlugin
    {
        private BridgeMemory _memory;
        private HostConfig _config;
        private CoordinateMapper _mapper;
        private FrameOverlay _overlay;

        private float _colliderTimer;
        private float _entityTimer;
        private float _statusTimer = 2f;
        private long _inputEvents;
        private long _followMoves;
        private bool _loggedInputUnavailable;
        private bool _anchored;
        private long _cameraFollows;
        private string _followSkip = "off";
        private bool _lastMcAlive;

        // Diagnostics: requested -> applied -> survived-next-frame
        private string _followState = "INIT";
        private string _cameraState = "INIT";
        private bool _dumpedComponents;
        private long _lastMcLogMs;
        private double _lastMcX, _lastMcY, _lastMcZ, _lastMcYaw, _lastMcPitch;
        private long _lastMcFrames = -1;
        private bool _hasPendingFollow;
        private Vector3 _pendingFollowTarget;
        private long _followLogCounter;
        private bool _hasPendingCam;
        private Vector3 _pendingCamRequested;
        private long _cameraLogCounter;
        private bool _hardLockApplied;
        private Player _hardLockPlayer;



        private readonly UnityEngine.Collider[] _overlapBuffer = new UnityEngine.Collider[512];
        private readonly List<BridgeCollider> _colliders = new List<BridgeCollider>();
        private readonly List<EntityMap> _entities = new List<EntityMap>();

        // Stable CrossEntityId allocation: host-native id -> CrossEntityId (kept for the session),
        // and the current CrossEntityId -> host creature binding (a lookup query).
        private readonly Dictionary<int, int> _hostToCross = new Dictionary<int, int>();
        private readonly Dictionary<int, Creature> _crossToCreature = new Dictionary<int, Creature>();
        private int _nextCrossId = 1000;

        private void Awake()
        {
            _config = HostConfig.Load();
            _mapper = new CoordinateMapper(_config);

            // The user plays Minecraft (focused); How to Fish must keep updating in the background or
            // the follow loop stops the moment the player focuses Minecraft. Force it on.
            Application.runInBackground = true;
            Logger.LogInfo("CrossMC: Application.runInBackground = true (host keeps following while Minecraft is focused)");

            try
            {
                _memory = BridgeMemory.Open();
                int pid = Process.GetCurrentProcess().Id;

                if (!_memory.HasValidHeader())
                {
                    _memory.InitHeader(pid, 0);
                    _memory.InitTripleBuffer();
                    _memory.InitTables();
                    Logger.LogInfo("CrossMC created shared memory header (hostPid=" + pid + ")");
                }
                else
                {
                    _memory.WriteHostPid(pid);
                    Logger.LogInfo("CrossMC joined shared memory (hostPid=" + pid + ")");
                }

                var go = new GameObject("CrossMC.Overlay");
                DontDestroyOnLoad(go);
                _overlay = go.AddComponent<FrameOverlay>();
                _overlay.Init(_memory, _config);

                Logger.LogInfo("CrossMC host ready. bridgeConfig=" + global::CrossMC.Bridge.Config.ConfigSource()
                        + " hostConfig=" + HostConfig.ConfigSource());
                Logger.LogInfo("CrossMC effective config: " + _config.Describe());
            }
            catch (Exception e)
            {
                Logger.LogError("CrossMC failed to open shared memory: " + e);
                _memory = null;
            }
        }

        private void Update()
        {
            if (!Application.runInBackground)
            {
                Application.runInBackground = true;
            }

            if (_memory == null)
            {
                return;
            }

            _memory.WriteHostHeartbeat(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());

            // Align the two coordinate systems once, from the current players (no teleport).
            EnsureAnchor();

            PublishHostEnvironment();

            // Host -> Minecraft: capture keyboard/mouse and forward as InputRing events.
            try
            {
                CaptureInput();
            }
            catch (Exception e)
            {
                Logger.LogError("CrossMC input capture failed: " + e);
            }

            // Minecraft -> host: make the How to Fish player follow the authoritative McState.
            try
            {
                FollowMcPlayer();
                FollowVitals();
            }
            catch (Exception e)
            {
                Logger.LogError("CrossMC follow failed: " + e);
            }

            float dt = Time.deltaTime;
            _colliderTimer -= dt;

            if (_colliderTimer <= 0f)
            {
                _colliderTimer = _config.ColliderInterval;
                ExportColliders();
            }

            _entityTimer -= dt;

            if (_entityTimer <= 0f)
            {
                _entityTimer = _config.EntityInterval;
                ExportEntities();
            }

            ApplyDamage();

            bool mcAlive = _memory.McAlive(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());

            if (mcAlive != _lastMcAlive)
            {
                _lastMcAlive = mcAlive;
                Logger.LogInfo("CrossMC: Minecraft " + (mcAlive
                        ? "connected (publishing McState)"
                        : "not publishing / disconnected (start Minecraft and load a world)"));
            }

            _statusTimer -= dt;

            if (_statusTimer <= 0f)
            {
                _statusTimer = 2f;
                long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                Player hp = Player.LocalPlayer;
                string mcStr = "-";

                if (_memory.McAlive(now))
                {
                    try
                    {
                        McState m = _memory.ReadMcState();
                        mcStr = m.X.ToString("F1") + "," + m.Y.ToString("F1") + "," + m.Z.ToString("F1")
                                + " yaw=" + m.Yaw.ToString("F0") + " pitch=" + m.Pitch.ToString("F0")
                                + " flags=" + m.Flags;
                    }
                    catch (Exception)
                    {
                        mcStr = "read-error";
                    }
                }

                string playersInfo = "";

                if (!hp)
                {
                    try
                    {
                        int total = 0;
                        int localOwned = 0;

                        foreach (Player p in PlayerManager.Players)
                        {
                            if (p == null)
                            {
                                continue;
                            }

                            total++;
                            var no = p.NetworkObject;

                            if (no != null && no.Owner != null && no.Owner.IsLocalClient)
                            {
                                localOwned++;
                            }
                        }

                        playersInfo = " players=" + total + " localOwned=" + localOwned;
                    }
                    catch (Exception e)
                    {
                        playersInfo = " players=err(" + e.GetType().Name + ")";
                    }
                }

                Logger.LogInfo("CrossMC status: hostAlive=" + _memory.HostAlive(now)
                        + " mcAlive=" + _memory.McAlive(now)
                        + " mcState=(" + mcStr + ")"
                        + playersInfo
                        + " | hostPlayer=" + (hp ? "ok" : "null")
                        + " hostTransform=" + (hp && hp.Transform ? "ok" : "null")
                        + " hostMovement=" + (hp && hp.Movement ? "ok" : "null")
                        + " hostRigidbody=" + (hp && hp.Rigidbody ? "ok" : "null")
                        + " hostCamera=" + (hp && hp.CamObject ? "ok" : "null")
                        + " | anchored=" + _anchored
                        + " follow=" + _config.PlayerFollow
                        + " followState=" + _followState
                        + " followSkip=" + _followSkip
                        + " followMoves=" + _followMoves
                        + " cameraState=" + _cameraState
                        + " cameraFollows=" + _cameraFollows
                        + " entities=" + _entities.Count
                        + " colliders=" + _colliders.Count);
            }
        }

        /// <summary>
        /// Debug/verification mode: turn the How to Fish player into a PURE follower. Disables the
        /// host's own movement simulation and any transform-sync components so nothing overwrites the
        /// position we set from McState. Called once per local player.
        /// </summary>
        private void ApplyHardLockOnce(Player player)
        {
            if (_hardLockApplied && _hardLockPlayer == player)
            {
                return;
            }

            _hardLockApplied = true;
            _hardLockPlayer = player;
            Logger.LogInfo("CrossMC hardlock: applying to host player (pure follower)");

            if (player.Movement != null)
            {
                player.Movement.enabled = false;
                Logger.LogInfo("CrossMC hardlock: disabled PlayerMovement");
            }

            if (player.Rigidbody != null)
            {
                player.Rigidbody.isKinematic = true;
                player.Rigidbody.useGravity = false;
                player.Rigidbody.linearVelocity = Vector3.zero;
                Logger.LogInfo("CrossMC hardlock: Rigidbody -> kinematic, gravity off");
            }

            foreach (MonoBehaviour mb in player.GetComponentsInChildren<MonoBehaviour>(true))
            {
                if (mb == null)
                {
                    continue;
                }

                string n = mb.GetType().Name;

                if (n.Contains("NetworkTransform") || n.Contains("RigidbodySync")
                        || n.Contains("NetworkTickSmoother") || n.Contains("Prediction"))
                {
                    mb.enabled = false;
                    Logger.LogInfo("CrossMC hardlock: disabled " + n);
                }
            }
        }

        /// <summary>
        /// Hard-lock follower update: force the host player position and camera straight from McState
        /// in LateUpdate (after the game's own Update/FixedUpdate pass), so nothing re-applies an old
        /// position for the rendered frame.
        /// </summary>
        private void HardLockLateUpdate()
        {
            if (!_config.FollowHardLock || _memory == null)
            {
                return;
            }

            Player player = Player.LocalPlayer;

            if (!player || !player.Transform)
            {
                return;
            }

            ApplyHardLockOnce(player);

            if (!_memory.McAlive(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()))
            {
                return;
            }

            McState mc;

            try
            {
                mc = _memory.ReadMcState();
            }
            catch (Exception)
            {
                return;
            }

            if ((mc.Flags & McState.InWorld) == 0)
            {
                return;
            }

            Vector3 host = _mapper.ToHost(new Vector3((float)mc.X, (float)mc.Y, (float)mc.Z));

            if (player.Rigidbody != null)
            {
                player.Rigidbody.position = host;
            }

            player.Transform.position = host;

            if (player.CamObject != null)
            {
                player.CamObject.rotation = Quaternion.Euler(
                        mc.Pitch * _config.CameraPitchSign, mc.Yaw * _config.CameraYawSign, 0f);
            }

            if (_followMoves == 0)
            {
                Logger.LogInfo("CrossMC hardlock: forcing host player to " + host.x.ToString("F1")
                        + "," + host.y.ToString("F1") + "," + host.z.ToString("F1"));
            }

            _followMoves++;
        }

        /// <summary>
        /// Host camera follows the Minecraft player's view (McState yaw/pitch). Runs in LateUpdate so
        /// it wins over the game's own camera update for the rendered frame; the host camera remains
        /// purely a follower (Minecraft is the view authority).
        /// </summary>
        private void LateUpdate()
        {
            HardLockLateUpdate();

            if (_memory == null || !_config.FollowCamera)
            {
                _cameraState = "DISABLED";
                return;
            }

            if (!_memory.McAlive(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()))
            {
                _cameraState = "NO_MC";
                return;
            }

            Player player = Player.LocalPlayer;
            Transform camTransform = player && player.CamObject
                    ? player.CamObject
                    : (FindCamera() != null ? FindCamera().transform : null);

            if (camTransform == null)
            {
                _cameraState = "NO_CAMERA";
                return;
            }

            McState mc;

            try
            {
                mc = _memory.ReadMcState();
            }
            catch (Exception)
            {
                _cameraState = "NO_MC";
                return;
            }

            if ((mc.Flags & McState.InWorld) == 0)
            {
                _cameraState = "NO_MC_WORLD";
                return;
            }

            // Did last frame's requested camera rotation survive?
            if (_hasPendingCam)
            {
                float err = Quaternion.Angle(camTransform.rotation, Quaternion.Euler(_pendingCamRequested));
                _cameraState = err > 2f ? "OVERRIDDEN" : "APPLIED";
                _hasPendingCam = false;
            }

            float yaw = mc.Yaw * _config.CameraYawSign;
            float pitch = mc.Pitch * _config.CameraPitchSign;
            Vector3 camBefore = camTransform.eulerAngles;
            camTransform.rotation = Quaternion.Euler(pitch, yaw, 0f);
            Vector3 camAfter = camTransform.eulerAngles;
            _pendingCamRequested = new Vector3(pitch, yaw, 0f);
            _hasPendingCam = true;

            if (++_cameraLogCounter % 30 == 1)
            {
                Logger.LogInfo("CrossMC camera: mcYaw=" + mc.Yaw.ToString("F0") + " mcPitch=" + mc.Pitch.ToString("F0")
                        + " requested=(" + pitch.ToString("F0") + "," + yaw.ToString("F0") + ")"
                        + " camBefore=" + camBefore.ToString("F0") + " camAfter=" + camAfter.ToString("F0")
                        + " nextState=" + _cameraState);
            }

            _cameraFollows++;
        }

        /// <summary>
        /// One-time coordinate alignment: makes the host player's current position and the Minecraft
        /// player's current position denote the same point. Afterwards the mapping (origin/scale/
        /// axis) is FIXED — it is never recomputed from the moving players, so host colliders,
        /// entities and world objects stay correctly placed. Requires <c>transform.autoAnchor</c>;
        /// otherwise the manual <c>transform.origin*</c> is used.
        /// </summary>
        private void EnsureAnchor()
        {
            if (_anchored || !_config.AutoAnchor)
            {
                return;
            }

            if (!_memory.McAlive(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()))
            {
                return;
            }

            Player player = Player.LocalPlayer;

            if (!player || !player.Transform)
            {
                return;
            }

            McState mc;

            try
            {
                mc = _memory.ReadMcState();
            }
            catch (Exception)
            {
                return;
            }

            // Only anchor once the Minecraft player actually exists in a world, otherwise we'd
            // anchor against a default (0,0,0) sample and shift the host player later.
            if ((mc.Flags & McState.InWorld) == 0)
            {
                return;
            }

            Vector3 hp = player.Transform.position;
            _mapper.Anchor(hp, new Vector3((float)mc.X, (float)mc.Y, (float)mc.Z));
            _anchored = true;
            Logger.LogInfo("CrossMC: anchored host(" + hp.x.ToString("F1") + "," + hp.y.ToString("F1") + "," + hp.z.ToString("F1")
                    + ") <-> mc(" + mc.X.ToString("F1") + "," + mc.Y.ToString("F1") + "," + mc.Z.ToString("F1")
                    + "); follow will keep the host player near this point.");
        }

        /// <summary>
        /// Publishes the host's OWN environment/avatar as <c>HostState</c>. This is informational
        /// only: the Minecraft player is authoritative and this is never used to drive it. The host
        /// -> Minecraft player channel is <c>InputRing</c> (see <see cref="CaptureInput"/>).
        /// </summary>
        private void PublishHostEnvironment()
        {
            Player player = Player.LocalPlayer;
            long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var state = new HostState
            {
                Flags = 1, // in game
                TimestampMs = now,
                UnitsPerBlock = 1f / Mathf.Max(0.0001f, _config.Scale),
                ViewportW = Screen.width,
                ViewportH = Screen.height,
            };

            // Informational host avatar/camera (NOT authority for the Minecraft player).
            if (player && player.Transform)
            {
                Vector3 pos = _mapper.ToMc(player.Transform.position);
                Vector3 rot = player.CamObject ? player.CamObject.eulerAngles : Vector3.zero;
                state.PosX = pos.x;
                state.PosY = pos.y;
                state.PosZ = pos.z;
                state.Yaw = rot.y;
                state.Pitch = rot.x;
                state.Roll = rot.z;
                state.EyeHeight = 1.62f;
            }

            _memory.WriteHostState(state);
        }

        /// <summary>
        /// Host -> Minecraft player channel: capture keyboard/mouse with the Unity Input System and
        /// push CrossMC input events. Minecraft interprets them with its own rules; the host never
        /// moves the Minecraft player directly.
        /// </summary>
        private void CaptureInput()
        {
            if (!_config.InputCapture)
            {
                return;
            }

            long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            Keyboard keyboard = Keyboard.current;
            Mouse mouseDevice = Mouse.current;

            if (keyboard == null && mouseDevice == null)
            {
                // Fall back to the legacy Input API if the new Input System has no devices.
                CaptureLegacy(now);
                return;
            }

            if (keyboard != null)
            {
                var keys = keyboard.allKeys;

                for (int i = 0; i < keys.Count; i++)
                {
                    KeyControl key = keys[i];

                    if (!key.wasPressedThisFrame && !key.wasReleasedThisFrame)
                    {
                        continue;
                    }

                    int semantic = SemanticKey(key.keyCode);

                    if (semantic == 0)
                    {
                        continue; // unmapped key — not forwarded
                    }

                    PushInput(key.wasPressedThisFrame ? BridgeProtocol.InputKeyDown : BridgeProtocol.InputKeyUp,
                            semantic, 0, 0, now);
                }
            }

            Mouse mouse = mouseDevice;

            if (mouse != null)
            {
                MouseButton(mouse.leftButton, 0, now);
                MouseButton(mouse.rightButton, 1, now);
                MouseButton(mouse.middleButton, 2, now);

                Vector2 delta = mouse.delta.ReadValue();

                if (delta.x != 0f || delta.y != 0f)
                {
                    PushInput(BridgeProtocol.InputMouseMove, 0,
                            (int)Math.Round(delta.x), (int)Math.Round(delta.y), now);
                }

                float scroll = mouse.scroll.ReadValue().y;

                if (scroll != 0f)
                {
                    PushInput(BridgeProtocol.InputMouseWheel, 0, (int)Math.Round(scroll), 0, now);
                }
            }
        }

        /// <summary>
        /// Fallback capture through the legacy <see cref="UnityEngine.Input"/> API, used when the new
        /// Input System reports no devices. Wrapped in try/catch because legacy Input throws when the
        /// project is configured for the new Input System only.
        /// </summary>
        private void CaptureLegacy(long now)
        {
            try
            {
                LegacyKey(KeyCode.W, BridgeProtocol.KeyForward, now);
                LegacyKey(KeyCode.S, BridgeProtocol.KeyBack, now);
                LegacyKey(KeyCode.A, BridgeProtocol.KeyLeft, now);
                LegacyKey(KeyCode.D, BridgeProtocol.KeyRight, now);
                LegacyKey(KeyCode.Space, BridgeProtocol.KeyJump, now);
                LegacyKey(KeyCode.LeftShift, BridgeProtocol.KeySneak, now);
                LegacyKey(KeyCode.LeftControl, BridgeProtocol.KeySprint, now);

                LegacyMouseButton(0, now);
                LegacyMouseButton(1, now);
                LegacyMouseButton(2, now);

                float dx = Input.GetAxisRaw("Mouse X");
                float dy = Input.GetAxisRaw("Mouse Y");

                if (dx != 0f || dy != 0f)
                {
                    PushInput(BridgeProtocol.InputMouseMove, 0,
                            (int)Math.Round(dx * 10f), (int)Math.Round(dy * 10f), now);
                }

                float scroll = Input.mouseScrollDelta.y;

                if (scroll != 0f)
                {
                    PushInput(BridgeProtocol.InputMouseWheel, 0, (int)Math.Round(scroll), 0, now);
                }
            }
            catch (Exception)
            {
                if (!_loggedInputUnavailable)
                {
                    _loggedInputUnavailable = true;
                    Logger.LogWarning("CrossMC: no usable input backend (new Input System has no "
                            + "Keyboard/Mouse and legacy Input is unavailable). Host input disabled.");
                }
            }
        }

        private void LegacyKey(KeyCode key, int semantic, long now)
        {
            if (Input.GetKeyDown(key))
            {
                PushInput(BridgeProtocol.InputKeyDown, semantic, 0, 0, now);
            }
            else if (Input.GetKeyUp(key))
            {
                PushInput(BridgeProtocol.InputKeyUp, semantic, 0, 0, now);
            }
        }

        private void LegacyMouseButton(int code, long now)
        {
            if (Input.GetMouseButtonDown(code))
            {
                PushInput(BridgeProtocol.InputMouseDown, code, 0, 0, now);
            }
            else if (Input.GetMouseButtonUp(code))
            {
                PushInput(BridgeProtocol.InputMouseUp, code, 0, 0, now);
            }
        }

        private void MouseButton(UnityEngine.InputSystem.Controls.ButtonControl button, int code, long now)
        {
            if (button.wasPressedThisFrame)
            {
                PushInput(BridgeProtocol.InputMouseDown, code, 0, 0, now);
            }
            else if (button.wasReleasedThisFrame)
            {
                PushInput(BridgeProtocol.InputMouseUp, code, 0, 0, now);
            }
        }

        private void PushInput(int type, int code, int a, int b, long now)
        {
            if (_inputEvents == 0)
            {
                Logger.LogInfo("CrossMC: capturing input (first event type=" + type + " code=" + code
                        + "). NOTE: How to Fish must be the focused window for input to be captured.");
            }

            _inputEvents++;
            _memory.PushInput(new InputEvent { Type = type, Code = code, A = a, B = b, TimestampMs = now });
        }

        /// <summary>
        /// Maps a Unity Input System key to a CrossMC keyboard semantic ({@code CROSSMC_KEY_*},
        /// 0 = not forwarded). This is the explicit Unity Key -> CrossMC -> Minecraft KeyBinding
        /// mapping; the wire format never carries a raw engine key code.
        /// </summary>
        private static int SemanticKey(Key key)
        {
            switch (key)
            {
                case Key.W: return BridgeProtocol.KeyForward;
                case Key.S: return BridgeProtocol.KeyBack;
                case Key.A: return BridgeProtocol.KeyLeft;
                case Key.D: return BridgeProtocol.KeyRight;
                case Key.Space: return BridgeProtocol.KeyJump;
                case Key.LeftShift: return BridgeProtocol.KeySneak;
                case Key.LeftCtrl: return BridgeProtocol.KeySprint;
                case Key.E: return BridgeProtocol.KeyInventory;
                case Key.Q: return BridgeProtocol.KeyDrop;
                case Key.F: return BridgeProtocol.KeySwapHands;
                default: return 0;
            }
        }

        private static Vector3 HostPos(Player player)
        {
            if (player.Rigidbody != null)
            {
                return player.Rigidbody.position;
            }

            return player.Transform != null ? player.Transform.position : Vector3.zero;
        }

        private void DumpComponentsOnce(Player player)
        {
            if (_dumpedComponents)
            {
                return;
            }

            _dumpedComponents = true;
            var sb = new System.Text.StringBuilder("CrossMC: local player found. owner=");
            sb.Append(player.NetworkObject != null ? player.NetworkObject.IsOwner.ToString() : "?");
            sb.Append(" server=").Append(player.NetworkObject != null ? player.NetworkObject.IsServerInitialized.ToString() : "?");
            sb.Append(" components=[");

            foreach (Component c in player.GetComponents<Component>())
            {
                if (c != null)
                {
                    sb.Append(c.GetType().Name).Append(',');
                }
            }

            sb.Append(']');

            if (player.Rigidbody != null)
            {
                sb.Append(" rb.isKinematic=").Append(player.Rigidbody.isKinematic);
                sb.Append(" rb.interpolation=").Append(player.Rigidbody.interpolation);
            }

            Logger.LogInfo(sb.ToString());

            var c2 = new System.Text.StringBuilder("CrossMC: player children: ");
            Transform t = player.transform;

            for (int i = 0; i < t.childCount; i++)
            {
                Transform ch = t.GetChild(i);
                c2.Append('[').Append(ch.name).Append(':');

                foreach (Component c in ch.GetComponents<Component>())
                {
                    if (c != null)
                    {
                        c2.Append(c.GetType().Name).Append(',');
                    }
                }

                c2.Append("] ");
            }

            Logger.LogInfo(c2.ToString());
        }

        private void LogMcChange(McState mc)
        {
            long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

            if (now - _lastMcLogMs < 1000)
            {
                return;
            }

            _lastMcLogMs = now;
            bool changed = mc.X != _lastMcX || mc.Y != _lastMcY || mc.Z != _lastMcZ
                    || mc.Yaw != _lastMcYaw || mc.Pitch != _lastMcPitch || mc.FrameCounter != _lastMcFrames;
            Logger.LogInfo("CrossMC mcState: x=" + mc.X.ToString("F2") + " y=" + mc.Y.ToString("F2") + " z=" + mc.Z.ToString("F2")
                    + " yaw=" + mc.Yaw.ToString("F1") + " pitch=" + mc.Pitch.ToString("F1")
                    + " health=" + mc.Health + " hunger=" + mc.Hunger
                    + " ts=" + mc.TimestampMs + " frame=" + mc.FrameCounter + " changed=" + changed);
            _lastMcX = mc.X;
            _lastMcY = mc.Y;
            _lastMcZ = mc.Z;
            _lastMcYaw = mc.Yaw;
            _lastMcPitch = mc.Pitch;
            _lastMcFrames = mc.FrameCounter;
        }

        /// <summary>
        /// Minecraft -> host: the How to Fish player is the *representation* of the authoritative
        /// Minecraft player. Its position is the fixed coordinate mapping of <c>McState</c> (the
        /// origin is computed once, then constant) — no per-frame re-anchoring. Movement goes through
        /// the game's own <c>PlayerMovement.Teleport</c> (Rigidbody-based), not a raw transform write.
        /// </summary>
        private void FollowMcPlayer()
        {
            if (!_config.PlayerFollow)
            {
                _followState = "DISABLED";
                _followSkip = "off";
                return;
            }

            long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

            if (!_memory.McAlive(now))
            {
                _followState = "NO_MC";
                _followSkip = "mcAlive=false (Minecraft not publishing)";
                return;
            }

            Player player = Player.LocalPlayer;

            if (!player)
            {
                _followState = "NO_HOST_PLAYER";
                _followSkip = "Player.LocalPlayer is null";
                return;
            }

            if (!player.Transform)
            {
                _followState = "NO_TRANSFORM";
                _followSkip = "host player transform is null";
                return;
            }

            DumpComponentsOnce(player);

            McState mc;

            try
            {
                mc = _memory.ReadMcState();
            }
            catch (Exception)
            {
                _followState = "NO_MC";
                _followSkip = "McState read error";
                return;
            }

            LogMcChange(mc);

            if ((mc.Flags & McState.InWorld) == 0)
            {
                _followState = "NO_MC_WORLD";
                _followSkip = "MC not in world (flags=" + mc.Flags + ")";
                return;
            }

            // Did last frame's requested position survive? (only meaningful if we wrote one)
            if (_hasPendingFollow)
            {
                Vector3 actual = HostPos(player);
                float err = Vector3.Distance(actual, _pendingFollowTarget);
                _followState = err > 0.1f ? "OVERRIDDEN_NEXT_FRAME" : "APPLIED";
                _hasPendingFollow = false;
            }

            _followSkip = "ok";
            Vector3 host = _mapper.ToHost(new Vector3((float)mc.X, (float)mc.Y, (float)mc.Z));
            Vector3 before = HostPos(player);
            string method;

            if (player.Movement != null)
            {
                player.Movement.Teleport(host, true);
                method = "PlayerMovement.Teleport";
            }
            else if (player.Rigidbody != null)
            {
                player.Rigidbody.position = host;
                method = "Rigidbody.position";
            }
            else
            {
                player.Transform.position = host;
                method = "Transform.position";
            }

            Vector3 after = HostPos(player);
            _pendingFollowTarget = host;
            _hasPendingFollow = true;

            if (++_followLogCounter % 30 == 1)
            {
                Logger.LogInfo("CrossMC follow: MC=(" + mc.X.ToString("F1") + "," + mc.Y.ToString("F1") + "," + mc.Z.ToString("F1")
                        + ") HostTarget=(" + host.x.ToString("F1") + "," + host.y.ToString("F1") + "," + host.z.ToString("F1")
                        + ") HostBefore=(" + before.x.ToString("F1") + "," + before.y.ToString("F1") + "," + before.z.ToString("F1")
                        + ") HostAfter=(" + after.x.ToString("F1") + "," + after.y.ToString("F1") + "," + after.z.ToString("F1")
                        + ") method=" + method + " nextState=" + _followState);
            }

            _followMoves++;

            if (_config.PlayerFollowRotation)
            {
                player.Transform.rotation = Quaternion.Euler(mc.Pitch, mc.Yaw, 0f);
            }
        }

        private void FollowVitals()
        {
            if (!_config.FollowVitals || !_memory.McAlive(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()))
            {
                return;
            }

            Player player = Player.LocalPlayer;

            if (!player || player.Vitals == null)
            {
                return;
            }

            McState mc;

            try
            {
                mc = _memory.ReadMcState();
            }
            catch (Exception)
            {
                return;
            }

            if ((mc.Flags & McState.InWorld) == 0)
            {
                return;
            }

            ApplyVitals(mc, player);
        }

        /// <summary>
        /// Minecraft health/hunger -> host player (Minecraft owns these). Best-effort: health via
        /// Heal/TakeDamage deltas, hunger via RestoreFullness. Marked for in-game verification.
        /// </summary>
        private void ApplyVitals(McState mc, Player player)
        {
            if (!_config.FollowVitals || player.Vitals == null)
            {
                return;
            }

            var vitals = player.Vitals;
            int healthDelta = mc.Health - vitals.Health;

            if (healthDelta > 0)
            {
                vitals.Heal(healthDelta);
            }
            else if (healthDelta < 0 && player.Transform != null)
            {
                vitals.TakeDamage(-healthDelta, Vector3.zero, player.Transform.position, false);
            }

            int hungerDelta = mc.Hunger - vitals.Fullness;

            if (hungerDelta > 0)
            {
                vitals.RestoreFullness(hungerDelta);
            }
        }

        private static Camera FindCamera()
        {
            Camera[] cams = Camera.allCameras;

            if (cams != null && cams.Length > 0)
            {
                return cams[0];
            }

            return null;
        }

        private void ExportColliders()
        {
            Player player = Player.LocalPlayer;
            Vector3 center;

            if (player && player.Transform)
            {
                center = player.Transform.position;
            }
            else
            {
                // No local player yet (lobby): still export around the active camera so the host
                // world does not vanish just because the player is missing.
                Camera cam = FindCamera();

                if (cam == null)
                {
                    _colliders.Clear();
                    _memory.WriteColliderTable(_colliders);
                    return;
                }

                center = cam.transform.position;
            }

            _colliders.Clear();

            int count = Physics.OverlapSphereNonAlloc(center, _config.ColliderRadius, _overlapBuffer);
            int id = 1;

            for (int i = 0; i < count && _colliders.Count < _config.ColliderMax; i++)
            {
                UnityEngine.Collider col = _overlapBuffer[i];

                if (col == null || col.isTrigger)
                {
                    continue;
                }

                Bounds b = col.bounds;
                Vector3 c = _mapper.ToMc(b.center);
                float s = _config.Scale;
                _colliders.Add(new BridgeCollider
                {
                    Id = id++,
                    Type = BridgeProtocol.ColliderBox,
                    Flags = BridgeProtocol.ColliderEnabled,
                    CenterX = c.x,
                    CenterY = c.y,
                    CenterZ = c.z,
                    HalfX = b.extents.x * s,
                    HalfY = b.extents.y * s,
                    HalfZ = b.extents.z * s,
                });
            }

            _memory.WriteColliderTable(_colliders);
        }

        private void ExportEntities()
        {
            Player player = Player.LocalPlayer;
            bool haveCenter = player && player.Transform;
            Vector3 center = haveCenter ? player.Transform.position : Vector3.zero;

            _entities.Clear();
            _crossToCreature.Clear();

            Creature boss = BossManager.Boss;
            Creature[] creatures = UnityEngine.Object.FindObjectsByType<Creature>(FindObjectsInactive.Exclude);

            foreach (Creature creature in creatures)
            {
                if (creature == null || creature.IsDead || creature.NetworkObject == null)
                {
                    continue;
                }

                // Without a local player (lobby) skip the proximity filter so host entities still
                // get exported; with a player, only export nearby ones.
                if (haveCenter && Vector3.Distance(creature.transform.position, center) > _config.EntityRadius)
                {
                    continue;
                }

                int hostId = creature.NetworkObject.ObjectId;

                if (hostId == 0)
                {
                    continue;
                }

                if (!_hostToCross.TryGetValue(hostId, out int crossId))
                {
                    crossId = ++_nextCrossId;
                    _hostToCross[hostId] = crossId;
                }

                Vector3 p = _mapper.ToMc(creature.transform.position);
                bool isBoss = boss == creature;
                _entities.Add(new EntityMap
                {
                    HostEntityId = hostId,
                    CrossEntityId = crossId,
                    Kind = isBoss ? BridgeProtocol.EntityBoss : BridgeProtocol.EntityCreature,
                    Flags = isBoss ? BridgeProtocol.EntityBossFlag : 0,
                    X = p.x,
                    Y = p.y,
                    Z = p.z,
                    Yaw = creature.transform.eulerAngles.y,
                    Health = creature.Hp,
                    MaxHealth = creature.MaxHp,
                });
                _crossToCreature[crossId] = creature;

                if (_entities.Count >= BridgeProtocol.EntityCapacity)
                {
                    break;
                }
            }

            _memory.WriteEntityTable(_entities);
        }

        private void ApplyDamage()
        {
            for (int guard = 0; guard < 64; guard++)
            {
                DamageEvent d = _memory.PollDamage();

                if (d == null)
                {
                    break;
                }

                float multiplier = _config.DamageMultiplier(d.SourceType);
                int scaled = Mathf.Max(0, Mathf.RoundToInt(d.Amount * multiplier));
                Logger.LogInfo("CrossMC damage: cross=" + d.CrossEntityId + " mc=" + d.McEntityId
                        + " type=" + d.SourceType + " raw=" + d.Amount + " x" + multiplier + " => " + scaled);
                ApplyToHostEntity(d.CrossEntityId, scaled, d);
            }
        }

        private void ApplyToHostEntity(int crossId, int amount, DamageEvent d)
        {
            if (amount <= 0)
            {
                return;
            }

            if (_crossToCreature.TryGetValue(crossId, out Creature creature) && creature != null)
            {
                ApplyCreatureDamage(creature, amount, d);
                return;
            }

            // No bound creature (e.g. the mapping targets the player): apply to the local player.
            Player player = Player.LocalPlayer;

            if (player && player.Vitals != null)
            {
                Vector3 hit = new Vector3(d.X, d.Y, d.Z);
                player.Vitals.TakeDamage(amount, Vector3.zero, hit, false);
                Logger.LogInfo("CrossMC applied " + amount + " damage to the local player");
            }
        }

        private void ApplyCreatureDamage(Creature creature, int amount, DamageEvent d)
        {
            // The real creature damage entry point (LocalHit) has a long gameplay-specific
            // signature that has NOT been verified against the current build. Best effort: find it
            // and invoke with mapped arguments; any failure is logged, never hidden.
            try
            {
                MethodInfo hit = null;

                foreach (MethodInfo m in creature.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance))
                {
                    if (m.Name == "LocalHit")
                    {
                        hit = m;
                        break;
                    }
                }

                if (hit == null)
                {
                    Logger.LogWarning("CrossMC: no creature damage method found; dropped " + amount + " damage");
                    return;
                }

                hit.Invoke(creature, BuildHitArgs(hit, creature, amount, d));
                Logger.LogInfo("CrossMC invoked creature damage (" + amount + ") on host entity");
            }
            catch (Exception e)
            {
                Logger.LogWarning("CrossMC creature damage failed: " + e.Message);
            }
        }

        private static object[] BuildHitArgs(MethodInfo method, Creature creature, int amount, DamageEvent d)
        {
            ParameterInfo[] ps = method.GetParameters();
            object[] args = new object[ps.Length];
            Player player = Player.LocalPlayer;

            for (int i = 0; i < ps.Length; i++)
            {
                Type t = ps[i].ParameterType;

                if (t == typeof(Transform))
                {
                    args[i] = player && player.Transform ? player.Transform : creature.transform;
                }
                else if (t == typeof(Vector3))
                {
                    args[i] = new Vector3(d.X, d.Y, d.Z);
                }
                else if (t == typeof(Player))
                {
                    args[i] = player;
                }
                else if (t == typeof(int))
                {
                    args[i] = amount;
                }
                else if (t == typeof(float))
                {
                    args[i] = (float)amount;
                }
                else if (t == typeof(bool))
                {
                    args[i] = false;
                }
                else
                {
                    args[i] = t.IsValueType ? Activator.CreateInstance(t) : null;
                }
            }

            return args;
        }

        private void OnDestroy()
        {
            try
            {
                _memory?.Dispose();
            }
            catch (Exception)
            {
                // ignore
            }

            _memory = null;
        }
    }
}
