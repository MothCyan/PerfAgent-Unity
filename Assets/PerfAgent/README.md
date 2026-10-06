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
2. 点工具栏「跟随采集」——然后**自己进 Play 操作**（战斗、开背包、切界面都算），
   想结束时再点一次（或直接退出 Play）；数据直接读 Profiler 面板已记录的帧，工具不做逐帧采样
3. **采集期间面板顶部会实时显示帧率波形**（FPS / P50 / P95 / 峰值），采完冻结保留，方便对着曲线回想卡在哪一段
4. 看「结论」标签：每条都带证据链、置信度、修复建议
5. 要贴给别人（或丢给外部 AI 继续追问）：点工具栏「复制结论」，整份 Markdown 进剪贴板；
   想让别人知道「哪些代码要改」就点「复制修复建议」（见下）
6. 想启用对话追问：点面板右上角「LLM 配置」填 Endpoint / 模型 / API Key（内置常见服务商预设与测试连接）
7. 没配 LLM 也能用：规则引擎独立产出完整报告（点「导出 HTML」）
8. 想验证「优化到底有没有效果」：在「对比」标签选一份基准快照，直接看恶化/改善指标与整体判定
9. 每条结论下方都有**修复计划**：可执行步骤带「执行」按钮，点击前会弹窗列出将要修改的目标；
   改完自动重跑审计，并在「变更」标签里留记录、可撤销

---

## 实时监视：按下「跟随采集」就能看到帧率在跳

采集期间面板上方会出现一条**实时帧率波形**（4 Hz 刷新，覆盖最近约 30 秒）：

```
● 采集监视中　58.3 FPS　帧耗时 P50 17.14 / P95 24.8 / 峰值 87.6 ms　已记录 812 帧　口径：面板帧号 / 墙钟
▂▁▂▁▁▇▂▁▂▁▂▁▂▁▂▁▂▁▂▁▂   ← 每根柱子 = 一次采样（≥33 ms 红、≥16.7 ms 黄、其余绿）
```

- 纵轴**至少**按 33 ms（30 FPS）铺满 —— 否则一个平稳的 60 FPS 过程会被画成满格噪点，看不出任何波动；
- 采完不消失：停下之后波形**冻结保留**，方便回忆「刚才卡在哪一段」；
- **口径写在图旁边**：实时曲线是「面板帧号 / 墙钟」（Δ面板帧 ÷ Δ秒），
  与快照里的帧耗时口径不同 —— 它包含编辑器自身的停顿，所以只能用来看**波动与趋势**，
  绝对数值以快照指标为准（逐帧精度的实时曲线要看能不能实测出正确的计数器名，见开发计划 P7-6）；
- 刷新限流 4 Hz：采集期间界面重绘本身也算被测对象的开销，刷太快等于自己污染测量。

## 把结论贴到别处：一键「复制结论」

工具栏「复制结论」把当前快照整份复制成 **Markdown** 进剪贴板（环境 / 指标 / 结论 / 证据链 / 修复计划），
直接粘进 issue、聊天窗口，或者丢给外部 AI 继续追问。
复制的内容与「导出 MD」是**同一份**，所以看到的和导出的一定一致。

## 想亲眼验证它准不准：前后对照样例工程

仓库根的 `Samples/` 里放了一个**单场景小游戏的两种版本**——同一个玩法，两套完整工程：

- `Samples/AngryBirds_before/`：优化之前的现场（每帧 `new List`、每帧 LINQ、`Camera.main`、
  每帧 `GetComponent`、每帧 `Debug.Log`、每帧 `Instantiate/Destroy`、手动 `GC.Collect`……共 16 处编号缺陷）；
- `Samples/AngryBirds_after/`：逐条修完之后（**功能完全一致**，每帧零分配、0 条编译警告）。

用法：用 Unity 打开 Before → Play →「跟随采集」玩 20~30 秒 → 生成快照；
再打开 After 同样采一份 → 在**对比**里选这两份快照，直接看帧时间 / 每帧分配 / 结论条数的变化。

- 缺陷编号与修法逐条对照：`Samples/PERF-FAULTS.md`
- 上游出处、MIT 许可与我们改了什么：`Samples/NOTICE.md`
- 怎么打开、怎么跑：`Samples/README.md`

## 为什么要接 AI 做 Agent（以及没有 AI 时它是什么）

先说结论：**这个工具在没有任何 LLM 的情况下就是完整可用的** —— 规则引擎独立产出结论、证据链与修复计划，
断网、没 Key、公司不允许数据外传时都能用。接 LLM 不是为了「更聪明地猜」，而是为了做**规则引擎做不到的那一半**。

