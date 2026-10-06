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
2. 打开 PerfAgent 面板 → **跟随采集** —— 工具会先量一次基线，然后**自动进入 Play** 开始记录，
   你只管玩 **5~10 秒**（拖弹弓发射小鸟、砸砖块、砸猪）；玩完**退出 Play** 即自动结束并出快照。
   > 采集期间面板顶部会实时显示帧率与帧耗时（4 Hz 刷新的波形在它下面）。
   > 面板**不会自动收起、也不会改窗口尺寸** —— 想不挡画面就自己拖到边上。
   > 条上实时显示 FPS / 帧耗时 P50·P95·峰值 / 已记录帧数，还有「展开面板」「停止采集」两个按钮 ——
   > 想边看波形边操作就点「展开面板」（展开后本次采集不会再自动收起）；
   > 直接退出 Play 也会结束采集，结束后面板自动展开。
   > 手动结束：菜单 `Tools/PerfAgent/停止跟随采集`，或工具栏的「停止采集」（也可以直接退出 Play）。
   > 不要玩很久：可分析的窗口受 **Profiler 面板历史长度**限制（工具会把它改成 2000 帧，但改不动时
   > 只剩默认的 300 帧，在高帧率下就是 1 秒多）。现在暖机最多只吃掉窗口的一半，所以即使面板历史只有
   > 300 帧，也能留下上百帧可用数据；实在想看长一点的窗口，就先把 Profiler 面板的历史长度调大。
3. 停止采集（或退出 Play） → 生成快照。应当能看到：
   - 「脚本反模式」一片红：`new List`、LINQ、`Camera.main`、`FindGameObjectsWithTag`、
     每帧 `GetComponent`、每帧 `Debug.Log`、每帧 `Instantiate/Destroy`、手动 `GC.Collect`…
   - 每帧 GC 分配明显高于预算（对比 After 能差出一个量级）
   - Console 被每帧日志刷屏
4. 换成打开 **`After/Scenes/AngryBirdsAfter.unity`**，同样操作再采一份快照
5. 在 PerfAgent 的**对比**页选这两份快照 → 直接看帧时间 / 每帧分配 / 结论条数的变化

> 采集前清一次 Console，「日志刷屏」的差异最直观。
> Before 故意保留每帧 `Debug.Log` 与周期性 `GC.Collect()`，长时间挂着会出现卡顿尖峰 —— 这是设计好的，不是工具坏了。

## 对比的时候该看什么（很重要）

前后对比**只看这两类**（数字是 2026-10-06 在本机实采到的，供你核对）：

| 看什么 | Before | After |
| --- | --- | --- |
| **每帧类代码问题（当前场景目录）**（Update/FixedUpdate/LateUpdate/OnGUI 方法体内的反模式） | **41 处**：`GetComponent` 9 · 字符串拼接 7 · `Camera.main` 4 · LINQ 3 · `Debug.Log` 2 · 查找 API 2 · `GC.Collect` / `new Vector2[]` / `new List` / 字符串格式 / `foreach` 枚举器 … | **0 处** |
| **事件类代码问题（当前场景目录）**（碰撞/触发回调里的写法） | 13 处 | 5 处（碰撞时 `Instantiate`+`Destroy`、取碰撞体刚体等，**是正常游戏逻辑**，两版都有） |
| **动态指标**（帧时间的 P95/峰值、每帧 GC 分配、Draw Call 等，需要采到帧） | 每帧托管分配 **37740 B**、Draw Call 14、三角面 568 | 每帧托管分配 **21615 B**、Draw Call 13、三角面 466 |

> **分配怎么比才对：看两份的**差值**，别看绝对值。**
> 编辑器里量到的「每帧托管分配」含编辑器自身开销（空场景 Play 就有 ~14 KB/帧）：
> Before 37740 − After 21615 = **约 16 KB/帧的差**，那才是 F1–F16 注入的效果。
> 绝对值（37153 / 21038）在编辑器里都不能当「项目分配」用 —— After 那个只比归因地板（16 KB/帧）高一点点，
> 报告会写明「未越过归因闸门」；要坐实请用 **Player 构建版** 采。这两句话在报告的**「闸门口径」**一节里。
>
> **帧耗时同理**：那两次采集 After 只录到 10 帧（< 30 帧门槛），统计类结论被工具主动跳过了 —— 重采时每份玩 5~10 秒。

**资源类结论（大纹理、磁盘占用…）与代码类结论都是工程级的** —— 但现在的处理方式不同：

