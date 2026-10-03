# PerfAgent · Unity 内置性能排查 AI Agent

在 Unity 编辑器里直接问你「为什么掉帧」，它自己抓 Profiler 数据、跑审计、定位根因、给出可执行的修复方案。

**零第三方依赖**（不依赖 Newtonsoft / UniTask / EditorCoroutines），只用了 Unity 自带 API。

---

## 一分钟上手

**方式 A：作为本地包引用（推荐，工程目录保持干净）**

1. 把 `PerfAgent` 文件夹放到工程根目录旁（例如 `<项目根>/PerfAgent/`，与 `Assets/` 同级）。
2. 编辑 `Packages/manifest.json`，加入：
   ```json
   { "dependencies": { "com.night.perfagent": "file:../PerfAgent" } }
   ```
3. 回到 Unity，等待编译完成。

**方式 B：直接放进工程**

把 `PerfAgent/Editor` 整个复制到 `Assets/PerfAgent/`（asmdef 已限定 Editor 平台，不会进包体）。

**开始使用**

1. 菜单 `Tools > PerfAgent > 打开性能诊断面板`
2. 点「采集并分析」——工具会把 Profiler 打开并记录，默认跑够 300 帧就自动分析
   （数据直接读 Profiler 面板已记录的帧，工具自己不做逐帧采样，见下节）
3. 看「结论」标签：每条都带证据链、置信度、修复建议
4. 想启用对话追问：点面板右上角「LLM 配置」填 Endpoint / 模型 / API Key（内置常见服务商预设与测试连接）
5. 没配 LLM 也能用：规则引擎独立产出完整报告（点「导出 HTML」）
6. 想验证「优化到底有没有效果」：在「对比」标签选一份基准快照，直接看恶化/改善指标与整体判定
7. 每条结论下方都有**修复计划**：可执行步骤带「执行」按钮，点击前会弹窗列出将要修改的目标；
   改完自动重跑审计，并在「变更」标签里留记录、可撤销

---

## 架构

```
UI 层         PerfAgentWindow / PerfApiProbeWindow / SettingsProvider
   ↓
Agent 层      AgentLoop（工具循环）· LlmClient（SSE 流式）· ToolRegistry（19 个工具）
   ↓
分析层        PerfRuleEngine（20+ 规则）· PerfDiff（快照对比）· PerfFixPlanner（修复计划）· PerfReportExporter（导出 + 幻觉校验）
   ↓
执行层        PerfFixExecutor（唯一会改工程的地方）· PerfChangeLog（变更日志 + 撤销）
   ↓
采集层        9 个采集器：环境 / 内存 / 渲染统计 / 帧耗时 / Marker / 资源 / 场景 / 物理 / 代码
   ↓
数据层        PerfSnapshot（统一可序列化模型）+ PerfSession
```

**关键设计：采集与分析彻底分离。** UI 按钮和 Agent 工具走的是同一条 `PerfPipeline`，所以「人点出来」和「Agent 调出来」的结果完全一致。

### 代码地图

| 路径 | 作用 |
|---|---|
| `Editor/Core/PerfModels.cs` | 数据模型（证据链、结论、指标、帧、问题） |
| `Editor/Core/ProfilerApi.cs` | Profiler 反射适配层 + `MemApi` 内存 API |
| `Editor/Core/PerfPipeline.cs` | 采集 → 分析 → 落盘 编排 |
| `Editor/Utils/Reflect.cs` | 通用跨版本反射工具（找不到就返回 null，不抛异常） |
| `Editor/Utils/MiniJson.cs` | 极简 JSON 解析/序列化（LLM 线协议） |
| `Editor/Collectors/*` | 9 个采集器 |
| `Editor/Analysis/PerfRuleEngine.cs` | 确定性规则引擎 |
| `Editor/Analysis/PerfDiff.cs` | 快照对比：指标方向判定 + 回归/改善分类 + 加权判定（无 Unity 依赖，可独立回归） |
| `Editor/Analysis/PerfFixPlan.cs` | 修复计划规划器：结论 → 可执行动作 + 人工步骤，按收益/成本排序（无 Unity 依赖） |
| `Editor/Analysis/PerfFixExecutor.cs` | 一键修复执行器 + 撤销（批量改导入设置、工程设置、场景对象） |
| `Editor/Core/PerfChangeLog.cs` | 修复变更日志（含还原信息），落盘可审计 |
| `Editor/Analysis/PerfReportExporter.cs` | MD/HTML 报告 + 幻觉校验 |
| `Editor/Core/PerfConversationStore.cs` | 会话持久化（对话文本 + LLM 上下文） |
| `Editor/Core/PerfAgentMcpIntegration.cs` | MCP 集成开关（启用/禁用/状态） |
| `Editor/Agent/ToolRegistry.cs` | Agent 工具集（已做限流与隐私过滤） |
| `Editor/Agent/LlmClient.cs` | OpenAI 兼容客户端（流式，含非流式回退） |
| `Editor/Agent/AgentLoop.cs` | 工具调用循环 + 防幻觉闸门 |
| `Editor/UI/*` | 主面板、API 探针、配置页、LLM 配置窗口 |

