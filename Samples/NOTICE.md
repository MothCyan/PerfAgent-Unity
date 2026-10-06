# NOTICE · 第三方来源与修改说明

本目录下的两个 Unity 工程是 **PerfAgent 的性能诊断样例**，不是本项目原创的游戏。

## 1. 上游工程

- 仓库：[`dgkanatsios/AngryBirdsStyleGame`](https://github.com/dgkanatsios/AngryBirdsStyleGame)
- 作者：Dimitris-Ilias Gkanatsios
- 许可：**MIT**（Copyright (c) 2016 Dimitris-Ilias Gkanatsios）
- 许可原文随工程保留在 `AngryBirds_before/License.md` 与 `AngryBirds_after/License.md`
- 上游工程特点：只有一个场景（`Assets/Scenes/game.unity`）、Unity 2021.3、2D 物理 + 拖拽弹弓玩法、自带 DOTween 插件

MIT 许可允许复制、修改、再分发，条件是保留版权声明与许可原文——因此这两个副本里都原样带着 `License.md`。

## 2. 我们对上游做了什么改动

只改「工程能不能在本仓库环境里跑起来」和「性能」，不改玩法：

**两份副本都改的（环境适配）**

| 文件 | 改动 | 原因 |
| --- | --- | --- |
| `ProjectSettings/ProjectVersion.txt` | 对齐到本仓库开发用的 Unity 版本 `2022.3.62f2c1` | 避免打开时弹出升级提示、保证与插件同版本 |
| `Packages/manifest.json` | 只保留能离线解析的包 + 增加 `"com.night.perfagent": "file:../../Assets/PerfAgent"` | 上游带的 `com.unity.ads/analytics/purchasing/toolchain` 等在 2022.3 上多余且会拖慢解析 |
| `Packages/packages-lock.json` | 删除 | 让 UPM 重新解析到上面这份 manifest |
| `ProjectSettings/EditorBuildSettings.asset` | 场景 `guid` 由上游的全 `0` 改成真实 guid | 上游那份全 `0` 的 guid 会让 `SceneManager.LoadScene` 找不到场景 |
| `Logs/`、`UserSettings/`、`.gitignore`、`.gitattributes` | 不纳入副本 | 上游跑过留下的日志与本地上次运行状态，与样例无关 |

**只在 `AngryBirds_before` 里加/改的（故意注入的故障）**

- 新增 `Assets/Scripts/TelemetryMonitor.cs`：每帧遥测的「新手写法」，逐条踩了 F1～F14 号反模式。
- `Assets/Scripts/GameManager.cs`：`Update` 里加每帧 `Debug.Log`，`OnGUI` 里加每帧字符串拼接（F15、F16）。

**只在 `AngryBirds_after` 里改的（修复）**

- 新增同名 `Assets/Scripts/TelemetryMonitor.cs`：同样功能、每帧零分配。
- `GameManager.cs`、`SlingShot.cs`、`Bird.cs`、`Brick.cs`、`Pig.cs`、`Destroyer.cs`、
  `CameraPinchToZoom.cs`、`ParallaxScrolling.cs`：逐条修掉 G1～G12（清单见 `PERF-FAULTS.md`）。

所有 `.meta` 文件与 GUID 保持上游原样，所以场景里的组件引用不会断。

## 3. 工程内其他第三方资源

这些是**上游工程自带的**，我们没有替换，也没有重新分发到别处——它们只随这两个样例工程一起存在于本仓库：

- **DOTween**（`Assets/Plugins/Demigiant/DOTween`）：Demigiant 的免费版，随上游工程一同携带，遵循其自带的 `readme.txt` 中的授权说明。
- **美术与音效**：上游 README/脚本注释里标注了出处（opengameart.org、freesound.org、以及社区作者的作品），
  版权归各自作者；本仓库仅作为性能诊断样例原样保留，未用于任何商业用途。

如果这些第三方资源的授权方式不适合你的使用场景，请只保留 `Samples/README.md`、`Samples/PERF-FAULTS.md` 与
`Samples/AngryBirds_*/Assets/Scripts/` 下的脚本（脚本由本项目维护），其余资源自行替换。
