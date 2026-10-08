using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
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
        private bool _mappingLocked;
        private string _mappingMode = "init";
        private long _cameraFollows;
        private string _followSkip = "off";
        private bool _lastMcAlive;

        // One-time player bootstrap (see UpdatePlayerBootstrap). _bootConfirmed gates the normal
        // MC -> host follow so a freshly-loaded Minecraft player can never pull the host player.
        // Defaults to FALSE: the follower stays gated until a bootstrap is explicitly confirmed.
        private bool _bootConfirmed;
        private string _bootState = "OFF";
        private int _bootSeq;
        private int _bootSeqCounter;
        private bool _bootTargetSet;
        private Vector3 _bootTargetMc;
        private bool _lastMcInWorld;
        private long _bootLogMs;
        private long _bootStartMs;
        private bool _confirmedDoneSeen;      // observed MC BOOTSTRAP_DONE for the current session
        private double _mcInitialX, _mcInitialY, _mcInitialZ; // MC position when the session was detected
        private string _lastFollowerSkipLog = "";

        // Identifies the actual loaded build in the BepInEx log (deployment verification).
        public const string BuildTag = "bootstrap+fixed-mapping 2026-10-08";

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
        private int _colDetected;
        private int _colPlayerSkipped;
        private int _colExported;
        private Player _followerPlayer;
        private bool _followerTookOver;
        private bool _savedMovementEnabled;
        private bool _savedKinematic;
        private bool _savedUseGravity;
        private string _localPlayerSource = "-";



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
            Logger.LogInfo("CrossMC HowToFish build=" + BuildTag
                    + " assembly=" + typeof(Plugin).Assembly.GetName().Version
                    + " dllWritten=" + SafeWriteTime(typeof(Plugin).Assembly.Location));
            Logger.LogInfo("CrossMC hostConfig source=" + HostConfig.ConfigSource());

            string userCfg = HostConfig.UserOverridePath();

            if (File.Exists(userCfg))
            {
                Logger.LogWarning("CrossMC: user config overrides host.properties: " + userCfg);
            }

            _config = HostConfig.Load();
            _mapper = new CoordinateMapper(_config);
            Logger.LogInfo("CrossMC effective config: " + _config.Describe());
            InitMapping();

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

                // Host -> Minecraft video: capture the How to Fish camera into the CrossMC host frame.
                var exporter = go.AddComponent<HostFrameExporter>();
                exporter.Init(_memory, _config);
                Logger.LogInfo("CrossMC HostFrame exporter " + (_config.RenderEnabled ? "enabled" : "disabled")
                        + " (" + _config.RenderWidth + "x" + _config.RenderHeight + "@" + _config.RenderFps + "fps)");

                Logger.LogInfo("CrossMC host ready. bridgeConfig=" + global::CrossMC.Bridge.Config.ConfigSource()
                        + " hostConfig=" + HostConfig.ConfigSource());
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

            // One-time player bootstrap: while a Minecraft world is loading we must NOT let the
            // freshly-loaded Minecraft player drive the host follower. Compute/confirm the
            // alignment first; PublishHostEnvironment advertises the bootstrap target to Minecraft.
            try
            {
                UpdatePlayerBootstrap();
            }
            catch (Exception e)
            {
                Logger.LogError("CrossMC bootstrap update failed: " + e);
            }

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

            // Minecraft -> host: diagnose McState + mirror health/hunger. Position/camera follow runs
            // in LateUpdate (see FollowerLateUpdate), after the host's own movement pass.
            try
            {
                DiagnoseMc();
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
                Player hp = FindLocalPlayer();
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
                        + " localPlayerSource=" + _localPlayerSource
                        + " | mapping=" + _mappingMode
                        + " mappingLocked=" + _mappingLocked
                        + " anchored=" + _anchored
                        + " bootState=" + _bootState
                        + " bootConfirmed=" + _bootConfirmed
                        + " follow=" + _config.PlayerFollow
                        + " followState=" + _followState
                        + " followSkip=" + _followSkip
                        + " followMoves=" + _followMoves
                        + " cameraState=" + _cameraState
                        + " cameraFollows=" + _cameraFollows
                        + " entities=" + _entities.Count
                        + " colliders=" + _colliders.Count
                        + " (detected=" + _colDetected + " playerSkipped=" + _colPlayerSkipped + " exported=" + _colExported + ")");
            }
        }

        /// <summary>
        /// Formal player follower takeover. The host player is a REPRESENTATION of the authoritative
        /// Minecraft player, so we stop the host's own movement authority for that player:
        ///   - disable <c>PlayerMovement</c> (the local movement simulation),
        ///   - make the <c>Rigidbody</c> kinematic (no physics),
        ///   - disable any FishNet transform sync on this player (typed, not by name).
        /// Everything is saved and restored when follow is turned off. This is the minimal, stable set
        /// that actually owns the position in this build (verified at runtime). <c>player.followHardLock</c>
        /// only adds an aggressive fallback scan if the formal takeover is ever insufficient.
        /// </summary>
        private void EnsureFollowerTakeover(Player player)
        {
            if (_followerPlayer != player)
            {
                RestoreFollower();
                _followerPlayer = player;
                _followerTookOver = false;
            }

            if (!_followerTookOver)
            {
                _followerTookOver = true;

                if (player.Movement != null)
                {
                    _savedMovementEnabled = player.Movement.enabled;
                    player.Movement.enabled = false;
                }

                if (player.Rigidbody != null)
                {
                    _savedKinematic = player.Rigidbody.isKinematic;
                    _savedUseGravity = player.Rigidbody.useGravity;
                    player.Rigidbody.isKinematic = true;
                    player.Rigidbody.useGravity = false;
                    player.Rigidbody.linearVelocity = Vector3.zero;
                }

                DisableTransformSync(player);
                Logger.LogInfo("CrossMC follower: took over host player (PlayerMovement off, Rigidbody kinematic)");
            }

            if (_config.FollowHardLock)
            {
                AggressiveDisable(player);
            }
        }

        /// <summary>Disable FishNet transform-sync components on the player (typed, not by name).</summary>
        private static void DisableTransformSync(Player player)
        {
            foreach (FishNet.Component.Transforming.NetworkTransform nt
                    in player.GetComponentsInChildren<FishNet.Component.Transforming.NetworkTransform>(true))
            {
                if (nt != null)
                {
                    nt.enabled = false;
                }
            }

            foreach (RigidbodySync rs in player.GetComponentsInChildren<RigidbodySync>(true))
            {
                if (rs != null)
                {
                    rs.enabled = false;
                }
            }
        }

        /// <summary>Emergency fallback (hardlock): blanket-disable anything that looks like a sync component.</summary>
        private void AggressiveDisable(Player player)
        {
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
                    if (mb.enabled)
                    {
                        mb.enabled = false;
                        Logger.LogInfo("CrossMC hardlock: disabled " + n);
                    }
                }
            }
        }

        private void RestoreFollower()
        {
            if (!_followerTookOver || _followerPlayer == null)
            {
                return;
            }

            Player p = _followerPlayer;

            if (p.Movement != null)
            {
                p.Movement.enabled = _savedMovementEnabled;
            }

            if (p.Rigidbody != null)
            {
                p.Rigidbody.isKinematic = _savedKinematic;
                p.Rigidbody.useGravity = _savedUseGravity;
            }

            _followerTookOver = false;
            _followerPlayer = null;
        }

        /// <summary>
        /// Formal follower update (LateUpdate, after the host's own movement pass): place the host
        /// player at the fixed CoordinateMapper position of the authoritative McState and mirror the
        /// camera. This is the Minecraft -> How to Fish chain; it never writes to Minecraft.
        /// </summary>
        private void FollowerLateUpdate()
        {
            if (!_config.PlayerFollow)
            {
                RestoreFollower();
                _followState = "DISABLED";
                _followSkip = "off";
                return;
            }

            if (_memory == null)
            {
                return;
            }

            Player player = FindLocalPlayer();

            if (!player || !player.Transform)
            {
                _followState = "NO_HOST_PLAYER";
                _followSkip = player ? "host player transform is null" : "Player.LocalPlayer is null";
                return;
            }

            DumpComponentsOnce(player);
            EnsureFollowerTakeover(player);

            if (!_bootConfirmed)
            {
                // The host player is frozen at its current position; do NOT let the freshly-loaded
                // Minecraft save position drive it. Camera/vitals also wait (see LateUpdate/FollowVitals).
                _followState = "BOOTSTRAP";
                _followSkip = "player bootstrap in progress (host not following yet)";

                if (_lastFollowerSkipLog != _bootState)
                {
                    _lastFollowerSkipLog = _bootState;
                    Logger.LogInfo("FollowerLateUpdate: SKIP — bootstrap state=" + _bootState + " (not writing HOF player/camera)");
                }

                return;
            }

            if (!_memory.McAlive(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()))
            {
                _followState = "NO_MC";
                _followSkip = "mcAlive=false (Minecraft not publishing)";
                return;
            }

            McState mc;

            try
            {
                mc = _memory.ReadMcState();
            }
            catch (Exception)
            {
                _followState = "NO_MC";
                return;
            }

            if ((mc.Flags & McState.InWorld) == 0)
            {
                _followState = "NO_MC_WORLD";
                _followSkip = "MC not in world (flags=" + mc.Flags + ")";
                return;
            }

            _followSkip = "ok";
            Vector3 host = _mapper.ToHost(new Vector3((float)mc.X, (float)mc.Y, (float)mc.Z));

            if (_hasPendingFollow)
            {
                float err = Vector3.Distance(HostPos(player), _pendingFollowTarget);
                _followState = err > 0.1f ? "OVERRIDDEN_NEXT_FRAME" : "APPLIED";
                _hasPendingFollow = false;
            }
            else
            {
                _followState = "APPLIED";
            }

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

            _pendingFollowTarget = host;
            _hasPendingFollow = true;

            if (++_followLogCounter % 60 == 1)
            {
                Logger.LogInfo("CrossMC follower: MC=(" + mc.X.ToString("F1") + "," + mc.Y.ToString("F1") + "," + mc.Z.ToString("F1")
                        + ") -> Host=(" + host.x.ToString("F1") + "," + host.y.ToString("F1") + "," + host.z.ToString("F1")
                        + ") nextState=" + _followState + " hardlock=" + _config.FollowHardLock);
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
            FollowerLateUpdate();

            if (!_bootConfirmed)
            {
                // Do not let the freshly-loaded Minecraft yaw/pitch drive the host camera either.
                _cameraState = "BOOTSTRAP";
                return;
            }

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

            Player player = FindLocalPlayer();
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
        /// Establishes the FIXED world mapping before any Minecraft world is involved.
        ///
        /// <p>Formal mode (<c>transform.autoAnchor=false</c>): the mapping comes from the explicit
        /// <c>transform.origin*</c>/scale/flipX config and is locked immediately; the Minecraft save
        /// position is never used to define it.</p>
        ///
        /// <p>Dev mode (<c>transform.autoAnchor=true</c>): reuse a previously established mapping
        /// (persisted) if present; otherwise leave it unlocked and establish it ONCE on the first
        /// aligned player pair, then lock and persist it. It is never re-anchored afterwards — not on
        /// a new Minecraft world, a save reload, a reconnect or a host restart.</p>
        /// </summary>
        private void InitMapping()
        {
            if (!_config.AutoAnchor)
            {
                _mappingLocked = true;
                _mappingMode = "explicit";
                Logger.LogInfo("CrossMC mapping: EXPLICIT (formal) origin=("
                        + _mapper.OriginX.ToString("F3") + "," + _mapper.OriginY.ToString("F3") + "," + _mapper.OriginZ.ToString("F3")
                        + ") scale=" + _mapper.Scale.ToString("F3") + " flipX=" + _mapper.FlipX
                        + " — locked; the Minecraft save position never affects it");
                return;
            }

            if (MappingStore.TryLoad(out float ox, out float oy, out float oz, out float scale, out bool flipX))
            {
                _mapper.OriginX = ox;
                _mapper.OriginY = oy;
                _mapper.OriginZ = oz;
                _mapper.Scale = scale == 0f ? 1f : scale;
                _mapper.FlipX = flipX;
                _mappingLocked = true;
                _anchored = true;
                _mappingMode = "auto-persisted";
                Logger.LogInfo("CrossMC mapping: AUTO-ANCHOR reused persisted origin=("
                        + ox.ToString("F3") + "," + oy.ToString("F3") + "," + oz.ToString("F3")
                        + ") scale=" + scale.ToString("F3") + " flipX=" + flipX + " — locked");
                return;
            }

            _mappingLocked = false;
            _mappingMode = "auto-pending";
            Logger.LogWarning("CrossMC mapping: autoAnchor is a DEV mode — it will anchor once from the "
                    + "current players. Prefer transform.autoAnchor=false with an explicit world mapping.");
        }

        /// <summary>
        /// Establishes the mapping once, only in the dev <c>autoAnchor</c> mode. It requires an
        /// aligned player pair, then locks and persists the mapping so it is never recomputed. In
        /// formal (explicit) mode this does nothing: the mapping is fixed from config.
        /// </summary>
        private void EnsureAnchor()
        {
            if (_mappingLocked)
            {
                return;
            }

            if (!_config.AutoAnchor)
            {
                _mappingLocked = true;
                _mappingMode = "explicit";
                return;
            }

            if (!_memory.McAlive(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()))
            {
                return;
            }

            Player player = FindLocalPlayer();

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
            _mappingLocked = true;
            _anchored = true;
            _mappingMode = "auto-established";
            MappingStore.Save(_mapper.OriginX, _mapper.OriginY, _mapper.OriginZ, _mapper.Scale, _mapper.FlipX);
            Logger.LogInfo("CrossMC: auto-anchored host(" + hp.x.ToString("F1") + "," + hp.y.ToString("F1") + "," + hp.z.ToString("F1")
                    + ") <-> mc(" + mc.X.ToString("F1") + "," + mc.Y.ToString("F1") + "," + mc.Z.ToString("F1")
                    + "); mapping LOCKED and persisted — it will not be re-anchored on world change (dev mode).");
        }

        /// <summary>
        /// One-time player bootstrap handshake. When a Minecraft world/session becomes ready, the
        /// host computes the target MC position of its current player through the fixed
        /// CoordinateMapper (never re-anchored) and asks Minecraft to align there once
        /// (<c>HostState.Bootstrap</c> + <c>teleportSeq</c>). Until confirmed, the normal
        /// MC -&gt; host follow is gated off, so a freshly-loaded Minecraft save position can never
        /// drag the host player. After confirmation, authority is Minecraft's again.
        /// </summary>
        private void UpdatePlayerBootstrap()
        {
            if (!_config.PlayerBootstrap)
            {
                _bootConfirmed = true;
                SetBootState("OFF");
                return;
            }

            long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

            if (!_memory.McAlive(now))
            {
                SetBootState("WAIT_MC");
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

            bool inWorld = (mc.Flags & McState.InWorld) != 0;
            bool mcDone = (mc.Flags & McState.BootstrapDone) != 0;

            if (!inWorld)
            {
                if (_lastMcInWorld)
                {
                    Logger.LogInfo("CrossMC bootstrap: Minecraft world unloaded — host player stays put");
                }

                _lastMcInWorld = false;
                _bootConfirmed = false;
                _bootTargetSet = false;
                _bootSeq = 0;
                _confirmedDoneSeen = false;
                SetBootState("WAIT_MC_WORLD");
                return;
            }

            // MC world/session detection. The primary trigger is the MC-owned BOOTSTRAP_DONE flag:
            // Minecraft clears it whenever a new ClientWorld loads. This is reliable even if the host
            // never samples the brief "world == null" window (unlike an IN_WORLD edge).
            if (!_lastMcInWorld)
            {
                _lastMcInWorld = true;
                ArmBootstrap(mc, now, "world loaded");
            }

            if (_bootConfirmed)
            {
                if (mcDone)
                {
                    _confirmedDoneSeen = true;
                }
                else if (_confirmedDoneSeen)
                {
                    // MC cleared BOOTSTRAP_DONE => it entered a new world session. Re-arm.
                    ArmBootstrap(mc, now, "new session (BOOTSTRAP_DONE cleared)");
                }
                else
                {
                    // Short grace right after our own confirmation, before MC publishes DONE.
                    SetBootState("FOLLOW_ACTIVE");
                    return;
                }
            }

            // --- Bootstrap in progress (_bootConfirmed == false) ---
            Player hp = FindLocalPlayer();

            if (!hp || !hp.Transform)
            {
                SetBootState("WAIT_HOST");
                LogBootstrapThrottled("waiting for host player");
                return;
            }

            if (!_bootTargetSet)
            {
                Vector3 hpos = hp.Transform.position;
                _bootTargetMc = _mapper.ToMc(hpos);
                _bootSeq = ++_bootSeqCounter;

                if (_bootSeq == 0)
                {
                    _bootSeq = ++_bootSeqCounter;
                }

                _bootTargetSet = true;
                Logger.LogInfo("CrossMC bootstrap: target HOF=(" + F(hpos.x) + "," + F(hpos.y) + "," + F(hpos.z)
                        + ") MC=(" + F(_bootTargetMc.x) + "," + F(_bootTargetMc.y) + "," + F(_bootTargetMc.z)
                        + ") | mcInitial=(" + F(_mcInitialX) + "," + F(_mcInitialY) + "," + F(_mcInitialZ)
                        + ") initialDistance=" + F(Distance((float)_mcInitialX, (float)_mcInitialY, (float)_mcInitialZ, _bootTargetMc))
                        + " mappingMode=" + _mappingMode + " autoAnchor=" + _config.AutoAnchor);
            }

            float distance = Distance((float)mc.X, (float)mc.Y, (float)mc.Z, _bootTargetMc);
            SetBootState("WAIT_CONFIRM");

            if ((mcDone && distance < 3f) || distance < 1.5f)
            {
                _bootConfirmed = true;
                _confirmedDoneSeen = mcDone;
                SetBootState("FOLLOW_ACTIVE");
                Logger.LogInfo("CrossMC bootstrap: completed (distance=" + F(distance)
                        + ") — player authority switched to Minecraft");
            }
            else if (now - _bootStartMs > 10000)
            {
                // Safety valve: never leave follow/camera frozen forever if Minecraft can't align
                // (e.g. a remote server that rejects the teleport). Fall back to Minecraft authority.
                _bootConfirmed = true;
                _confirmedDoneSeen = mcDone;
                Logger.LogWarning("CrossMC bootstrap: timed out waiting for confirmation (distance=" + F(distance)
                        + ") — falling back to Minecraft authority");
                SetBootState("FOLLOW_ACTIVE");
            }
            else
            {
                LogBootstrapThrottled("state=WAIT_CONFIRM distance=" + F(distance));
            }
        }

        /// <summary>Arms a fresh one-time bootstrap for the current MC world session.</summary>
        private void ArmBootstrap(McState mc, long now, string reason)
        {
            _bootConfirmed = false;
            _bootTargetSet = false;
            _bootSeq = 0;
            _bootStartMs = now;
            _confirmedDoneSeen = false;
            _mcInitialX = mc.X;
            _mcInitialY = mc.Y;
            _mcInitialZ = mc.Z;
            SetBootState("WAIT_HOST");
            Logger.LogInfo("CrossMC bootstrap: armed (" + reason + ") — mcInitial=("
                    + F(mc.X) + "," + F(mc.Y) + "," + F(mc.Z) + ") mappingMode=" + _mappingMode
                    + " mappingLocked=" + _mappingLocked + " autoAnchor=" + _config.AutoAnchor);
        }

        /// <summary>Logs a bootstrap state transition once (never per frame).</summary>
        private void SetBootState(string state)
        {
            if (_bootState == state)
            {
                return;
            }

            _bootState = state;
            Logger.LogInfo("CrossMC bootstrap: state=" + state
                    + " mappingMode=" + _mappingMode
                    + " mappingLocked=" + _mappingLocked
                    + " autoAnchor=" + _config.AutoAnchor
                    + " bootstrapCfg=" + _config.PlayerBootstrap
                    + " bootConfirmed=" + _bootConfirmed);
        }

        private static float Distance(float x, float y, float z, Vector3 t)
        {
            float dx = x - t.x;
            float dy = y - t.y;
            float dz = z - t.z;
            return Mathf.Sqrt(dx * dx + dy * dy + dz * dz);
        }

        private static string F(float v)
        {
            return v.ToString("F2");
        }

        private static string F(double v)
        {
            return v.ToString("F2");
        }

        private static string SafeWriteTime(string path)
        {
            try
            {
                return File.GetLastWriteTime(path).ToString("yyyy-MM-dd HH:mm:ss");
            }
            catch (Exception)
            {
                return "unknown";
            }
        }

        private void LogBootstrapThrottled(string message)
        {
            long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

            if (now - _bootLogMs < 1000)
            {
                return;
            }

            _bootLogMs = now;
            Logger.LogInfo("CrossMC bootstrap: " + message);
        }

        /// <summary>
        /// Publishes the host's OWN environment/avatar as <c>HostState</c>. This is informational
        /// only: the Minecraft player is authoritative and this is never used to drive it. The host
        /// -> Minecraft player channel is <c>InputRing</c> (see <see cref="CaptureInput"/>).
        /// </summary>
        private void PublishHostEnvironment()
        {
            Player player = FindLocalPlayer();
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

            // Bootstrap request: advertise the one-time alignment target + generation until Minecraft
            // confirms. Minecraft teleports to (PosX,PosY,PosZ) once per new teleportSeq.
            if (_config.PlayerBootstrap && !_bootConfirmed && _bootTargetSet)
            {
                state.Flags |= HostState.Bootstrap;
                state.TeleportSeq = _bootSeq;
                state.PosX = _bootTargetMc.x;
                state.PosY = _bootTargetMc.y;
                state.PosZ = _bootTargetMc.z;
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

        /// <summary>
        /// The single local-player accessor. Prefers <c>Player.LocalPlayer</c>; if it is null, falls
        /// back to <c>PlayerManager.Players</c> and picks the player whose FishNet owner is the local
        /// client. Logs which path succeeded (once per change).
        /// </summary>
        private Player FindLocalPlayer()
        {
            Player direct = Player.LocalPlayer;

            if (direct)
            {
                NoteLocalPlayerSource("Player.LocalPlayer");
                return direct;
            }

            var players = PlayerManager.Players;

            if (players != null)
            {
                foreach (Player candidate in players)
                {
                    if (!candidate)
                    {
                        continue;
                    }

                    var no = candidate.NetworkObject;

                    if (no != null && no.Owner != null && no.Owner.IsLocalClient)
                    {
                        NoteLocalPlayerSource("PlayerManager.Players owner.IsLocalClient");
                        return candidate;
                    }
                }
            }

            _localPlayerSource = "-";
            return null;
        }

        private void NoteLocalPlayerSource(string source)
        {
            if (_localPlayerSource != source)
            {
                _localPlayerSource = source;
                Logger.LogInfo("CrossMC: resolved local player from " + source);
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

            var c3 = new System.Text.StringBuilder("CrossMC: player colliders: ");

            foreach (UnityEngine.Collider col in player.GetComponentsInChildren<UnityEngine.Collider>(true))
            {
                if (col == null)
                {
                    continue;
                }

                c3.Append('[').Append(Path(col.transform)).Append(':').Append(col.GetType().Name);
                c3.Append(col.isTrigger ? "/trigger" : "").Append("] ");
            }

            Logger.LogInfo(c3.ToString());
        }

        private static string Path(Transform t)
        {
            var sb = new System.Text.StringBuilder(t.name);

            while (t.parent != null)
            {
                t = t.parent;
                sb.Insert(0, t.name + "/");
            }

            return sb.ToString();
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
        /// Reads McState each frame for diagnostics (change tracking + validity). The actual position
        /// follow happens in LateUpdate (see FollowerLateUpdate); this never writes to Minecraft.
        /// </summary>
        private void DiagnoseMc()
        {
            if (_memory == null || !_memory.McAlive(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()))
            {
                return;
            }

            try
            {
                LogMcChange(_memory.ReadMcState());
            }
            catch (Exception)
            {
                // ignore
            }
        }

        private void FollowVitals()
        {
            if (!_config.FollowVitals || !_memory.McAlive(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()))
            {
                return;
            }

            if (!_bootConfirmed)
            {
                return; // wait until the player bootstrap is confirmed before mirroring health/hunger
            }

            Player player = FindLocalPlayer();

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
            Player player = FindLocalPlayer();
            Transform playerTf = player && player.Transform ? player.Transform : null;
            Rigidbody playerRb = player ? player.Rigidbody : null;
            Vector3 center;

            if (playerTf != null)
            {
                center = playerTf.position;
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
                    _colDetected = 0;
                    _colPlayerSkipped = 0;
                    _colExported = 0;
                    return;
                }

                center = cam.transform.position;
            }

            _colliders.Clear();

            int count = Physics.OverlapSphereNonAlloc(center, _config.ColliderRadius, _overlapBuffer);
            int id = 1;
            int skipped = 0;

            for (int i = 0; i < count && _colliders.Count < _config.ColliderMax; i++)
            {
                UnityEngine.Collider col = _overlapBuffer[i];

                if (col == null || col.isTrigger)
                {
                    continue;
                }

                // The local host player is the follower representation of the Minecraft player; its
                // own body colliders must NOT become Minecraft world obstacles. Exclude the player's
                // whole hierarchy and anything sharing the player's Rigidbody. Other colliders
                // (monsters, NPCs, interactables, buildings) are still exported.
                if (IsLocalPlayerCollider(col, playerTf, playerRb))
                {
                    skipped++;
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

            _colDetected = count;
            _colPlayerSkipped = skipped;
            _colExported = _colliders.Count;
            _memory.WriteColliderTable(_colliders);
        }

        /** True when a collider belongs to the local host player (its hierarchy or Rigidbody). */
        private static bool IsLocalPlayerCollider(UnityEngine.Collider col, Transform playerTf, Rigidbody playerRb)
        {
            if (playerRb != null && col.attachedRigidbody == playerRb)
            {
                return true;
            }

            if (playerTf != null)
            {
                Transform t = col.transform;

                if (t == playerTf || t.IsChildOf(playerTf))
                {
                    return true;
                }
            }

            return false;
        }

        private void ExportEntities()
        {
            Player player = FindLocalPlayer();
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
            Player player = FindLocalPlayer();

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

        private object[] BuildHitArgs(MethodInfo method, Creature creature, int amount, DamageEvent d)
        {
            ParameterInfo[] ps = method.GetParameters();
            object[] args = new object[ps.Length];
            Player player = FindLocalPlayer();

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
