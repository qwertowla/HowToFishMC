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

                Logger.LogInfo("CrossMC host ready. config=" + global::CrossMC.Bridge.Config.ConfigSource());
            }
            catch (Exception e)
            {
                Logger.LogError("CrossMC failed to open shared memory: " + e);
                _memory = null;
            }
        }

        private void Update()
        {
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

            _statusTimer -= dt;

            if (_statusTimer <= 0f)
            {
                _statusTimer = 2f;
                long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                Logger.LogInfo("CrossMC status: hostAlive=" + _memory.HostAlive(now)
                        + " mcAlive=" + _memory.McAlive(now)
                        + " inputEvents=" + _inputEvents
                        + " follow=" + _config.PlayerFollow
                        + " followMoves=" + _followMoves
                        + " entities=" + _entities.Count
                        + " colliders=" + _colliders.Count);
            }
        }

        /// <summary>
        /// Host camera follows the Minecraft player's view (McState yaw/pitch). Runs in LateUpdate so
        /// it wins over the game's own camera update for the rendered frame; the host camera remains
        /// purely a follower (Minecraft is the view authority).
        /// </summary>
        private void LateUpdate()
        {
            if (_memory == null || !_config.FollowCamera)
            {
                return;
            }

            if (!_memory.McAlive(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()))
            {
                return;
            }

            Player player = Player.LocalPlayer;

            if (!player || !player.CamObject)
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

            float yaw = mc.Yaw * _config.CameraYawSign;
            float pitch = mc.Pitch * _config.CameraPitchSign;
            player.CamObject.rotation = Quaternion.Euler(pitch, yaw, 0f);

            if (_cameraFollows == 0)
            {
                Logger.LogInfo("CrossMC: camera follow active (yawSign=" + _config.CameraYawSign
                        + " pitchSign=" + _config.CameraPitchSign
                        + "). Flip the signs in host.properties if the view is mirrored/inverted.");
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
                return;
            }

            long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

            if (!_memory.McAlive(now))
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

            if ((mc.Flags & McState.InWorld) == 0)
            {
                return;
            }

            Vector3 host = _mapper.ToHost(new Vector3((float)mc.X, (float)mc.Y, (float)mc.Z));

            if (player.Movement != null)
            {
                player.Movement.Teleport(host, true);
            }
            else if (player.Rigidbody != null)
            {
                player.Rigidbody.position = host;
            }
            else
            {
                player.Transform.position = host;
            }

            if (_followMoves == 0)
            {
                Logger.LogInfo("CrossMC: following McState -> host player (fixed origin) at "
                        + host.x.ToString("F2") + "," + host.y.ToString("F2") + "," + host.z.ToString("F2")
                        + " via PlayerMovement.Teleport.");
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

        private void ExportColliders()
        {
            Player player = Player.LocalPlayer;

            if (!player || !player.Transform)
            {
                return;
            }

            Vector3 center = player.Transform.position;
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

            if (!player || !player.Transform)
            {
                return;
            }

            Vector3 center = player.Transform.position;
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

                if (Vector3.Distance(creature.transform.position, center) > _config.EntityRadius)
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