数据落地位置（刻意不放在 `Assets/`，避免触发导入与版本库噪声）：

```
<项目根>/ProjectSettings/PerfAgent/PerfAgentSettings.asset   配置
<项目根>/ProjectSettings/PerfAgent/Snapshots/*.json          快照
<项目根>/ProjectSettings/PerfAgent/Snapshots/Reports/*        报告
<项目根>/ProjectSettings/PerfAgent/Conversations/*.json      对话历史（含可恢复的 LLM 上下文）
<项目根>/ProjectSettings/PerfAgent/FixLog.json                一键修复的变更日志（含可撤销的前值）
```

---

## 三条设计原则

**1. 工具优先于模型**
所有数值都来自工具实测，LLM 只负责编排与解释。工具返回值只含数值与标识，不含解释性文案。

**2. 结论必须带证据链**
每条结论结构为 `{现象, 证据[], 根因, 建议, 置信度, 定位}`，证据形如
`[frame_capture] 帧耗时 P95 = 24.3 ms（预算 16.67）@FrameCapture/300 帧`。

**3. 不产生脏数据**
`ProfilerRecorder` 的计数器名字对不上时 `Valid == false`，采集器直接跳过而不是记 0。
拿不到数据会写进「提示」区，而不是假装测到了。

---

## 一键修复的安全约束

这是插件里**唯一会改工程文件**的部分，所以它按最保守的方式做：

| 约束 | 做法 |
|---|---|
| 绝不自动执行 | 只有用户在结论卡片里点「执行」才会改东西；Agent 那边只有只读的 `get_fix_plan`，工具集里不存在修改入口 |
| 改什么先说清楚 | 点击后弹窗列出：动作、具体改哪个属性、影响多少个目标（含路径清单）、预期收益、风险等级 |
| 风险分级 | `safe`（只影响内存/开销）/ `moderate`（会改导入结果或运行时行为）/ `risky`（可能影响玩法，如去掉碰撞体、关闭 BlendShape、标记 Static） |
| 可撤销 | 每个动作把「改前的值」写进变更日志，「变更」标签里可逐条撤销 |
| 没有执行器就不给按钮 | 代码级每帧分配、LOD、遮挡剔除这类无法安全自动化的问题，只给人工步骤 + 跳转，**不假装能一键修** |
| 改完立即复验 | 执行后自动重跑静态审计并刷新结论，能直接看到问题是否消失 |
| 找不到的目标跳过 | 对象/资源不存在或已符合预期就跳过并如实报告，不猜、不硬改 |

典型可一键修复项：纹理关闭 Read/Write、纹理改压缩、maxTextureSize 降档、开启 Mipmap Streaming、
模型关闭 Read/Write、关闭自动碰撞体、关闭 BlendShape、网格压缩、音频加载方式、关闭 Preload、
关闭 Physics Auto Sync Transforms、fixedDeltaTime 复位、关闭 `updateWhenOffscreen`、批量标记 Static。

---

## 防幻觉的三道闸门

1. **系统提示词**：强制「数值必须来自工具返回值，不允许估算」。
2. **上下文隔离**：原始帧数据永不进入 prompt，只传聚合值与 Top-N。
3. **产出校验**：`PerfReportExporter.UnverifiedNumbers()` 抽取报告里所有数字，逐个回溯证据集，
   找不到出处的在文末列出「未验证数值」清单。Agent 回答时同样会跑这一步。

---

## API 探针（重要）

Unity 的 Profiler / 内存 API 跨版本差异很大，其中不少是 `internal`。本插件的策略是：
**凡是不确定能用的一律走反射，保证任何版本都能编译；拿不到就优雅降级并提示你运行探针。**

菜单 `Tools > PerfAgent > API 探针` 会一次列出：

- Profiler 帧数据链路是否打通（`ProfilerDriver` / `HierarchyFrameDataView` 成员逐个探测）
- 内存 API 哪些可用、实际读数多少
- 14 个 `ProfilerRecorder` 计数器哪些有效
- `UnityStats` / `RawFrameDataView` / `FrameTimingManager` / TMP 是否存在
- 关键类型的完整成员清单（照着它 5 分钟就能把适配代码补精确）

**如果某个指标一直是空的，先跑探针，再改代码。**

---

## 数据来源与可信度分级

Unity 的 Profiler 窗口有一部分数据来自引擎内部接口，第三方插件**拿不到**：Deep Profile 的调用栈级分配归因、
内存快照的分配来源、Frame Debugger 的逐 Draw Call 细节、GPU 计数器、真机 SysTrace。
所以本工具不承诺「复刻 Profiler」，而是把每份数据按来源分三级，并在指标里写明出处：

