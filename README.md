# HowToFishMC

[中文](README_ZH.md) | **English**

The **How to Fish** host adapter for [CrossMC](../CrossMC) — a standalone BepInEx plugin that makes
*How to Fish* (Unity 6 / Mono / FishNet / BepInEx 5) the first game bridged to Minecraft.

> ⚠️ **Work in progress.** Builds with `dotnet build`; runtime behaviour is not yet verified in game.
> See the framework's `docs/VERIFICATION.md`.

This is an **independent repository**. All game-independent logic (the shared-memory protocol, the
C#/Java bindings, the Minecraft mod) lives in the **CrossMC** repository. This repo contains only the
How to Fish-specific half: opening the mapping, the frame overlay, publishing the host player,
colliders and entities, and applying Minecraft damage with How to Fish's own multipliers.

```text
CrossMC/        generic framework (protocol + bindings + Minecraft mod)   ← sibling repo
HowToFishMC/    How to Fish host adapter                                  ← this repo
```

Future games get their own sibling repository (e.g. `EldenRingMC`, `SkyrimMC`) that depends on the
same CrossMC framework.

## Layout

```text
HowToFishMC/
├─ CrossMC.HowToFish.csproj   builds the plugin; references ../CrossMC/bindings/csharp
├─ host.properties            this adapter's config (damage multipliers, world→MC transform)
└─ src/
   ├─ Plugin.cs               BepInEx entry point + host export/damage consumption
   ├─ HostConfig.cs           config + coordinate mapper
   ├─ MappingStore.cs         persists the fixed world mapping (auto-anchor lock)
   └─ FrameOverlay.cs         draws the Minecraft frame
```

## Build

Requires the CrossMC repo checked out as a sibling (`../CrossMC`) — override with
`-p:CrossMCDir=<path>` if it lives elsewhere.

```powershell
dotnet build -c Release
# -> bin/Release/CrossMC.HowToFish.dll
```

Install into `...\How to Fish\BepInEx\plugins\` together with CrossMC's `CrossMC.Bindings.dll` and
`host.properties` (the game dir is set by the `GameDir` MSBuild property).

## What it does

- **Frame** — reads the newest Minecraft frame from shared memory and draws it as a screen rectangle.
- **Environment** — publishes the host environment/avatar as `HostState` (viewport, camera mode;
  informational only — Minecraft is authoritative for the player).
- **Input (generic, off by default)** — the user plays Minecraft with Minecraft's own input; this
  adapter does not drive the player. `input.capture` (default `false`) can forward host keyboard/
  mouse through the CrossMC `InputRing` as a generic capability (Minecraft would inject it into its
  own `KeyBinding`/`Mouse`).
- **Camera** — (opt-in, `camera.follow`) makes the How to Fish camera follow the Minecraft player's
  view (yaw/pitch from `McState`). Calibrate `camera.yawSign` / `camera.pitchSign` if the view is
  mirrored or inverted.
- **Follow** — (opt-in, `player.follow`) places the How to Fish player at the fixed `CoordinateMapper`
  position of the authoritative Minecraft player (`McState`), and mirrors Minecraft **health/hunger**
  (`player.followVitals`).
- **Collision** — publishes host world collider AABBs so Minecraft can build collision proxies.
- **Entities** — publishes host creatures with a stable CrossMC **`CrossEntityId`** (mapped from the
  host-native `NetworkObject.ObjectId`) so Minecraft can spawn proxy entities.
- **Damage** — consumes Minecraft's native damage events (keyed by `CrossEntityId`) and applies
  `host.properties` multipliers to the mapped host entity / local player.

## Player authority

**Play Minecraft; How to Fish is integrated as a second world.** Minecraft is the main game and the
Minecraft player is the one authoritative player:

```text
player keyboard/mouse ─▶ Minecraft native input ─▶ Minecraft player ─▶ McState
                                                         └─▶ host player + host camera (mirror)
```

- The host player/camera **mirror** `McState` (fixed coordinate mapping); they are representations,
  not a second player.
- The host transform is **never** written back onto the Minecraft player (`HostState` position is
  informational).
- Health/hunger flow Minecraft → host player.
- `InputRing` is a generic capability, not the player control path.

## Configuration (`host.properties`)

Belongs to this adapter, never to the protocol. Holds the world→MC transform and the damage
multipliers (`damage.default`, `damage.explosion`, `damage.projectile`, `damage.fall`, ...).

### World mapping — two modes

`MC = (host − origin) * scale`, with the X axis flipped. The mapping is **world configuration**: it
must be stable and must **not** depend on the current Minecraft save/spawn position.

- **Formal mode — `transform.autoAnchor=false` (default).** The mapping is the explicit
  `transform.originX/Y/Z` + `scale` + `flipX`, locked at startup. A loaded Minecraft save never
  changes it. Set the origin so the HOF world maps to a valid Minecraft location; the bootstrap
  teleports the Minecraft player to `ToMc(HOF player)`, so a wrong origin means a wrong landing spot.
- **Dev mode — `transform.autoAnchor=true`.** No explicit mapping yet: compute the origin **once**
  from the current players, then **lock and persist** it to `%LOCALAPPDATA%/CrossMC/howtofish.anchor`.
  It is never re-anchored afterwards (world change / save reload / reconnect / host restart all reuse
  the locked mapping). Use only while developing a mapping; formal integration should be explicit.

### Player lifecycle — one-time bootstrap

When a Minecraft world/session loads, the host asks Minecraft to align its player to the host
avatar's **fixed** mapped position **before** normal following starts:

```text
fixed CoordinateMapper ─▶ ToMc(HOF player) ─▶ MC player teleport ─▶ confirmed ─▶ FOLLOW_ACTIVE
```

During the bootstrap the HOF player does not move, and the follow/camera/health loops are paused, so
a freshly-loaded save position can never drag the host player. After confirmation the only direction
is **Minecraft → How to Fish**. This is a one-shot handshake (`HostState.BOOTSTRAP` + `teleportSeq` /
`McState.BOOTSTRAP_DONE`), not a two-way player sync. `player.bootstrap=false` disables it.

## Threading

All game access runs on the Unity main thread; only the shared-memory reads/writes are
thread-agnostic.

## Known limits

- `Creature.LocalHit(...)` (creature damage) is invoked best-effort via reflection; its exact
  signature is not yet verified in game.
- Collider export uses `Physics.OverlapSphereNonAlloc` AABBs; rotation is ignored.
- Overlay is IMGUI, not a URP `CommandBuffer`.
- Server-side proxy entities require an integrated server (singleplayer/LAN).
- Host-player follow is **opt-in** (`player.follow`). The formal follower takes over the host
  player, which is a *representation* of the authoritative Minecraft player: it disables the local
  `PlayerMovement`, makes the `Rigidbody` kinematic and disables any FishNet transform sync, then
  places the player at the fixed `CoordinateMapper` position of `McState` in `LateUpdate` (camera
  likewise). Everything is restored when follow is off. `player.followHardLock` (default `false`) is
  a debug/emergency fallback that additionally blanket-disables sync components. Verified in game
  for movement, camera and health.
