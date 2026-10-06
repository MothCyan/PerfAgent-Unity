# PerfAgent

**Unity 编辑器里的性能排查 Agent** —— 在编辑器里问一句「为什么掉帧」，它自己抓 Profiler 数据、跑审计、定位根因、给出带证据链的修复方案。

主包 `PerfAgent` **零第三方依赖**（不用 Newtonsoft / UniTask / EditorCoroutines），只用 Unity 自带 API，断网、没配 LLM Key 也完全可用。

> 完整文档（架构、代码地图、MCP 工具清单、数据口径说明）在
> **[`Assets/PerfAgent/README.md`](Assets/PerfAgent/README.md)**，本文件只讲「怎么在这个工程里跑起来」。

---

## 环境

| 项 | 值 |
|---|---|
| Unity | 2022.3 LTS（本仓库在 `2022.3.62f2c1` 上验证） |
| 渲染管线 | URP 14.0.12（工程基于 3D URP 模板） |
| 平台 | Editor only（asmdef 已限定 `includePlatforms: ["Editor"]`，不会进包体） |

## 仓库结构

```
Assets/PerfAgent/                 插件本体（以嵌入式包的形式放在工程里）
  Editor/                         采集 / 分析 / 修复执行 / UI / Agent 五层
  Samples~/DiagnosticSnapshot/    一份示例快照，可直接导入看效果
  Tests~/                         测试（~ 结尾，Unity 默认不编译）
Assets/PerfAgentSample/           内附的开源小游戏样例：优化前 / 优化后两份（见下文）
PerfAgent.McpForUnity/            可选的 MCP 桥接包，暴露 perf_* 工具给外部 MCP 客户端
Packages/manifest.json            已接入 MCP for Unity v10.2.0 与本地桥接包
```

## 跑起来

1. 用 Unity 2022.3 打开本工程（首次导入会比较久）。
2. 菜单 **`Tools > PerfAgent > 打开性能诊断面板`**。
3. 点 **「抓帧并分析」**（默认 300 帧，编辑器内几秒）。
4. 看 **「结论」** 标签：每条都带证据链、置信度、修复建议与可执行的修复计划。
5. 想追问 / 让模型参与判断：面板右上角 **「LLM 配置」** 填 Endpoint / 模型 / API Key
   （OpenAI 兼容协议，OpenAI / Azure / DeepSeek / 本地 Ollama 都能用）。
   **API Key 只存在本机 EditorPrefs，不会写进工程，也永远不会被提交。**

没配 LLM 也能用：规则引擎会独立产出完整报告，可以直接导出 HTML / Markdown。

---

## 内置示例：同一个开源小游戏，「优化前」与「优化后」

插件自带一份可以直接跑的对照样例。**玩法、场景、美术、音效、物理参数全部来自一个开源项目**：

