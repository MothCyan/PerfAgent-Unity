# PerfAgent 性能样例：AngryBirds Before / After

同一个单场景小游戏的**两份实现**，都装在本工程里（不是两个独立工程），专门用来验证
「同一玩法，优化前 vs 优化后」的采集与对比：

| 目录 | 场景 | 脚本 | 用途 |
| --- | --- | --- | --- |
| `Before/` | `Before/Scenes/AngryBirdsBefore.unity` | `Before/Scripts/Before*.cs` | 每帧反模式现场，采集出「有问题」的报告 |
| `After/` | `After/Scenes/AngryBirdsAfter.unity` | `After/Scripts/After*.cs` | 功能完全一致的优化版，用来对比 |
| `Plugins/` | — | DOTween（两份共用，只放一份） | 样例动画依赖 |

两份的脚本**类名与文件名都带前缀**（`BeforeGameManager` / `AfterGameManager`…），
场景也各自改名，所以能同时待在一个工程里、互不干扰；After 那一份的 asset GUID 整体重发过，
两份之间没有任何 GUID 冲突。

## 怎么测

1. 用 Unity 打开 **`Assets/PerfAgentSample/Before/Scenes/AngryBirdsBefore.unity`**（两个场景都已登记在 Build Settings 里）
2. Play → 打开 PerfAgent 面板 → **跟随采集** → 玩 20~30 秒（拖弹弓发射小鸟、砸砖块、砸猪）
3. 停止采集 → 生成快照。应当能看到：
   - 「脚本反模式」一片红：`new List`、LINQ、`Camera.main`、`FindGameObjectsWithTag`、
     每帧 `GetComponent`、每帧 `Debug.Log`、每帧 `Instantiate/Destroy`、手动 `GC.Collect`…
   - 每帧 GC 分配明显高于预算（对比 After 能差出一个量级）
   - Console 被每帧日志刷屏
4. 换成打开 **`After/Scenes/AngryBirdsAfter.unity`**，同样操作再采一份快照
5. 在 PerfAgent 的**对比**页选这两份快照 → 直接看帧时间 / 每帧分配 / 结论条数的变化

> 采集前清一次 Console，「日志刷屏」的差异最直观。
> Before 故意保留每帧 `Debug.Log` 与周期性 `GC.Collect()`，长时间挂着会出现卡顿尖峰 —— 这是设计好的，不是工具坏了。

## 对比的时候该看什么（很重要）

前后对比**只能看这两类**：

| 看什么 | Before 应该出现 | After 应该是 |
| --- | --- | --- |
| **代码问题数**（脚本反模式扫描，按「每帧方法体内」判定） | 20 条上下：`new List` / LINQ / `Camera.main` / `FindGameObjectsWithTag` / 每帧 `GetComponent` / 每帧 `Debug.Log` / `new GameObject`+`Destroy` / `GC.Collect` … | **0 条** |
| **动态指标**（帧时间的 P95/峰值、每帧 GC 分配、Draw Call 等，需要采到帧） | 每帧分配明显高于预算、掉帧与卡顿尖峰可见、Console 被日志刷屏 | 每帧分配接近 0，帧时间更平 |

**资源类结论（大纹理、磁盘占用…）是工程级的**：资源审计扫的是整个工程，而两份样例共用同一批
美术/音频资源（After 只是重发了 GUID），所以那几条在 Before / After 两份报告里**必然一模一样** ——
它不是「没有差异」，而是这一类结论本来就不区分玩法版本。

### 两份报告几乎一样？先看这里

按顺序自查：

1. **报告头部「采集窗口」是不是 `无帧数据`**。是的话这次根本没采到帧 ——
   现在会直接产出一条 **Error 结论「本次没有采到帧：动态指标全部不可用」**，
   并且采集进行中超过 2 秒没帧会在 Console 打 Warning（带 `enabled` / `profileEditor` / `historyLength`）。
   处理办法：到 Profiler 窗口确认 **Record 是开着的**（工具会自动打开，但被手动暂停时不会自己恢复），再采一次。
2. **报告头部的 `PerfAgent vX.Y.Z（程序集 时间）`** 是不是当前这次编译的时间。
   Unity 在源码编译失败时会继续跑**上一次编译成功的程序集** —— 那时报告的行为跟源码对不上，
   先看 Console 有没有 `error CS`，编译干净了再采。
3. 两份报告的「代码问题数」：After 如果不是 0，说明扫描或样例没装对（Before 应当一片红）。

## 遥测脚本是怎么生效的

两份的 `BeforeTelemetryMonitor` / `AfterTelemetryMonitor` 都用
`[RuntimeInitializeOnLoadMethod(AfterSceneLoad)]` 在 Play 时自己起一个隐藏宿主对象，
并且**只在属于自己那一版的场景里启动**（`SceneManager.GetActiveScene().name` 判断）。
因此：

- 不需要往场景里挂组件（避免手改 `.unity` 的 YAML，那样很容易把场景改坏）；
- 开着 Before 场景时，After 的那份不会跟着启动，不会污染测量。

## 想验证「静态扫描能不能报出来」

打开 `Before/Scripts/BeforeTelemetryMonitor.cs`，文件头注释里逐条写了它踩了哪些反模式
（编号 F1～F14，清单见 [`PERF-FAULTS.md`](./PERF-FAULTS.md)）。

一个刻意的对照点：`After/Scripts/AfterTelemetryMonitor.cs` 的 `RefreshRegistry()` 同样用了
`FindObjectsOfType` / `FindGameObjectsWithTag`，但它是**每秒一次**、而且写在普通方法里而不是 `Update`
方法体里，所以脚本反模式扫描不会报它 —— 扫描的判据是「这段调用是否坐落在每帧执行的方法体内」。

## 工程侧需要的东西（已经改好，别删）

- **Tag**：`Bird` / `Brick` / `Pig`（`ProjectSettings/TagManager.asset`）。
  没定义时 `FindGameObjectsWithTag` / `CompareTag` 会直接抛 `Tag: Bird is not defined`。
- **Sorting Layer**：`Background` / `Trees` / `Floor` / `Foreground`，**uniqueID 必须与场景里存的一致**
  （场景存的是 ID 不是名字），否则画面层次会全塌到 Default。
- **Build Settings**：两个场景都已登记（两版的「重开一局」分别依赖 `buildIndex` 与 `loadedLevel`）。

## 其他文件

- [`PERF-FAULTS.md`](./PERF-FAULTS.md)：缺陷编号表（哪条反模式对应哪个扫描项 / 怎么修）
- [`NOTICE.md`](./NOTICE.md)：上游出处、MIT 许可、我们改了什么
- [`License.md`](./License.md)：上游 MIT 许可原文

## 不要做的事

- 不要在样例里做业务开发 —— 它是**性能诊断的固定样本**，改了就没法和另一份对照。
- 不要把 Before 的写法当参考（它的注释里写清了「这是反面样板」）。
- 不要只保留一份脚本：两份的类名是各自独立的，删掉任何一边都会让另一个场景的引用失效。