| 只有 LLM 能做 | 具体例子 | 为什么规则做不到 |
|---|---|---|
| **把多个问题排成「先修哪个」** | 同时有 GC 尖峰、纹理超预算、TempAllocator 缓涨 → 先动哪个收益最大、改动最小 | 规则只能各自判「超没超阈值」，排序需要理解它们之间的相互影响与你的工程现状 |
| **串起跨系统的因果链** | 「每次开背包 → UI 重建 → 同时触发了图集加载 → 那一帧分配 12 MB」 | 要读代码、读调用关系、读资源依赖；写全了等于重造一个静态分析器 |
| **结合工程语义给建议** | 「`MonsterSpawner.Update` 里在 new List，改成复用字段即可」 | 规则知道「Update 里有分配」，但改法取决于这个类怎么用、生命周期如何 |
| **追问式排查** | 用户说「不是这里，我们把对象池关掉了」→ 换一条路径继续查 | 规则流程是固定的，没有「根据你的回答换方向」的能力 |
| **把结论翻译成可交付物** | 生成 issue 描述、PR 标题、给策划看的白话说明 | 同一条结论面向不同读者要不同措辞 |
| **自然语言入口** | 直接问「打团战时掉帧是为什么」，不用先学会看 Profiler | 规则需要人先知道看哪个指标 |

**AI 不能做的（写死在设计里，不靠提示词自觉）**

| 边界 | 落地方式 |
|---|---|
| 不许编数值 | 所有数值只能来自工具返回值；报告带幻觉校验；没数据就写「没有数据」，而不是估算 |
| 不许改工程 | Agent 工具集里**不存在**任何写操作入口；改工程只能通过面板按钮或 MCP 操作通道（需人批准的同意凭据），且执行/撤销全部写进 `Log/operations.jsonl` |
| 不许越过采样事实 | 数据不足（例如没测到编辑器开销基线）时必须写「无法归因」，不给一个偏小的数 |

一句话：**规则引擎负责「不撒谎」，LLM 负责「像个人一样帮你判断且能对话」**。前者让结论可信，后者让结论可用。

---

## 架构

