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
   ├─ MappingStore.cs         持久化固定世界映射（auto-anchor 锁定）
   ├─ HostFrameExporter.cs    抓取游戏相机到 CrossMC host frame
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
- **宿主帧**（`render.enabled`）——把 How to Fish 相机抓取到 CrossMC host-frame 通道（私有
  `RenderTexture` + `AsyncGPUReadback`），供 Minecraft 作为世界空间面片绘制。分辨率/帧率可配
  （`render.width`/`height`/`fps`，默认 1280×720 @ 15）。**不会**改动游戏自身相机。
- **环境**——发布宿主环境 / 表现为 `HostState`（视口、相机模式；仅信息性——玩家权威在 Minecraft）。
- **输入（通用，默认关闭）**——玩家用 Minecraft 自己的输入游玩；本适配器不驱动玩家。`input.capture`
  （默认 `false`）可选地把宿主键鼠经 `InputRing` 转发（通用能力；Minecraft 会注入自身的
  `KeyBinding`/`Mouse`）。
- **视角**——（可选的 `camera.follow`）让 How to Fish 相机跟随 Minecraft 玩家的视角（`McState` 的
  yaw/pitch）。若画面镜像或上下反了，调整 `camera.yawSign` / `camera.pitchSign`。
- **跟随**——（可选的 `player.follow`）把 How to Fish 玩家放到 Minecraft 玩家经固定 `CoordinateMapper`
  映射后的位置，并镜像 Minecraft 的 **Health/Hunger**（`player.followVitals`）。
- **碰撞**——发布宿主世界的 Collider AABB，供 Minecraft 构建碰撞代理。
- **实体**——发布宿主生物，带稳定的 CrossMC **`CrossEntityId`**（由宿主的 `NetworkObject.ObjectId` 映射），
  Minecraft 据此生成代理实体。
- **伤害**——消费 Minecraft 的原生伤害事件（以 `CrossEntityId` 为键），按 `host.properties` 的倍率作用于
  对应宿主实体 / 本地玩家。

## 玩家权威

**玩 Minecraft，把 How to Fish 作为第二个世界接入。** Minecraft 是主游戏，Minecraft 玩家是唯一权威
玩家：

```text
玩家键鼠 ─▶ Minecraft 原生输入 ─▶ Minecraft 玩家 ─▶ McState
                                        └─▶ 宿主玩家 + 宿主相机（镜像）
```

- 宿主玩家/相机**镜像** `McState`（固定坐标映射）；它们只是表现，不是第二个玩家。
- 宿主 Transform **绝不**回写 Minecraft 玩家（`HostState` 的位置只是信息性）。
- Health/Hunger 由 Minecraft 流向宿主玩家。
- `InputRing` 是通用能力，不是玩家控制路径。

## 配置（`host.properties`）

属于本适配器，绝不进协议。包含世界→MC 变换与伤害倍率（`damage.default`、`damage.explosion`、
`damage.projectile`、`damage.fall` 等）。

### 世界映射——两种模式

`MC = (host − origin) * scale`，X 轴翻转。映射是**世界配置**：必须稳定，且**不得**依赖 Minecraft 当前
存档 / 出生位置。

- **正式模式 —— `transform.autoAnchor=false`（默认）。** 使用显式的 `transform.originX/Y/Z` + `scale` +
  `flipX`，启动即锁定。加载任何 Minecraft 存档都不会改变它。Origin 必须使 HOF 世界映射到合法的 Minecraft
  位置——Bootstrap 会把 Minecraft 玩家传送到 `ToMc(HOF 玩家)`，Origin 不对就会落到错误位置。
- **开发模式 —— `transform.autoAnchor=true`。** 尚无显式映射时：根据当前两名玩家**只计算一次** Origin，
  随后**锁定并持久化**到 `%LOCALAPPDATA%/CrossMC/howtofish.anchor`。之后绝不重锚（切存档 / 重进 / 重连 /
  宿主重启都复用该锁定映射）。仅在开发映射阶段使用；正式接入应使用显式映射。

### 玩家生命周期——一次性 Bootstrap

Minecraft 世界/会话加载时，宿主先让 Minecraft 玩家对齐到宿主玩家的**固定**映射位置，然后才进入正常跟随：

```text
固定 CoordinateMapper ─▶ ToMc(HOF 玩家) ─▶ Minecraft 玩家传送 ─▶ 确认 ─▶ FOLLOW_ACTIVE
```

Bootstrap 期间 HOF 玩家不动，跟随 / 相机 / Health 循环暂停，因此新加载的存档位置绝不会拖动宿主玩家。确认后
唯一的同步方向是 **Minecraft → How to Fish**。这是一次性握手（`HostState.BOOTSTRAP` + `teleportSeq` /
`McState.BOOTSTRAP_DONE`），不是双向玩家同步。`player.bootstrap=false` 可关闭。

## 线程

所有游戏 API 都在 Unity 主线程调用；只有共享内存的读写是线程无关的。

## 已知限制

- `Creature.LocalHit(...)`（生物伤害）通过反射 best-effort 调用，签名尚未实机验证。
- Collider 导出使用 `Physics.OverlapSphereNonAlloc` 的 AABB，忽略旋转。
- 叠加层目前是 IMGUI，不是 URP 的 `CommandBuffer`。
- 服务端代理实体需要集成服务端（单机 / 局域网）。
- 宿主玩家跟随是**可选项**（`player.follow`）。正式 follower 会“接管”宿主玩家（它是权威 MC 玩家的
  **表现**）：禁用本地 `PlayerMovement`、把 `Rigidbody` 设为 kinematic、禁用 FishNet 变换同步，然后在
  `LateUpdate` 把玩家放到 `McState` 经固定 `CoordinateMapper` 映射的位置（相机同理）；关闭跟随时恢复。
  `player.followHardLock`（默认 `false`）是调试/应急后备，会额外按名称禁用同步组件。移动 / 视角 /
  Health 已实机验证。
