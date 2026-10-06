# NOTICE · 第三方来源与修改说明

`Assets/PerfAgentSample/` 下的游戏来自第三方开源项目，不是本项目原创。

## 1. 上游工程

- 仓库：[`dgkanatsios/AngryBirdsStyleGame`](https://github.com/dgkanatsios/AngryBirdsStyleGame)
- 作者：Dimitris-Ilias Gkanatsios
- 许可：**MIT**（Copyright (c) 2016 Dimitris-Ilias Gkanatsios），原文见同目录 [`License.md`](./License.md)
- 上游特点：只有一个场景、Unity 2021.3、2D 物理 + 拖拽弹弓玩法、自带 DOTween 插件

MIT 允许复制、修改、再分发，条件是保留版权声明与许可原文 —— 因此本目录保留了 `License.md`。

## 2. 我们做了什么改动

### 2.1 为了让两份能同时待在**同一个工程**里

| 改动 | 原因 |
| --- | --- |
| 每个脚本**改名**：`GameManager` → `BeforeGameManager` / `AfterGameManager`（其余同理，含 `Constants`、`Enums` 与枚举类型 `SlingshotState`/`GameState`/`BirdState`） | 两个版本同名类会冲突；文件名与 MonoBehaviour 类名必须一致 |
| 场景改名：`Scenes/game.unity` → `Scenes/AngryBirdsBefore.unity` / `AngryBirdsAfter.unity` | 场景路径唯一 |
| **After 那一份整体重发 GUID**，并重写其内部引用（场景 / 预制体里的 `m_Script`、材质与精灵引用） | 两份共存时 GUID 不能撞；Before 保留上游 GUID，After 全部换成新的。已校验：两份 GUID 集合无交集、各自引用都能解析到本目录内的 asset |
| DOTween 插件只保留**一份**（`PerfAgentSample/Plugins/Demigiant/DOTween`），两份脚本共用 | 两份都放会命中 `Multiple precompiled assemblies with the same name 'DOTween'`，`DOTweenModule*.cs` 也会 `CS0101` 重复定义 |
| 去掉上游的 `Assets/Resources`（内含 `BillingMode.json`） | 是 IAP 配置，本样例用不到；两个同名 `Resources/BillingMode.json` 在同一个工程里会冲突 |
| `ProjectSettings/TagManager.asset` 补 `Bird`/`Brick`/`Pig` 三个 Tag 与 `Background`/`Trees`/`Floor`/`Foreground` 四个 Sorting Layer（沿用上游的 uniqueID） | Tag 未定义会让 `FindGameObjectsWithTag` / `CompareTag` 抛异常；Sorting Layer 场景里存的是 ID，必须与名字对应 |
| 两个场景登记进 `EditorBuildSettings` | 两版的「重开一局」分别走 `buildIndex` 与 `loadedLevel` |

### 2.2 为了造出「优化之前 / 优化之后」的对照

- **新增** `Before/Scripts/BeforeTelemetryMonitor.cs`：每帧遥测的「新手写法」，逐条踩了 F1～F14 号反模式；
- **新增** `After/Scripts/AfterTelemetryMonitor.cs`：同样功能、每帧零分配；
- `Before/Scripts/BeforeGameManager.cs`：`Update` 里加每帧 `Debug.Log`、`OnGUI` 里加每帧字符串拼接（F15、F16）；
- `After/Scripts/` 里的 `AfterGameManager` / `AfterSlingShot` / `AfterBird` / `AfterBrick` / `AfterPig` /
  `AfterDestroyer` / `AfterCameraPinchToZoom` / `AfterParallaxScrolling`：逐条修掉 G1～G12（见 `PERF-FAULTS.md`）。

两个遥测脚本都用 `[RuntimeInitializeOnLoadMethod]` 自建隐藏宿主对象，并且**只在属于自己那一版的场景里启动**，
所以不需要往场景里挂组件（避免手改 `.unity` 的 YAML）。

### 2.3 玩法相关的部分完全没动

场景内容、预制体、精灵、音效、物理参数、DOTween 调用都保持上游原样；
Before 的脚本依旧是上游那套逻辑，After 只把**每帧开销**降下来。

## 3. 工程内其他第三方资源

- **DOTween**（`Plugins/Demigiant/DOTween`）：Demigiant 的免费版，随上游工程一起携带，遵循其自带 `readme.txt` 的授权说明。
- **美术与音效**：上游 README / 脚本注释里标注了出处（opengameart.org、freesound.org 等社区作者作品），版权归各自作者，
  本仓库仅作为性能诊断样例原样保留，未用于商业用途。

如果这些第三方资源的授权方式不适合你的使用场景，请只保留 `Assets/PerfAgentSample/` 下我们自己维护的
文档与 `README.md`/`PERF-FAULTS.md` 所述脚本，其余资源自行替换。
