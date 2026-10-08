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
        /// One-time alignment: makes the host player's current position and the Minecraft player's
        /// current position denote the same point, so host &lt;-&gt; MC movement is relative and
        /// neither player is yanked to a foreign coordinate. Requires <c>transform.autoAnchor</c>.
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
                if (!_loggedInputUnavailable)
                {
                    _loggedInputUnavailable = true;
                    Logger.LogWarning("CrossMC: Unity Input System reports no Keyboard/Mouse device. "
                            + "Host input will not be captured (is How to Fish focused / using the new Input System?).");
                }

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
        /// Minecraft -> host: drive the How to Fish player to follow the authoritative Minecraft
        /// player (<c>McState</c>) through the coordinate mapper. Enabled by <c>player.follow</c>;
        /// this only ever writes the HOST transform, never the Minecraft player.
        ///
        /// <p><b>Known limitation:</b> this writes <c>Transform.position</c> directly. How to Fish's
        /// player is FishNet/Rigidbody-driven, so the game's own movement/network sync may overwrite
        /// it. The correct long-term entry point (e.g. a network transform / rigidbody move) is not
        /// yet identified; enable this only for testing and verify whether FishNet overrides it.</p>
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

            Vector3 host = _mapper.ToHost(new Vector3((float)mc.X, (float)mc.Y, (float)mc.Z));
            player.Transform.position = host;

            if (_followMoves == 0)
            {
                Logger.LogInfo("CrossMC: following McState -> host player (first move to "
                        + host.x.ToString("F2") + "," + host.y.ToString("F2") + "," + host.z.ToString("F2")
                        + "). If FishNet/Rigidbody overrides this, the transform write may not stick.");
            }

            _followMoves++;

            if (_config.PlayerFollowRotation)
            {
                player.Transform.rotation = Quaternion.Euler(mc.Pitch, mc.Yaw, 0f);
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