- **资源审计**扫的是整个工程，而两份样例共用同一批美术/音频资源（After 只是重发了 GUID），
  所以那几条在 Before / After 两份报告里**必然一模一样** —— 它不是「没有差异」，
  而是这一类结论本来就不区分玩法版本。
- **代码反模式扫描**同样是工程级的（扫 `Assets/**/*.cs`），所以两份报告默认都会列出**两套脚本的问题**。
  为此每条代码问题都标了「属于当前采集的场景目录 / 属于其它目录」，报告里：
  - 指标表看 **「每帧类代码问题（当前场景目录）」**（前后对比就看这一行）；
  - 代码表里带 **★** 的行 = 属于本次采集场景，其余行来自另一份副本，对比时**直接忽略**；
    表里还有一列**「时机」**：`每帧` = 每帧方法体内（稳态分配），`事件` = 碰撞/触发回调里（正常游戏逻辑）；
  - 结论里会写「分组（当前场景目录 / 其它目录）= 1 / 5」这样的拆分证据；
    如果一条问题**全在另一份副本**里，它会被降级成提示级并写明「不属于本次采集的场景目录」。

### 两份报告几乎一样？先看这里

按顺序自查：

1. **报告头部「采集窗口」是不是 `无帧数据`**。是的话这次根本没采到帧 ——
   现在会直接产出一条 **Error 结论「本次没有采到帧：动态指标全部不可用」**，
   并且采集进行中超过 2 秒没帧会在 Console 打 Warning（带 `enabled` / `profileEditor` / `historyLength`）。
   处理办法：到 Profiler 窗口确认 **Record 是开着的**（工具会自动打开，但被手动暂停时不会自己恢复），再采一次。
2. **报告头部的 `PerfAgent vX.Y.Z（程序集 时间）`** 是不是当前这次编译的时间。
   Unity 在源码编译失败时会继续跑**上一次编译成功的程序集** —— 那时报告的行为跟源码对不上，
   先看 Console 有没有 `error CS`，编译干净了再采。
3. **「采集窗口只有 N 帧，统计类结论已跳过」**：说明有效窗口不够（门槛 30 帧）。
   现在这份数据**不能**用来和另一份快照对比（报告里会直接写明这一点）。
   处理办法：重新采，并按上面「怎么测」第 2 步控制时长（太短则帧少，太长则早帧被面板历史挤出去）；
   如果报告里出现「暖机只丢了开头的 N 帧」的备注，说明窗口本身就很小，先把 Profiler 面板历史长度调大。
4. 两份报告的「代码问题数」：After 一般不为 0（工程里还有 Before 那套脚本）——
   要看 **「代码问题数（当前场景目录）」**，After 应当是 **0**，Before 应当一片红。
   如果两边的这一行数字一样，说明作用域判定没生效（例如采集时没有打开任何场景）。
5. **报告或 AI 说「每帧分配超标好几倍」**：先看报告的**「闸门口径」**一节和工具返回的 `gates`。
   那里会写明这个数字是不是含编辑器开销的口径、有没有越过归因地板。
   实测就有一次：拿「每帧托管分配 14443.6 B ÷ 播放器预算 2048 B」算出了「超标 7.05 倍」，
   而那是**空场景在编辑器里也会有的开销**，规则引擎本来就没把它当问题。

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
- **序列化字段名必须和场景里的键一致**。场景存的是**字段名**：
  上游 `CameraMove` 里的 `public Slingshot SlingShot;` 被改名成 `AfterSlingShot` 时，
  `.unity` 里那一行还是 `SlingShot:` —— 键名对不上，运行时字段就是 **null**，
  `AfterCameraMove.Update()` 每帧抛 `NullReferenceException`（已于 2026-10-06 修好：
  两个场景的键分别改成 `BeforeSlingShot` / `AfterSlingShot`）。
  如果改完名字又出这种报错，跑一遍体检：
  `python Assets/PerfAgent/Tests~/Standalone/audit-sample-serialized-fields.py <工程根>`。

## 其他文件

- [`PERF-FAULTS.md`](./PERF-FAULTS.md)：缺陷编号表（哪条反模式对应哪个扫描项 / 怎么修）
- [`NOTICE.md`](./NOTICE.md)：上游出处、MIT 许可、我们改了什么
- [`License.md`](./License.md)：上游 MIT 许可原文

## 不要做的事

- 不要在样例里做业务开发 —— 它是**性能诊断的固定样本**，改了就没法和另一份对照。
- 不要把 Before 的写法当参考（它的注释里写清了「这是反面样板」）。
- 不要只保留一份脚本：两份的类名是各自独立的，删掉任何一边都会让另一个场景的引用失效。