| 级别 | 数据 | 来源 | 拿不到时的行为 |
|---|---|---|---|
| **确定性**（不依赖 Profiler） | 资源导入问题、场景/物理反模式、代码反模式、预算比对 | `AssetImporter` / 场景遍历 / 文本扫描 | 不适用，全部是确定性结果 |
| **实测·公开 API** | 总分配/保留内存、Mono 堆、TempAllocator、GPU 驱动内存、Draw Call / Batches / SetPass / Triangles、GC 分配、帧耗时 | `Profiler.GetXxx`（即时读数）、**Profiler 面板帧历史**（序列数据） | 计数器 `Valid == false` 时**不写该指标**，并在「提示」里说明原因 |
| **实验性·内部 API** | Marker 排行（自身耗时 / GC 分配 / 调用次数） | `UnityEditorInternal.ProfilerDriver` + `HierarchyFrameDataView`（internal，走反射） | 解析不出就返回空 + 写原因，绝不产生错误数据 |

每条结论都带证据链，证据含 `tool` / `metric` / `value` / `source`，其中 `source` 就是上表里的具体 API 名，
可以直接对照 Unity 文档复核。菜单 `Tools > PerfAgent > API 探针` 会逐个探测本机实际可用的成员与计数器。

### 数据从哪来：Profiler 面板，而不是自己采样

以前的做法是工具自己逐帧采样（`EditorApplication.update` + `Stopwatch` + `GC.GetTotalMemory` +
`ProfilerRecorder`）。长期跑有三个问题，所以已改成**只读 Profiler 面板已记录的数据**：

| 问题 | 自采样 | 读面板 |
|---|---|---|
| 测量污染 | 采样回调本身就在给编辑器加开销，而它要测的正是「每帧开销」 | 分析时一次性读取，不介入运行 |
| 内存 | 跟随采集原上限 100 万帧，要一直维护帧缓冲 | 无缓冲，序列由面板持有 |
| 帧耗时口径 | `Stopwatch` 量编辑器 update 间隔，把编辑器停顿也算进去 | 面板 `HierarchyFrameDataView` 的 **PlayerLoop 行总耗时**，不含编辑器开销 |

具体映射：

```
帧范围      ProfilerDriver.firstFrameIndex / lastFrameIndex
整段序列    GetCounterValuesBatchByCategory(category, name, firstFrame, scale, buffer, ref max)
            → Memory/GC Allocated In Frame、Render/Draw Calls Count 等，窗口内每一帧都有值
单帧耗时    HierarchyFrameDataView 里 PlayerLoop 行的总耗时（取不到时退回 frameTimeMs，并注明）
逐帧明细    HierarchyFrameDataView（均匀抽样，默认最多 300 帧；热点排行只看 PlayerLoop 子树）
```

代价：**帧耗时分位是抽样统计**（默认 300/窗口帧数，会写在 `source` 里）；
面板帧历史默认约两千帧，超过就被面板丢掉；逐帧「GC 次数」与「TempAllocator 增长」
两个口径随自采样一起移除（面板没有这两个序列，会在快照提示里说明），
现在判断 GC 看的是「每帧分配 P95」与帧耗时尖峰。

两个跟面板打交道踩出来的坑，已写进代码注释与回归用例：

- **面板有「时间戳列」**：`[14]` 是帧起始时间戳（裸小数、量级 3.6e9、随帧递增），
  只靠「裸小数 = 耗时」会把它当成耗时列，于是「耗时列取最大值」得出 36 亿 ms/帧。
  现在列语义识别会把超过 `2000 ms` 的裸数列归为时间戳并排除；
  采样层与分析层还各有一道「荒谬读数不接受」的闸门。
- **有些计数器只在有人订阅时才逐帧记录**（典型是 `Memory/GC Allocated In Frame`）。
  自采样删掉后没人订阅它，面板序列就只有 1/300 帧有值（实测）。
  所以采集与基线测量期间会挂一个 `ProfilerRecorder` 把它「点亮」——只订阅、不读值，
  不构成逐帧采样。
- 附带：`GetAllStatisticsProperties()` 列出的属性在 2022.3 里 identifier 全是 -1，
  所以「用统计序列拿帧耗时」这条路在这个版本实际不可用，需要用就自己用探针确认。

**口径交叉校验**：托管分配同时用两条独立路径采集 ——
`ProfilerRecorder「GC Allocated In Frame」`（与 Profiler 窗口 GC Alloc 同源，主口径）
与 `GC.GetTotalMemory 差值`（弱口径，看不见「当帧分配后立即被回收」的部分）。
两者偏差超过 30% 时会在「提示」标签里同时写出两个数字，不隐藏差异；主口径不可用时明确降级并提示。

