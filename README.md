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
- **Input** — captures keyboard/mouse with the Unity Input System, maps keys to CrossMC semantics
  and forwards them through the CrossMC `InputRing` (`input.capture`); Minecraft injects them into
  its **own** `KeyBinding`/`Mouse`, so its native movement/look/collision still apply.
- **Follow** — (opt-in, `player.follow`) drives the How to Fish player to follow the authoritative
  Minecraft player (`McState`) through the `CoordinateMapper`.
- **Collision** — publishes host world collider AABBs so Minecraft can build collision proxies.
- **Entities** — publishes host creatures with a stable CrossMC **`CrossEntityId`** (mapped from the
  host-native `NetworkObject.ObjectId`) so Minecraft can spawn proxy entities.
- **Damage** — consumes Minecraft's native damage events (keyed by `CrossEntityId`) and applies
  `host.properties` multipliers to the mapped host entity / local player.

## Player authority

**The Minecraft player is authoritative.** The host only captures input and follows the result; it
never moves the Minecraft player:

```text
host keyboard/mouse ─▶ InputRing ─▶ Minecraft ─▶ Minecraft player ─▶ McState ─▶ host player (follows)
```

- The host transform is **never** written back onto the Minecraft player (`HostState` position is
  informational).
- Host movement/collision is not authoritative: Minecraft decides the final state, including
  collisions with host proxies.
- View control must go through input (mouse delta), not `HostState.yaw/pitch`.

## Configuration (`host.properties`)

Belongs to this adapter, never to the protocol. Holds the world→MC transform and the damage
multipliers (`damage.default`, `damage.explosion`, `damage.projectile`, `damage.fall`, ...).

`transform.autoAnchor=true` (default) aligns the two coordinate systems from the current players —
the host player's position is mapped to the Minecraft player's position once — so host ⇄ MC
movement is relative and **neither player is teleported** to a foreign coordinate. Set
`transform.autoAnchor=false` to use the manual `transform.origin*`.

## Threading

All game access runs on the Unity main thread; only the shared-memory reads/writes are
thread-agnostic.

## Known limits

- `Creature.LocalHit(...)` (creature damage) is invoked best-effort via reflection; its exact
  signature is not yet verified in game.
- Collider export uses `Physics.OverlapSphereNonAlloc` AABBs; rotation is ignored.
- Overlay is IMGUI, not a URP `CommandBuffer`.
- Server-side proxy entities require an integrated server (singleplayer/LAN).
