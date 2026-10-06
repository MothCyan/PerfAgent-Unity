# 前后对照样例工程（Before / After）

这里放着**同一个单场景小游戏的两种版本**，专门用来给 PerfAgent 做「同一份代码，优化前 vs 优化后」的现场验证：

| 目录 | 内容 | 用途 |
| --- | --- | --- |
| `AngryBirds_before/` | 上游原样的 Unity 工程 + 一份刻意写成反面样板的每帧遥测脚本 | 采集出「有问题」的报告 |
| `AngryBirds_after/` | **同样的玩法、同样的脚本文件清单**，但把每帧开销逐条修掉 | 采集出「干净」的报告，与 before 做对比 |

两边的脚本各存一份（不是共享），因为它们是**两个独立的 Unity 工程**：一次性放进同一个 `Assets/` 下会被同一个工程编译，出现两份同名类冲突。

## 上游来源与许可

- 上游工程：[`dgkanatsios/AngryBirdsStyleGame`](https://github.com/dgkanatsios/AngryBirdsStyleGame)（MIT，Copyright (c) 2016 Dimitris-Ilias Gkanatsios）
- 许可原文随工程保留：`AngryBirds_before/License.md`、`AngryBirds_after/License.md`
- 我们改了什么、为什么改：见 [`NOTICE.md`](./NOTICE.md)

---

## 用法 A（推荐）：一键装进当前工程，直接 Play 就能测

> 为什么推荐：Unity 只导入 `Assets/` 与 `Packages/`，`Samples/` 下的两个工程**不会**出现在当前工程的
> Project 窗口里；而快照又是按工程存放的（`ProjectSettings/PerfAgent/Snapshots`）——
> 跨工程连「对比」都选不到一起。装进当前工程，这两个问题同时消失。

菜单：**`Tools/PerfAgent/样例工程/`**

| 菜单项 | 作用 |
| --- | --- |
| 安装 Before（优化之前） | 把 `AngryBirds_before/Assets/**` 复制到 `Assets/PerfAgentFixture`，补 Tag / Sorting Layer，把场景加进 Build Settings 并打开 |
| 安装 After（优化之后） | 同上，换成 after 那一份（会先提示卸载当前这版） |
| 卸载（清理当前工程） | 删掉 `Assets/PerfAgentFixture`、回滚安装时补的 Tag / Sorting Layer、从 Build Settings 移除场景（**快照不删**） |
| 打开缺陷清单 / 打开使用说明 | 用系统默认程序打开 `PERF-FAULTS.md` / 本文件 |

安装器会自动处理这些坑（手工拷贝很容易漏）：

- **Tag**：样例用 `Bird` / `Brick` / `Pig` 三个标签（`FindGameObjectsWithTag` / `CompareTag`），
  当前工程没有就会直接抛 `UnityException: Tag: Bird is not defined` → 安装时补进 `TagManager`，卸载时回滚；
- **Sorting Layer**：样例有 `Background` / `Trees` / `Floor` / `Foreground`，
  而且**必须沿用样例里的 uniqueID**（场景里存的是 ID，不是名字），否则画面层次会全塌到 Default；
- **Build Settings**：两版的「重开一局」都靠 buildIndex / loadedLevel，场景不进 Build Settings 会直接报错；
- **DOTween**：样例的动画依赖它，随样例一起装（卸载时一起删）。

测完一版的流程：

1. `Tools/PerfAgent/样例工程/安装 Before`
2. Play → PerfAgent 面板 → **跟随采集** → 玩 20~30 秒（拖弹弓发射小鸟、砸砖块）→ 生成快照
3. 停止 Play → `卸载` → `安装 After`
4. 再采一份快照 → 两份在**同一个工程**里，「对比」页直接选

> 采集前清一次 Console，这样「日志刷屏」的前后差异看得最清楚。
> Before 故意保留每帧 `Debug.Log` 和手动 `GC.Collect()`，长时间挂着会有周期性卡顿尖峰，这是设计好的。

## 用法 B：用 Unity Hub 单独打开两个工程

1. Unity Hub → `Add` → `Add project from disk` → 选 `Samples/AngryBirds_before`（或 `_after`）
2. 用 Unity **2022.3** 打开（`ProjectSettings/ProjectVersion.txt` 已对齐本仓库开发用的 `2022.3.62f2c1`）
3. PerfAgent 不用手动装：工程的 `Packages/manifest.json` 里已经是
   `"com.night.perfagent": "file:../../Assets/PerfAgent"`
4. 打开 `Assets/Scenes/game.unity` → Play → 跟随采集 → 生成快照

> **跨工程的快照不能直接对比**。要比就得把 Before 的快照文件
> `AngryBirds_before/ProjectSettings/PerfAgent/Snapshots/*.json`
> 复制到 `AngryBirds_after/ProjectSettings/PerfAgent/Snapshots/` 下，再在面板里刷新快照列表。
> 嫌麻烦就用**用法 A**（同一工程内两版都采一遍）。

## 想验证「静态扫描能不能报出来」

打开 `AngryBirds_before/Assets/Scripts/TelemetryMonitor.cs`，文件头注释里逐条写了它踩了哪些反模式
（编号 F1～F14，对应清单在 [`PERF-FAULTS.md`](./PERF-FAULTS.md)）。After 里的同名文件是**同样的功能**，但每帧零分配。

一个刻意的对照点：After 的 `RefreshRegistry()` 也会 `FindObjectsOfType` / `FindGameObjectsWithTag`，
但它是**每秒一次**、而且写在普通方法里而不是 `Update` 方法体里，所以脚本反模式扫描不会报它 ——
扫描的判据是「这段调用是否坐落在每帧执行的方法体内」。

## 目录结构

```
Samples/
  README.md               ← 本文件：两种用法
  PERF-FAULTS.md          ← 缺陷编号表：每条反模式对应哪个扫描项 / 怎么修
  NOTICE.md               ← 上游出处、许可、我们改了什么
  AngryBirds_before/      ← 完整 Unity 工程（优化之前）
  AngryBirds_after/       ← 完整 Unity 工程（优化之后）
```

## 不要做的事

- 不要把这两个工程挪进 `Assets/`。要装进当前工程，请用**用法 A 的菜单**（它会装到
  `Assets/PerfAgentFixture`，一次只装一版，并且能从菜单里干净卸载）。
- 不要在样例工程里做业务开发 —— 它们是**性能诊断的固定样本**，改了就没法和另一份对照。
- 不要把 Before 的写法当参考（它的注释里写清了「这是反面样板」）。