**每帧托管分配要扣掉一个基线**（重要）：
`GC Allocated In Frame` 统计的是**整个编辑器进程**当帧的托管分配 —— 里面包含 Inspector / SceneView / GUI、
Profiler 记录，以及本工具自己的采样与界面刷新。实证：同一个**空工程**、同一个场景，两次采集的 P50 分别是
`16871 B` 与 `97409 B`，**差 6 倍**，差的就是编辑器状态（窗口开没开、Profiler 是否在记录）。

所以每次采集前，工具会先在编辑模式空转一小会儿（约 0.2～1.5 秒，够 5 个样本就停），用同一个计数器量出「编辑器开销基线」，然后：

```
项目每帧分配（估算） = 每帧托管分配（实测，含编辑器开销） − 编辑器开销基线
```

- 三个数字都写进快照（指标 + 提示），可以自己核验；
- 预算只套在「项目每帧分配」上，且额外要求它越过基线的噪声带（`max(4 KB, 基线 × 25%)`）才会报「超出预算」；
- 没了基线（已经在 Play 里、取不到计数器）就**不报该结论**，只写一句「无法归因」——
  宁可不说，也不把编辑器自身的开销算到项目头上。
- 采集期间状态栏刷新限流到 4 Hz：逐帧拼字符串 + 重绘会把工具自己的开销算进被测对象的「每帧分配」。

**帧耗时抖动与尖峰归因：没有游戏侧证据就不冤枉项目**

编辑器里量到的「帧耗时」是编辑器 update 的墙钟间隔，包含编辑器自身的停顿
（资源刷新、窗口重绘、编辑器 GC、首次 Shader 编译）。
实测：一个空场景（无脚本、24 个 Draw Call）也出现过 **87 ms** 的孤立尖峰，
而起因帧是「进入 Play 的第 2 帧」，其余尖峰帧的分配（~16.9 KB）与 Draw Call（24）与普通帧**完全一样**。

所以：

- 跟随采集会丢掉**进入 Play 后的前 1 秒**（域重载 + 首次 Shader 编译 + 资源初始化），并在快照里写明丢了多少帧；
- 「帧耗时抖动」要同时满足：峰值 > P95×1.5、峰值 ≥ 33 ms（低于 30 FPS 才算卡顿），
  且**要么**尖峰帧的分配/Draw Call 明显高于普通帧（有游戏侧佐证），**要么**超阈帧数 ≥ max(10, 2%)（反复出现）；
- 只有前者没有佐证时，不报结论，只写一句「在编辑器内无法归因到项目」；
- 「尖峰帧归因」只在拿到佐证时才输出，分配对比优先用 `ProfilerRecorder` 口径（与主指标同源），
  证据里会写明用的是哪个口径。

---

## 配置 LLM（可选）

三个入口等价，改的都是同一份配置：菜单 `Tools > PerfAgent > LLM 配置`、
性能诊断面板工具栏 / 对话区右上角的「LLM 配置」、`Project Settings > PerfAgent`。

配置窗口提供：

- **服务商预设**：OpenAI / DeepSeek / 智谱 GLM / 阿里通义 / Ollama 本地 / 自定义，点一下自动填 Endpoint 与模型
- **测试连接**：确认 Endpoint、Key、模型名三者真能跑通，成功后会显示服务端回传的模型名
- **显示明文**开关、Temperature、工具调用轮数、最大输出 tokens
- **隐私开关**：纯本地模式、是否允许上传代码片段 / 资源路径

对话区上方有一条状态栏，实时显示当前是「纯本地模式 / 未配置 Key / 已就绪」，未配置时是黄字提醒 ——
不用等发完消息才知道没配。

### Endpoint 怎么写

只填域名就行，路径会自动补全：

| 你填的 | 实际请求 |
|---|---|
| `https://api.deepseek.com` | `https://api.deepseek.com/v1/chat/completions` |
| `https://api.deepseek.com/v1` | `https://api.deepseek.com/v1/chat/completions` |
| `https://open.bigmodel.cn/api/paas/v4` | `https://open.bigmodel.cn/api/paas/v4/chat/completions` |
| 完整端点 | 原样使用，不会重复拼接 |

配置窗口输入框下方会实时显示「实际请求」的 URL，`Project Settings` 页同理。

（原因：直接 POST 到根路径会得到 `404 Not Found` 且响应体为空，报错信息看不出是 URL 少写了路径。这个坑踩过一次，所以做成自动补全而不是写进文档要求用户记住。）

「测试连接」的报错会按 HTTP 码给方向：`404` = 路径不对，`401/403` = Key 无效，`400` = 多半是模型名不对。

### API Key 存在哪里

取值顺序：**界面填写的优先**，输入框留空时回退到环境变量。