| 项 | 值 |
|---|---|
| 上游仓库 | [`dgkanatsios/AngryBirdsStyleGame`](https://github.com/dgkanatsios/AngryBirdsStyleGame) |
| 作者 | Dimitris-Ilias Gkanatsios |
| 许可 | **MIT**（Copyright © 2016 Dimitris-Ilias Gkanatsios），原文保留在 [`Assets/PerfAgentSample/License.md`](Assets/PerfAgentSample/License.md) |
| 上游特点 | 只有一个场景、Unity 2021.3、2D 物理 + 拖拽弹弓玩法、自带 DOTween |

我们把这份游戏拷成了**两份共存在同一个工程里**（`Before` / `After`），玩法一字未改，只把「每帧开销」拆开：

- `Assets/PerfAgentSample/Before/`：上游写法 + 逐条注入的反模式（F1～F16）——每帧分配、每帧查找、每帧 `Debug.Log`、周期 `GC.Collect()` …
- `Assets/PerfAgentSample/After/`：同样玩法，逐条修掉（G1～G12）——同样的功能改成每帧零分配

> 脚本改名、After 那份整体重发 GUID、DOTween 只留一份、Tag / Sorting Layer / Build Settings 的改动，
> 以及 MIT 要求的来源与修改说明，全部记在 **[`Assets/PerfAgentSample/NOTICE.md`](Assets/PerfAgentSample/NOTICE.md)**；
> 缺陷/修复逐条对照表在 **[`PERF-FAULTS.md`](Assets/PerfAgentSample/PERF-FAULTS.md)**。

### 怎么跑

1. 打开 `Assets/PerfAgentSample/Before/Scenes/AngryBirdsBefore.unity`，**Play**，拖弹弓发射小鸟、砸砖、砸猪，玩 5～10 秒；
   同时开 PerfAgent 面板 → **跟随采集**（你操作，工具自己记；面板会**自动收成一条只显示帧率的细条**，不挡 Game 视图）；
   退出 Play 后自动展开并生成快照。
2. 换成 `After/Scenes/AngryBirdsAfter.unity`，重复同样操作，再采一份。
3. 在面板的**对比**页选这两份快照。

### 实测结果（编辑器内测量，2026-10-06）

Unity 2022.3.62f2c1 · WindowsEditor · RTX 5060 Laptop：

| 指标 | 优化前 | 优化后 | 变化 |
|---|---:|---:|---|
| 采集窗口 | 101 帧 | 150 帧 | — |
| **项目每帧托管分配** | **29 634 B**（越过归因地板，报「严重」） | **15 911 B**（未越过地板） | **−46 %（−13.7 KB/帧）** |
| 每帧分配 P50 / P95 | 23 438 / 38 320 B | 15 219 / 15 467 B | P95 **−60 %** |
| 帧耗时均值 / P50 | 8.15 / 3.77 ms | 2.43 / 2.37 ms | 均值 −70 % |
| **帧耗时峰值** | **133.52 ms**（`GC.Collect()` 造成的卡顿尖峰） | **3.23 ms** | 尖峰消失 |
| 实际 FPS | 122.71 | 412.16 | ×3.4 |
| Draw Call / 三角面 | 14 / 564 | 13 / 466 | 持平（同一批美术） |
| **每帧类代码问题**（当前场景目录） | **41 处** | **0 处** | 清零 |
| 事件类代码问题（碰撞回调里的 `Destroy` 等，正常游戏逻辑） | 13 处 | 5 处 | — |
| 严重级结论条数 | 7 | 0 | — |

### 读这张表要注意三件事

1. **代码问题看「每帧类」那一行，不是总数。** 静态扫描是工程级的，两份会互相扫到对方的脚本（总数两边都是 59）；
   工具会按「当前场景目录 / 其它目录」与「每帧方法 / 事件回调」两层拆开报。
2. **分配看两份的差值，别看绝对值。** 编辑器里量到的数字含编辑器自身开销（空场景 Play 就有 ~14 KB/帧），
   所以优化后的「项目每帧分配」15 911 B 仍高于预算 2 048 B，工具会写明「未越过归因闸门」；
   真正的结论是**差值 −13.7 KB/帧**（≈ 每秒少产生 0.8 MB 垃圾 @60 FPS）。想在编辑器里读到绝对值，得用 Player 开发构建复测。
3. **资源类结论两份完全相同**（大纹理未开 streaming、磁盘占用…）：资源审计本来就是工程级的、两份共用同一批资源 ——
   那不是「没差别」，而是这类结论不区分玩法版本。

更详细的自查步骤与常见误判（窗口太小、报告像旧版产的、把含编辑器开销的数字算成「超标 7 倍」等）
写在 **[`Assets/PerfAgentSample/README.md`](Assets/PerfAgentSample/README.md)**。

## 可选：用 MCP 驱动它

装了 [MCP for Unity](https://github.com/CoplayDev/unity-mcp) 后，可以把采集与分析能力暴露给外部 MCP 客户端
（Claude Desktop / VS Code Copilot 等），让它们自己触发采集、读取结论与修复计划。

本仓库的 `Packages/manifest.json` 里已经有这两行：

```json
"com.coplaydev.unity-mcp": "https://github.com/CoplayDev/unity-mcp.git?path=/MCPForUnity#v10.2.0",
"com.night.perfagent.mcpbridge": "file:../PerfAgent.McpForUnity",
```

桥接包是**编译期硬依赖** MCP for Unity 的，所以刻意独立成包、默认按需启用：
不想要它就把 `com.night.perfagent.mcpbridge` 那一行删掉（桥接代码保留在仓库里，随时再加回来）。

启用后，Unity 菜单 `Window > MCP for Unity` 完成客户端配置，暴露的工具包括
`perf_static_audit`、`perf_follow_capture_*`、`perf_list_snapshots`、
`perf_get_findings` / `perf_get_metrics` / `perf_get_fix_plan`。

桥接层**刻意只读**：没有任何「让外部模型直接改你工程」的入口，修改必须由人在面板里点确认。

## 这个仓库里没有的东西

`Library/`、`Temp/`、`Logs/`、`UserSettings/`、`*.csproj`、`*.sln` 都由 Unity 本机生成，已被 `.gitignore` 排除。
拉下来直接用 Unity 打开即可，不需要额外步骤。

## 许可

[MIT](LICENSE) © 2026 MothCyan

可以自由使用、修改、分发（含商业用途），只需保留版权与许可声明。
软件按「原样」提供，不附带任何担保。

`Assets/PerfAgentSample/` 下的样例游戏来自第三方开源项目（`dgkanatsios/AngryBirdsStyleGame`，MIT © 2016 Dimitris-Ilias Gkanatsios），
并非本项目原创；它自带的美术/音效/ DOTween 授权与出处见 [`Assets/PerfAgentSample/NOTICE.md`](Assets/PerfAgentSample/NOTICE.md)。
