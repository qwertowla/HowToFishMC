# HowToFishMC

[English](README.md) | **中文**

**How to Fish** 的 CrossMC 宿主适配器——一个独立的 BepInEx 插件，让 *How to Fish*（Unity 6 / Mono /
FishNet / BepInEx 5）成为第一个桥接到 Minecraft 的游戏。

> ⚠️ **开发中。** 可用 `dotnet build` 编译；运行时行为尚未实机验证。参见框架的 `docs/VERIFICATION.md`。

这是一个**独立仓库**。所有与游戏无关的逻辑（共享内存协议、C#/Java bindings、Minecraft 模组）都在
**CrossMC** 仓库里。本仓库只包含 How to Fish 专属的那一半：打开映射、画面叠加、发布宿主玩家 / 碰撞体 /
实体，以及用 How to Fish 自己的倍率施加 Minecraft 伤害。

```text
CrossMC/        通用框架（协议 + bindings + Minecraft 模组）   ← 同级仓库
HowToFishMC/    How to Fish 宿主适配器                          ← 本仓库
```

以后接入更多游戏时各自新建同级仓库（如 `EldenRingMC`、`SkyrimMC`），依赖同一个 CrossMC 框架。

## 目录结构

```text
HowToFishMC/
├─ CrossMC.HowToFish.csproj   构建插件；引用 ../CrossMC/bindings/csharp
├─ host.properties            本适配器配置（伤害倍率、世界→MC 变换）
└─ src/
   ├─ Plugin.cs               BepInEx 入口 + 宿主导出 / 伤害消费
   ├─ HostConfig.cs           配置 + 坐标映射
   └─ FrameOverlay.cs         绘制 Minecraft 画面
```

## 构建

需要把 CrossMC 仓库放在同级目录（`../CrossMC`）——放在别处时用 `-p:CrossMCDir=<路径>` 覆盖。

```powershell
dotnet build -c Release
# -> bin/Release/CrossMC.HowToFish.dll
```

安装到 `...\How to Fish\BepInEx\plugins\`，与 CrossMC 的 `CrossMC.Bindings.dll` 和 `host.properties`
放在一起（游戏目录由 MSBuild 属性 `GameDir` 指定）。

## 功能

- **画面**——从共享内存读取最新的 Minecraft 帧，作为屏幕矩形绘制。
- **环境**——发布宿主环境 / 表现为 `HostState`（视口、相机模式；仅信息性——玩家权威在 Minecraft）。
- **输入**——用 Unity Input System 采集键鼠，映射为 CrossMC 语义键码后经 `InputRing` 转发（`input.capture`）；
  Minecraft 把它们注入**自身**的 `KeyBinding`/`Mouse`，因此原生移动/视角/碰撞照常生效。
- **跟随**——（可选的 `player.follow`）通过 `CoordinateMapper` 让 How to Fish 玩家跟随权威的 Minecraft
  玩家（`McState`）。
- **碰撞**——发布宿主世界的 Collider AABB，供 Minecraft 构建碰撞代理。
- **实体**——发布宿主生物，带稳定的 CrossMC **`CrossEntityId`**（由宿主的 `NetworkObject.ObjectId` 映射），
  Minecraft 据此生成代理实体。
- **伤害**——消费 Minecraft 的原生伤害事件（以 `CrossEntityId` 为键），按 `host.properties` 的倍率作用于
  对应宿主实体 / 本地玩家。

## 玩家权威

**Minecraft 玩家是权威。** 宿主只负责采集输入并跟随结果，绝不移动 Minecraft 玩家：

```text
宿主键鼠 ─▶ InputRing ─▶ Minecraft ─▶ Minecraft 玩家 ─▶ McState ─▶ 宿主玩家（跟随）
```

- 宿主 Transform **绝不**回写 Minecraft 玩家（`HostState` 的位置只是信息性）。
- 宿主不拥有移动/碰撞权威：最终状态由 Minecraft 决定，包括与宿主代理的碰撞。
- 视角控制必须走输入（鼠标 delta），不要用 `HostState.yaw/pitch`。

## 配置（`host.properties`）

属于本适配器，绝不进协议。包含世界→MC 变换与伤害倍率（`damage.default`、`damage.explosion`、
`damage.projectile`、`damage.fall` 等）。

## 线程

所有游戏 API 都在 Unity 主线程调用；只有共享内存的读写是线程无关的。

## 已知限制

- `Creature.LocalHit(...)`（生物伤害）通过反射 best-effort 调用，签名尚未实机验证。
- Collider 导出使用 `Physics.OverlapSphereNonAlloc` 的 AABB，忽略旋转。
- 叠加层目前是 IMGUI，不是 URP 的 `CommandBuffer`。
- 服务端代理实体需要集成服务端（单机 / 局域网）。