```
UI 层         PerfAgentWindow / PerfApiProbeWindow / SettingsProvider
   ↓
Agent 层      AgentLoop（工具循环）· LlmClient（SSE 流式）· ToolRegistry（18 个工具）
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
| `Editor/Core/ProfilerOwnership.cs` | Profiler 开关的「借还」（含域重载兜底归还、面板历史上限） |
| `Editor/Core/PanelCapture.cs` | 跟随采集的记录窗口（开/停、进度、收尾读面板数据） |
| `Editor/Core/ProfilerPanel.cs` | 读面板已记录的帧（序列 / 帧视图，含荒谬读数闸门） |
| `Editor/Core/CaptureLiveStats.cs` | 采集期间的实时帧率采样（界面那条波形的数据源） |
| `Editor/Core/FrameWaveform.cs` | 实时波形环缓冲 + 分位统计（无 Unity 依赖，可独立回归） |
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
| `Editor/Agent/SopDefinition.cs` | AI 的 SOP：阶段划分 + 每阶段允许的工具 + 硬规则 |
| `Editor/Core/IAnalysisStore.cs` | 持久化接口（分析目录 + 操作日志；SQLite 后端换在这里） |
| `Editor/Core/JsonlAnalysisStore.cs` | 默认后端：JSONL + 给 AI 的历史摘要 |
| `Editor/Core/SecretProtection.cs` | 敏感数据保护接口（现在不加密，只留替换点） |
| `架构与SOP.md` | **架构唯一事实来源**：模块职责、MCP 通道、加密、持久化、AI SOP |
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
| 不自动执行 | 只有用户在结论卡片里点「执行」才会改东西；Agent 工具集里**不存在**修改入口。后续的 MCP 操作通道也必须带「人批准过的同意凭据」，且每次执行都写审计日志 |
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
| 内存 | 自采样要一直维护帧缓冲（上限 100 万帧） | 无缓冲，序列由面板持有 |
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

所以每次采集前，工具会先在编辑模式空转一小会儿（最多 4 秒，够 20 个样本或 5 个兜底样本就停），
用同一个计数器量出「编辑器开销基线」，然后：

```
项目每帧分配（估算） = 每帧托管分配（实测，含编辑器开销） − 编辑器开销基线
```

- 三个数字都写进快照（指标 + 提示），可以自己核验；
- 预算只套在「项目每帧分配」上，而且要同时满足三个条件才会报「超出预算」：
  ① 越过播放器预算；② 越过基线噪声带（`max(8 KB, 基线 × 25%)`）；
  ③ 越过**归因地板** `PerfBudget.editorPlayModeOverheadBytes`（默认 16384 B）。

  第 ③ 条是实测逼出来的：空场景（`SampleScene`，4 个 Light、24 个 Draw Call、**没有用户脚本**）
  在编辑器 Play 下实测 **14435 B/帧**，而编辑模式空闲基线只有 **465 B/帧** —— 两者不是一个量级，
  减完剩下的 13970 B/帧 仍然几乎全是编辑器自己的开销。以前这组数字会被报成
  「严重：项目每帧托管分配约 13970 B，超出预算 2048 B」，那是工具制造的假问题。
  过不了闸门时不报结论，而是在附录写清楚「数字是多少、卡在哪道闸、怎么确认（用 Player 构建版复核）」；
  脚本扫描命中的每帧分配反模式只作为**佐证**（命中 → 置信度 0.8，未命中 → 0.6 并说明“第三方/反射扫不到”），
  不拿它当闸门 —— 扫不到不等于没问题。
- **样本量不足不下统计结论**：窗口不足 30 帧（如只录到 10 帧就退出 Play）时跳过帧耗时/抖动/分配结论，
  只产出一条 Info 说明原因 —— 实测踩过：2 帧算出的 P50/P95/峰值会完全相同（2.27 / 2.3 / 2.3），
  看着精确、实际毫无信息量。资源 / 场景 / 代码审计不依赖帧数据，照常给出。
- **零值不当实测用**：`ProfilerRecorder` 的单点读数在编辑器里经常读出 0（计数器只在 Profiler 采到
  对应数据时才有值），以前会写成「Draw Calls 0 次」与另一行的「Draw Calls 均值 24 次」自相矛盾；
  现在面板序列优先，单点读数 ≤ 0 一律按「读不到」处理并在附录列出。
- 没了基线（已经在 Play 里、取不到计数器）就**不报该结论**，只写一句「无法归因」——
  宁可不说，也不把编辑器自身的开销算到项目头上。
- 采集期间状态栏刷新限流到 4 Hz：逐帧拼字符串 + 重绘会把工具自己的开销算进被测对象的「每帧分配」。
- **编辑模式下必须开 `ProfilerDriver.profileEditor`**（测完会自动还原成你原来的设置）。
  只开 `ProfilerDriver.enabled` 的话，面板**不记录编辑器帧**，`lastFrameIndex` 一动不动，
  测出来就是「面板只录到 0 帧」—— 这是实际踩过的坑。测量期间若你直接进了 Play，
  就只用打断之前那段编辑模式帧；不够就作废（宁可不设基线，也不把 Play 帧当空转开销）。
- 拿不到基线时会写出具体卡在哪：面板录到几帧、兜底口径采到几个样本、是否在编译/导入。
- **Profiler 开关是「借了必还」的**（`ProfilerOwnership`）：记录期间把 Profiler 的面板历史上限
  压到 2000 帧，归还时还原你原来的设置、并清掉我们录下来的帧数据。
  为什么必须这么较真：进 Play 会触发域重载，静态字段连同收尾回调一起消失，
  开关一旦留在「开」的状态，编辑器就会一直逐帧记录，Profiler 帧数据持续膨胀 ——
  系统内存告急时 Unity 会先丢数据并报
  `The system is running out of memory … Discarding profiler frames data.`
  因此新域加载时还有一道兜底归还：看到「上一次会话没有归还 Profiler 开关」的警告，
  说明刚才确实被域重载打断了，插件已经自动还原。

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

**不配 LLM 也完全可用**，而且**纯本地模式是真的本地**：

| 开关状态 | 你提问时发生什么 |
|---|---|
| **纯本地模式（默认）** | 由**本地规则引擎**回答：按问题里的关键词找维度（帧率 / 渲染 / 内存 / 资源 / 代码 / 物理 / 场景 / 采集），把该维度已经算好的结论、数字与证据链列出来。**不发任何网络请求** |
| 关掉纯本地、但没配 Key | 同上（即使想联网也发不出去） |
| 关掉纯本地、配了 Key | 交给 LLM：能理解自由表达、做跨维度推理、写给人看的解释；代价是把工具结论发给你配置的服务商 |

「纯本地模式」是在**发字节那一层**强制的（`LlmClient` 里的硬闸门），不是只改 UI 文案 ——
面板对话、Agent 循环、以后接入的 MCP 工具都绕不过它。

规则引擎的边界也写清楚，免得误以为它听得懂：它**不理解句子意思**，只做关键词匹配，
也不做跨维度推理（「为什么只有在战斗时才卡」这类问题它答不了）；本地回答的末尾会自己注明这条边界。

### 开了 AI 之后，你多看到什么

不用靠文档解释 —— 每条提问都会**先贴「本地规则引擎」的结论**，开了 AI 再在下面**加**一段「AI 解释」：

```
**我**：为什么只有战斗时才卡？