| 方式 | 位置 | 说明 |
|---|---|---|
| 界面填写 | 本机 `EditorPrefs` | **优先使用**。不写进工程目录、不会被提交；换机器需重填 |
| 环境变量 | 进程环境 | 回退用。支持 `PERF_AGENT_API_KEY` / `DEEPSEEK_API_KEY` / `OPENAI_API_KEY`，适合 CI 与多人共用的机器 |

（早期版本是「环境变量优先」，但那样会出现「刚在界面上填了 Key 却一直 401」——被环境变量里的旧值静默覆盖，极难排查，所以改成界面优先。）

模型名请以官方文档为准 —— 官方会退役旧名（旧名往往仍可用一段时间），所以别把模型名写死在文档里。

**「测试连接」只发送固定字符串 `ping`** —— 不带 tools、不带快照、不带任何工程信息，可以放心点。

**不配 LLM 也完全可用**：规则引擎会独立产出带证据链的结论与一键修复计划，
断网、无 Key 时只是不能追问而已。

### 接入方式

没有使用（也无法使用）编辑器内置的 AI —— Unity 2022.3 没有对第三方插件开放的 AI 接口。
实现是自建的 OpenAI 兼容客户端：`UnityWebRequest` POST + `DownloadHandlerScript` 解析 SSE 流，
用 `EditorApplication.update` 轮询完成状态，零第三方依赖，同时兼容非流式响应。
因此任何 OpenAI 兼容服务（含本地 Ollama / vLLM）都能直接用。

---

## 用 MCP 驱动 PerfAgent（可选）

