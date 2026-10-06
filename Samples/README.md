# 前后对照样例工程（Before / After）

这里放着**同一个单场景小游戏的两种版本**，专门用来给 PerfAgent 做「同一份代码，优化前 vs 优化后」的现场验证：

| 目录 | 内容 | 用途 |
| --- | --- | --- |
| `AngryBirds_before/` | 上游原样的 Unity 工程 + 一份刻意写成反面样板的每帧遥测脚本 | 采集出「有问题」的报告 |
| `AngryBirds_after/` | **同样的玩法、同样的脚本文件清单**，但把每帧开销逐条修掉 | 采集出「干净」的报告，与 before 做对比 |

两边的脚本各存一份（不是共享），因为它们是**两个独立的 Unity 工程**：一次性放进同一个 `Assets/` 下会被同一个工程编译，出现两份同名 `GameObject`/类冲突。

## 上游来源与许可

- 上游工程：[`dgkanatsios/AngryBirdsStyleGame`](https://github.com/dgkanatsios/AngryBirdsStyleGame)（MIT，Copyright (c) 2016 Dimitris-Ilias Gkanatsios）。
- 许可原文随工程保留：`AngryBirds_before/License.md`、`AngryBirds_after/License.md`。
- 我们做了哪些改动、为什么改，见 [`NOTICE.md`](./NOTICE.md)。

## 怎么打开

1. Unity Hub → `Add` → `Add project from disk` → 选 `Samples/AngryBirds_before`（或 `_after`）。
2. 用 Unity **2022.3** 打开（`ProjectSettings/ProjectVersion.txt` 已对齐本仓库开发用的 `2022.3.62f2c1`）。
3. PerfAgent 不需要手动装：工程的 `Packages/manifest.json` 里已经有
   `"com.night.perfagent": "file:../../Assets/PerfAgent"`，会直接引用本仓库里的插件源码。

> 第一次打开会做一次资源导入；场景只有一个：`Assets/Scenes/game.unity`。

## 怎么用（两边各跑一遍，再看对比）

1. 打开 **`AngryBirds_before`**，按 Play 进入游戏。
2. 打开 PerfAgent 面板 → 「跟随采集」→ 自己玩 20～30 秒（拖拽弹弓发射小鸟、把砖块砸下来）。
3. 结束采集 → 生成快照 → 看报告。这时应当能看到：
   - 「脚本反模式」一片红（`new List`、LINQ、`Camera.main`、`GameObject.Find*`、每帧 `GetComponent`、每帧 `Debug.Log`…）；
   - 每帧 GC 分配明显高于预算；
   - Console 被每帧日志刷屏。
4. 关掉工程，打开 **`AngryBirds_after`**，同样操作，再采集一份快照。
5. 在 PerfAgent 的**对比**里选这两份快照 → 直接看帧时间、每帧分配、结论条数的前后差。

> 采集前把 Console 清一次（`Clear`），这样「日志刷屏」的对比更直观。
> Before 工程**故意**保留每帧 `Debug.Log` 与手动 `GC.Collect()`，长时间挂着会出现周期性卡顿尖峰，这是设计好的，不是工具坏了。

## 想验证「静态扫描能不能报出来」

打开 `AngryBirds_before/Assets/Scripts/TelemetryMonitor.cs`，文件头注释里逐条写了它踩了哪些反模式（编号 F1～F14，
对应清单在 [`PERF-FAULTS.md`](./PERF-FAULTS.md)）。After 里的同名文件是**同样的功能**，但每帧零分配。

一个刻意的对照点：After 里的 `RefreshRegistry()` 也会 `FindObjectsOfType` / `FindGameObjectsWithTag`，
但它是**每秒一次**、而且写在普通方法里而不是 `Update` 方法体里，
所以脚本反模式扫描不会报它——扫描的判据是「这段调用是否坐落在每帧执行的方法体内」。

## 目录结构

```
Samples/
  README.md               ← 本文件：怎么用
  PERF-FAULTS.md          ← 缺陷编号表：每条反模式对应哪个扫描项 / 怎么修
  NOTICE.md               ← 上游出处、许可、我们改了什么
  AngryBirds_before/      ← 完整 Unity 工程（优化之前）
  AngryBirds_after/       ← 完整 Unity 工程（优化之后）
```

## 不要做的事

- 不要把这两个工程挪进 `Assets/`；也不要直接在这两个工程里做业务开发——它们是**性能诊断的固定样本**，
  改了就没法再和另一份对照。
- 不要把 Before 的写法当参考（它的注释里写清了「这是反面样板」）。