**本地规则引擎**（规则算出来的结论与数字，可逐条回溯）
  命中维度：帧率（按关键词匹配，不做语义理解）
  ⚠ 这是解释类问题：本地只能给事实与证据，给不出因果、取舍与优先级
  1. [警告] 帧耗时抖动明显：峰值 42.1 ms 是 P95（14.2 ms）的 3 倍
     证据：帧耗时峰值 = 42.1 ms（预算 P95×1.5 = 21.3 ms）
  相关数字：帧耗时均值 8.4 ms / P95 14.2 ms / 实际 FPS 119

**AI 解释（gpt-4o-mini）**
  尖峰集中在战斗阶段，且尖峰帧的托管分配比普通帧高一个量级 —— 这更像
  战斗开始时批量生成对象/特效造成的分配峰值触发了 GC，而不是 Draw Call 问题
  （Draw Call 全程稳定在 412）。建议先查战斗初始化路径上的容器分配……
```

一句话分工：**本地给事实与证据（不联网、可回溯），AI 给因果、取舍与人话（联网、由你开）。**
纯本地模式下，解释类问题的回答会**在开头一句话**告诉你能答到哪一步，不装作听懂了
（本地引擎的完整边界写在对话卡的 tooltip 里，不每条回答都重复一遍）。

AI 拿到的是本地结论的事实底稿（`LocalAnswer.BriefForPrompt`），所以它引用的数字与本地一致，
不会自己编一个 —— 而且少一轮工具调用，更快也更便宜。

## 「复制修复建议」：本地清单 vs AI 改法

工具栏（以及「代码」标签页顶部）的「复制修复建议」有两种输出，状态栏会告诉你这次是哪种：

| 状态 | 复制到什么 | 说明 |
|---|---|---|
| **本地模式 / 没配 Key** | 按文件分组的清单：文件:行 + 反模式 + 规则建议 + 相关性能结论 | 这是**扫描出来的事实**，可核对；不含「改成什么代码」 |
| **已开 AI** | 含**可替换代码**的修复清单（改前 → 改后），自动进剪贴板并回写到对话区 | 这是**建议**：模型会出错，改之前自己看一遍 |

两条与隐私有关的实话：

- 关着「允许上传代码片段」时，**一个字符的源码都不会发** —— 只发文件:行与模式名，
  状态栏会写明「未上传代码片段」，AI 也会被告知信息不足时直说要上下文；
- 只要内容真的发出去了，就在 `ProjectSettings/PerfAgent/Log/operations.jsonl` 里留一条记录
  （actor=human，kind=ai，含「已发送/未发送代码片段」）。

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

暴露的 8 个工具：

| 工具 | 作用 | 副作用 |
|---|---|---|
| `perf_list_snapshots` | 列出本机快照（id / 时间 / 帧数 / 结论数） | 无 |
| `perf_static_audit` | 静态审计，秒级，流水线最快的入口 | 无 |
| `perf_get_findings` | 取结论 + 完整证据链 | 无 |
| `perf_get_metrics` | 取指标 + 预算对比 + 数据来源 API | 无 |
| `perf_get_fix_plan` | 取一键修复计划（动作 id / 风险 / 影响面） | 无 |
| `perf_follow_capture_start` | 进入跟随采集待命：**用户自己进 Play 操作**，工具在旁边记录 | 无 |
| `perf_follow_capture_status` | 查跟随采集状态（待命 / 采集中 / 已记录帧数） | 无 |
| `perf_follow_capture_stop` | 结束采集并出快照（**不会退出 Play**） | 无 |

### 跟随采集：你自己操作，工具在旁边记录

这是本工具集里**唯一**能拿到运行时数据的入口，而且它**不控制 Play 模式** ——
你进 Play 自己玩，PerfAgent 在旁边记录，**时长不限**：

```
点面板工具栏「跟随采集」  → 待命
进入 Play 模式             → 自动开始记录（不用再点一次）
你自己操作：战斗、开背包、切界面、读档 …
点「跟随采集」再结束       → 立刻出快照（不会退出 Play，你可以接着玩）
```

也可以直接退出 Play —— 退出那一刻会自动收尾。但**推荐在 Play 里点停止**：
退出 Play 那一帧 `AssetDatabase` 已经不太可靠，资源审计可能拿不到数据。

为什么不自己进 Play：这个模式的价值就在于**操作是人做出来的**。脚本演不出玩家的操作节奏 ——
真实的按键间隔、突然的开镜、边走边翻背包，这些才是卡顿的真正来源。

MCP 侧的正确用法：`perf_follow_capture_start` 进入待命 → **提示用户去操作（不要替他按 Play）**
→ 轮询 `perf_follow_capture_status` → 用户玩完后再 `perf_follow_capture_stop`。
这三个工具都**不会**替你进 Play，也不会替你退出。

### 采集多久：时长不限，但受面板帧历史限制

跟随采集没有固定时长：从你进入 Play 开始记录，到退出 Play（或手动停止）结束。

- 单次硬上限 `FollowCapture.MaxFrames = 20000` 帧，只是防内存失控的安全阀；
- 真正的边界是 **Profiler 面板自己的帧历史长度**（Unity 默认约两千帧，更早的会被面板丢掉）——
  所以「一次几个小时」这种超长采集拿不到全程，需要中途收尾再开一次；
- 逐帧明细落盘时会均匀抽取到 3000 条，但**所有统计指标始终基于全部原始帧**计算，
  抽取只影响「帧」标签里曲线的密度。快照里 `capturedFrameCount` 是真实采集量、
  `stored_frames` 是落盘条数，界面与 MCP 都读前者，否则会把 18000 帧显示成 3000 帧；
- 进入 Play 后的**前 1 秒会被丢掉**（域重载 + 首次 Shader 编译 + 资源初始化都集中在那里），
  快照里会写明丢了多少帧；
- **只测量、不修改。** 这里没有任何写工程资源的动作，与下面两条边界一致。

### 两条边界

1. **没有、也不会有「执行一键修复」的 MCP 工具。** 外部模型可以读结论、看修复计划，
   但真正改工程资源必须**过人这一关** —— 要么人在 Unity 面板里点确认，要么走 MCP 操作通道并带上人批准过的同意凭据（每次执行都写审计日志，可撤销）。
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

**加一个工具**：`ToolRegistry.All()` 里用 `Tool(name, desc, Schema(...), impl)`。
工具必须同步返回 —— 需要真实时间流逝的能力（抓帧）已随两个采集模式一起移除。

---

## 路线图

见 `开发计划.md`。

- **P0 ~ P5** 已实现（骨架 + 采集 + 规则 + Agent + UI + gold-standard 回归）；
- **P6** 已包含 UPM 包、CI、README 与示例（`.tgz` 发布待做）；
- **P7 起**是后续计划：采集完整性（分段采集 / 面板清空检测 / 口径补全）、真机与流水线
  （Development Build + Autoconnect、CLI/CI）、规则与修复面扩展、工程化发布。
  当前最大的功能边界是 **面板帧历史约 2000 帧** —— 长会话只能覆盖最近一段，P7-1 专门解决它。

## 流程追溯：日志与目录

每次分析、每次改工程都会落盘，方便事后追责与给 AI 当上下文：

```
ProjectSettings/PerfAgent/Log/index.jsonl       分析目录：哪次分析 / 场景 / 帧数 / 结论数 / 数据来源
ProjectSettings/PerfAgent/Log/operations.jsonl  操作日志：改了什么 / 谁发起 / 谁同意 / 结果 / 能否撤销
```

- 两边都是 JSONL，一行一条，`type index.jsonl` 就能读，也能整份丢给模型当上下文；
- `operations.jsonl` 的 `consent` 列回答「**谁授意的**」：空 = 面板上人工确认，`mcp:<client>` = 外部客户端经同意门批准；
- 最近的分析与操作会被压成摘要自动接进系统提示词，AI 不需要从零猜「已经做过什么」；
- 后端是可换的：实现 `IAnalysisStore` 即可换成 **SQLite**（表结构照 record 字段建，用 JSONL 回灌），
  上层一行不用改；默认先用 JSONL 是因为它零依赖、可读、可直接当上下文；
- 敏感数据（API Key、导出包）的保护走 `ISecretProtector`：**现在是明文并会明确标注**，
  接入 DPAPI / Keychain / libsecret 时分只需注册一个实现（见 `架构与SOP.md` 第五节）。

> 模块划分、MCP 操作通道、加密与持久化接口、AI 的标准操作流程（SOP）全部写在 `架构与SOP.md`。

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