装了 [MCP for Unity](https://github.com/CoplayDev/unity-mcp) 的话，可以把 PerfAgent 暴露给外部 MCP 客户端
（Claude Desktop / VS Code Copilot 等），让它们自己触发采集、读取带证据链的结论与修复计划。

**默认关闭**，因为桥接代码必须带 `[McpForUnityTool]` 特性并引用 `MCPForUnity.Editor` 程序集 ——
这是**编译期硬依赖**，一旦启用后卸载 MCP 包就会编译报错。所以桥接做成了一个**独立的可选包**
（`PerfAgent.McpForUnity/`，与 PerfAgent 同级），主包保持零依赖、独立可用。

```
Tools > PerfAgent > MCP 集成 > 启用     校验 MCP 包已安装后，往 Packages/manifest.json 加一条本地包依赖
Tools > PerfAgent > MCP 集成 > 禁用     移除那条依赖（桥接包源码保留，随时可再启用）
Tools > PerfAgent > MCP 集成 > 状态     看当前状态与工具清单
```

启用/禁用就是往 `manifest.json` 加/删一行，**不搬任何文件**，随时可逆。
（最初用的是「重命名目录」方案，但目录句柄被文件监视器占用时会被拒绝访问 ——
实测 `IOException: Access to the path is denied`，编辑器内不可靠，已改为 UPM 依赖方式。）

**卸载 MCP 包之前务必先禁用本集成**，否则桥接包会编译报错（PerfAgent 主包不受影响）。

暴露的 11 个工具：

| 工具 | 作用 | 副作用 |
|---|---|---|
| `perf_list_snapshots` | 列出本机快照（id / 时间 / 帧数 / 结论数） | 无 |
| `perf_static_audit` | 静态审计，秒级，流水线最快的入口 | 无 |
| `perf_capture_start` | 开始抓帧，立即返回；`duration_seconds` 可改为按时长采 | **会占用编辑器若干秒** |
| `perf_capture_status` | 查抓帧进度；完成后返回快照摘要 | 无 |
| `perf_get_findings` | 取结论 + 完整证据链 | 无 |
| `perf_get_metrics` | 取指标 + 预算对比 + 数据来源 API | 无 |
| `perf_get_fix_plan` | 取一键修复计划（动作 id / 风险 / 影响面） | 无 |
| `perf_playmode_test_start` | 启动「自动走游戏循环并采性能」 | **编辑器会进入 Play 模式** |
| `perf_playmode_test_status` | 查自动测试进度（阶段 / 帧数 / 事件流水） | 无 |
| `perf_playmode_test_result` | 取自动测试的指标与结论 | 无 |
| `perf_playmode_test_cancel` | 中止自动测试并退出 Play | 无 |

### 自动走游戏循环（`perf_playmode_test_*`）

让外部 Agent 直接完成「进 Play → 跑一段 → 采数据 → 出结论」，不需要人守在编辑器前面：

```
# 只看一小段稳态：按帧数
perf_playmode_test_start(frames=300, warmup_frames=60, scene="Assets/Scenes/City.unity")

# 走完一整个游戏流程：按时长
perf_playmode_test_start(duration_seconds=300, warmup_frames=60, setup_method="Night.AutoPlay.RunAll")

  → 返回 job_id
perf_playmode_test_status(job_id)      # 轮询，建议每 2~3 秒一次
  → phase="capturing", captured_frames=8400
perf_playmode_test_result(job_id)      # 完成后取结论
  → metrics + findings + snapshot_id
```

参数：

| 参数 | 默认 | 说明 |
|---|---|---|
| `duration_seconds` | 0 | **>0 时按时长采集**，跑够这么多秒自动停；用于单场景跑完整个流程 |
| `frames` | 预算里的采样帧数 | 按帧数采集时的采样量；配合时长模式时它退化为**安全上限** |
| `warmup_frames` | 60 | 预热帧，丢弃启动抖动（JIT / 资源加载 / 场景初始化） |
| `timeout_seconds` | 240 | 兜底超时，到点强制退出 Play。**遍历/时长模式会自动放宽**（每个场景都会续期） |
| `scene` | 当前场景 | 单场景模式下先切到这个场景再测；切换前会问是否保存未保存的修改 |
| `setup_method` | 无 | 每个场景进 Play 后调用的无参静态方法（`命名空间.类型.方法`），用来把游戏摆到要测的状态 |
| `restore_scene` | true | 测完恢复测试前的场景 |

### 跟随采集：你自己操作，工具在旁边记录

这是给人用的模式 —— 你进 Play 自己玩，PerfAgent 在旁边记录，**时长不限**：

```
点面板工具栏「跟随采集」  → 待命
进入 Play 模式             → 自动开始记录（不用再点一次）
你自己操作：战斗、开背包、切界面、读档 …
点「跟随采集」再结束       → 立刻出快照（不会退出 Play，你可以接着玩）
```

也可以直接退出 Play —— 退出那一刻会自动收尾。但**推荐在 Play 里点停止**：
退出 Play 那一帧 `AssetDatabase` 已经不太可靠，资源审计可能拿不到数据。

为什么不自动进 Play：这个模式的价值就在于**操作是人做出来的**。脚本演不出玩家的操作节奏 ——
真实的按键间隔、突然的开镜、边走边翻背包，这些才是卡顿的真正来源。

| 模式 | 谁控制进 Play | 时长 | 适合 |
|---|---|---|---|
| **跟随采集** | **你** | **不限** | 真实操作过程、手感相关的问题 |
| 自动测试 | 工具 | 固定 N 秒 | 回归、版本对比、无人值守 |

一次一小时也没问题：一小时 60fps 大约 21 万帧（约 24MB），落盘时还会均匀抽取到 3000 条。
硬上限是 100 万帧（防止 Game 视图设成几千 fps 时把内存吃光），到了会自动收尾。

### 采集多久：帧数还是时长

### 采集多久：帧数还是时长

两种模式，对应面板工具栏的「采样帧数」与「采集时长(秒)」两个输入框：

| 模式 | 参数 | 适用 |
|---|---|---|
| 按帧数 | `frames` | 只想看一小段稳态表现 |
| 按时长 | `duration_seconds` | **走完一整个游戏流程** —— 流程长度按秒算，按帧数算既不准（帧率变了就不是同一段时间）也难换算 |

**上限**：单次 30 万帧 / 1 小时（时长模式到点就停，帧数上限只在极端低帧率下兜底）。
更长的建议分多次采集 —— 否则一份快照里会混进不同关卡，均值和分位就失去意义了。

**长采集不会撑爆快照文件**：逐帧明细落盘时会均匀抽取到 3000 条，
但**所有统计指标始终基于全部原始帧**计算，抽取只影响「帧」标签里曲线的密度。
快照里两个字段分开记：`capturedFrameCount` 是真实采集量，`stored_frames` 是落盘条数 ——
界面与 MCP 都读前者，否则会把 18000 帧显示成 3000 帧。

**为什么必须拆成 start / status / result 三段**：整个流程要穿过**两次域重载**（进 Play、退出 Play各一次），
MCP 连接会被切断，单次调用不可能阻塞等待几十秒。任务状态落在
`ProjectSettings/PerfAgent/PlayModeRuns/`，其中 `current.json` 指向最近一次，外部随时可读。

**几个刻意的设计**：

- **快照在退出 Play 之前就落盘。** 退出 Play 会再触发一次域重载，那之后代码只读文件、
  不依赖任何静态状态 —— 这是流程能走完的关键。
- **兜底超时。** 任何一步卡住（比如场景没加载出来、用户手动退出 Play）都会强制退出 Play，
  不会把编辑器留在 Play 模式里出不来。
- **采样口径与手动抓帧完全一致**（同一个 `FrameCapture` + `PerfPipeline`），
  所以自动跑出来的数字和面板里点「抓帧」得到的是同一套，不存在两套标准。
- **只测量、不修改。** 这里没有任何写工程资源的动作，与下面两条边界一致。

手动执行同一件事：`Tools > PerfAgent > 自动走 Play 模式并采集性能`，或面板工具栏的「自动测试」按钮。

### 两条边界

1. **没有、也不会有「执行一键修复」的 MCP 工具。** 外部模型可以读结论、看修复计划，
   但真正改工程资源必须由人在 Unity 面板里点击确认 —— 让外部模型直接批量改导入设置风险太大。
2. **数据同源。** MCP 返回的内容与面板、内置 Agent 走的是同一条 `PerfPipeline` + `PerfSnapshotStore`，
   不会出现「MCP 看到一套数、面板看到另一套」。

---

## 自动修复覆盖范围

每条结论都会产出修复计划。能安全机械完成的做成**可执行按钮**，不能的给出人工步骤。
下面这张表就是「哪些能自动」的准确边界 —— 它同时也是回归测试锁定的对象。

| 类别 | 可自动修复 |
|---|---|
| 纹理 | 关 Read/Write、改压缩、降 maxTextureSize、开 Mipmap Streaming |
| 模型 | 关 Read/Write、关自动碰撞体、关 BlendShapes、开网格压缩 |
| 音频 | 修正加载方式（Streaming / CompressedInMemory）、关预加载 |
| 工程设置 | 关 `Physics.autoSyncTransforms`、恢复 `fixedDeltaTime`、物理改回 FixedUpdate |
| 场景对象 | 关 `updateWhenOffscreen`、标记 Static、点光/聚光改硬阴影、副相机改 Depth Only、降粒子上限、粒子改 Local、动态非凸碰撞体改凸包 |
| Draw Call / SetPass | **生成 Sprite 图集**（收录工程内全部 Sprite 贴图） |
| 代码 | **不自动改**：只给「跳到问题位置」+ 人工步骤 —— 见下节 |

**代码类结论一律不自动改写。** 这是刻意画的边界：改代码需要理解上下文，
错一行就是编译不过或运行时崩，而机械替换的收益往往只是省几次字符串比较 ——
性价比不划算。所以本插件对代码只做诊断：**指出位置、给出改法**，动手的是你。

因此代码类结论的修复计划里只有两种步骤：

- **跳到问题位置** —— 直接打开 `文件:行`，省去手工搜索；
- **人工步骤** —— 附上这条反模式的具体改法与注意事项。

想要更具体的改法，用面板里的 LLM 追问（见「对话追问」一节），或者导出报告自己过一遍。

> **历史说明**：早期版本提供过两条改代码的路 —— 「机械替换」（`.tag == "X"` →
> `CompareTag("X")`、注释掉 `GC.Collect(...)`）与「AI 改写」（LLM 生成补丁后人工确认）。
> 两者现已移除。回归测试里有一条断言专门钉死「代码类结论不得出现执行按钮」，
> 避免以后又把这条线推过去。

### 安全约定

1. **代码只诊断、不改写。** 见上节；执行器的动作白名单里没有任何会写 `.cs` 文件的项。
2. **场景级结论会全场景扫描。** 审计只报了数量、没报具体对象时（如「3 个非凸 MeshCollider」），
   执行器扫描整个场景找符合条件的目标，而不是因为拿不到目标列表就放弃自动化。

---

## 已知限制

| 限制 | 说明 | 规避 |
|---|---|---|
| 编辑器内测量 | 数据包含编辑器自身开销，绝对帧耗时不可直接对标真机；每帧分配已扣除「编辑器开销基线」，但基线在编辑模式测、采集在 Play 模式，残差是估算值；帧耗时抖动需游戏侧佐证或反复出现才下结论 | 用 Development Build + Autoconnect Profiler；`ProfilerRecorder` 可打包进 Player |
| 扫描范围 | 脚本反模式只扫**会进到构建里**的脚本：插件自身安装目录、`Editor/` 目录、Editor-only 程序集、`Packages/` 全部跳过 | 空工程应得到 0 条代码问题；若扫到了插件自己的代码，那是 bug |
| 面板帧历史长度 | 一次采集能分析的帧数上限 = Profiler 面板自己的历史长度（默认约两千帧） | 在 Profiler 窗口把历史长度调大；或分多次采集分段分析 |
| 帧耗时是抽样统计 | 逐帧明细默认最多抽 300 帧（分位/峰值基于抽样），整段序列（分配 / Draw Call）则是全量 | 需要逐帧精确分位时把 `ProfilerPanel.MaxSamples` 调大（帧越多读面板越慢） |
| 快照保留 | 默认只保留最近 20 个快照（`保留快照个数` 可改，0 = 不清理）；面板左侧列表里每条右侧有 `✕` 可单删，顶部「清空全部」可一次清干净（均需确认） | 快照只占磁盘，不存在「一直占用内存」：UI 只持有当前打开的那一份（几十 KB） |
| 帧耗时有合理性闸门 | 单帧耗时超过 **2000 ms** 一律当成「读数不对」而不是「游戏很卡」：采样层丢弃该帧，规则层跳过全部帧率结论并写原因（实测踩过：面板列语义错位把一列字节数当耗时列，算出 36 亿 ms/帧） | 看快照提示里的原因；用 API 探针核对列内容 |
| Marker 采集是实验性的 | `HierarchyFrameDataView` 没有列名 / 列数 API，列语义只能从内容反推（实测 2022.3：`[1][2]` 百分比、`[3]` 调用次数、`[4]` GC Alloc、`[5][6]` 耗时且是**裸小数没有 ms 后缀**） | 已改为「先从多行内容识别列类型，再按类型取值」，并用探针实测数据写了回归用例；排行只统计 `PlayerLoop` 子树，不把编辑器开销算成项目热点 |
| 内存估算 | 纹理内存按导入设置推算（含 mipmap 增量），不是实测加载值 | 需要精确值时用 Memory Profiler 包 |
| 域重载 | 脚本重编译会重置对话历史 | 对话已落盘到 `ProjectSettings/PerfAgent/Conversations`，重开面板自动恢复上下文；不要恢复时点「新会话」 |
| `FrameTimingManager` | 需在 Player Settings 开启 Frame Timing Stats | 未开启时会在「提示」区说明 |
| 托管分配口径 | `GC.GetTotalMemory` 差值看不见「当帧分配后立即回收」的部分，会低估 | 已改为优先用 `ProfilerRecorder「GC Allocated In Frame」`；两路口径偏差 > 30% 时会在「提示」里写出两个数字 |
| Profiler 内部数据不可达 | Deep Profile 调用栈归因、内存快照分配来源、Frame Debugger 逐 Draw Call 细节、GPU 计数器都拿不到 | 不假装拥有：这些方向只给「用官方工具进一步验证」的指引，不编造数值 |
| Unity 内测试默认不启用 | 包的测试程序集只有列入 `Packages/manifest.json` 的 `testables` 才会被 Unity 编译 | 包内测试放在 `Tests~`（Unity 忽略），默认不影响工程编译；确实要跑时按 `Tests~/README.md` 的步骤启用 |

---

## 扩展方式

**加一条规则**：在 `PerfRuleEngine` 加一个 `EvaluateXxx(List<PerfFinding>, PerfSnapshot, PerfBudget)`，
用 `New(...)` 造结论、`Ev(...)` 挂证据，然后在 `Evaluate` 里调用。

**加一个采集器**：实现 `IPerfCollector`，注册到 `CollectorRegistry.All()`，
再在 `ToolRegistry` 加一个同名工具（同步即可）。

**加一个工具**：`ToolRegistry.All()` 里用 `Tool(name, desc, Schema(...), impl)`；
需要等待真实时间流逝的用 `AsyncTool`（参考 `capture_frames`）。

---

## 路线图

见 `开发计划.md`。P0 ~ P5 已实现（骨架 + 采集 + 规则 + Agent + UI + gold-standard 回归），
P6 已包含 UPM 包、CI、README 与示例。

## 回归测试

- 无 Unity License 的确定性回归（推荐）：运行 `dotnet run --project PerfAgent/Tests~/Standalone/PerfAgent.RuleRegression.csproj --configuration Release`。
- Unity 集成测试：`Tests~/Editor` 默认被 Unity 忽略，需按 `Tests~/README.md` 手动启用 `testables` 后才能在 Test Runner 里看到 `PerfAgent.Editor.Tests`。
- CI 使用独立入口，当前覆盖 19 个用例：
  - 五类故障定位：GC Alloc 爆炸 / Draw Call 过多 / 纹理内存泄漏 / Temp Allocator 增长 / 物理配置错误
  - 干净快照不得命中任何故障签名（防误报）
  - 对比引擎：同一快照拒绝对比、指标方向判定（FPS 下降算恶化）、2% 以下噪声不算回归、
    新增/消除/加重/减轻结论分离、消除 error 级结论必须给出改善判定
  - 修复计划：纹理 Read/Write 必须是低风险可撤销、去掉碰撞体必须标高风险、
    工程设置必须归为 project_setting、GC 分配类不得给出执行按钮、纹理内存超标要展开四类修复、
    未知问题类型必须回退为人工步骤而不是无按钮
  - 数据保真：与 Profiler 同源的口径必须优先、主口径不可用时必须返回 NaN 让上层显式降级（不得用弱口径冒充）、
    GC 事件的负差值不得污染分配均值

## 打包

```powershell
cd PerfAgent
npm pack
```

`.tgz` 会包含 `Editor/`、`Tests/`、`Tests~/`、`Samples~/`、`README.md`、`package.json`（`bin/`、`obj/` 由包根 `.gitignore` 排除）。
