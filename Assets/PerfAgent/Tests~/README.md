# P5 Gold Standard 回归集

该测试集以人工构造的 `PerfSnapshot` 作为确定性输入，避免编辑器负载、硬件和当前场景造成波动。

## 故障定位

| 故障 | 输入信号 | 预期定位 |
|---|---|---|
| GC Alloc 爆炸 | 每帧托管分配 64 KB | `gc_alloc_per_frame` / 内存 |
| Draw Call 过多 | Draw Calls = 800 | `draw_calls_over` / 渲染 |
| 纹理内存泄漏 | 纹理内存估算 256 MB + 问题资源 | `texture_memory_over` / 内存 |
| Temp Allocator 增长 | 60 帧从 8 MB 单调增长 | `temp_allocator_growth` / 内存 |
| 物理配置错误 | `autoSyncTransforms = true` 审计项 | 场景/Physics 配置结论 |

每个正向用例同时验证严重级别和证据链；干净快照用例用于防止五类签名误报。

## 快照对比（P4）

| 用例 | 验证点 |
|---|---|
| 同一快照对比 | 必须判定为「不可对比」并给出原因 |
| 指标方向 | FPS 下降 = 恶化，Draw Call 上升 = 恶化（方向由规则给出，不看正负号） |
| 噪声门限 | 1% 变化低于 2% 显著性阈值，不计入回归 |
| 无方向指标 | 既无预算也无成本关键词的指标不做好坏判定 |
| 结论分类 | 新增 / 消除 / 加重 / 减轻 / 未变必须分开 |
| 加权判定 | 消除 error 级结论必须得到改善判定 |

## 一键修复计划（P4）

| 用例 | 验证点 |
|---|---|
| 纹理 Read/Write | 必须是低风险、可撤销、批量的可执行步骤 |
| 同一 code 不同文案 | 不得被拆成多条结论（分组稳定性） |
| 去掉模型碰撞体 | 必须标为高风险，且附带人工确认步骤 |
| GC 每帧分配 | 只能给导航 + 人工步骤，**不得**出现执行按钮 |
| 未知问题类型 | 没有执行器时必须回退为人工步骤，绝不假装能一键修 |
| 纹理内存超标 | 要展开成四类纹理修复（Read/Write、压缩、尺寸、Streaming） |
| 计划排序 | error 级结论必须排在前面 |

这些断言保证的是「计划不会误导用户」，比「能不能执行」更重要。

## 数据保真度

| 用例 | 验证点 |
|---|---|
| 主口径优先 | 有 `ProfilerRecorder「GC Allocated In Frame」` 时必须用它（与 Profiler 窗口同源），差值口径保留作交叉校验 |
| 降级要显式 | 主口径不可用时必须返回 `NaN` 让上层降级，不得用弱口径数字冒充 |
| GC 事件识别 | `GC.GetTotalMemory` 负差值要识别为 GC 事件，且不得污染分配均值 |
| 缺失要留痕 | 拿不到数据必须写进快照 notes，并给出可操作的原因说明 |

## 运行方式

### 1. Unity EditMode（默认不启用）

Unity 只会编译**列入 `testables` 的包**里的测试程序集，所以 `Tests~/Editor` 默认被 Unity 完全忽略（`~` 结尾的目录不参与导入），
这样包的引入不会影响工程编译。

要在 Unity 里跑这 10 个 EditMode 测试，需要两步：

```powershell
# 1. 目录改名，让 Unity 能看到 asmdef
Move-Item PerfAgent/Tests~/Editor PerfAgent/Tests/Editor
```

```json
// 2. Packages/manifest.json 增加 testables
"testables": ["com.night.perfagent"]
```

然后：

`Unity.exe -batchmode -projectPath <project> -runTests -testPlatform EditMode -testFilter PerfAgent.Tests -testResults <path> -quit`

> **动手前必读**：启用 `testables` 会让 Unity 开始编译 `UnityEngine.TestRunner`。
> 如果工程里 `com.unity.test-framework` 存在多个版本（例如同时残留 1.1.x 与 1.4.x），
> TestRunner 会挂到错误版本的 `nunit.framework.dll` 上，产生上千条 `CS0246`（缺 `ITestExecutionContext` / `TestCommand` 等），
> 并连带让整个 Editor 程序集编译失败、包菜单都不出现。
> 先执行 `Window > Package Manager` 把 test-framework 与 `com.unity.ext.nunit` 收敛到同一套版本，再启用 `testables`。

### 2. 独立回归（推荐，无需 Unity）

`dotnet run --project PerfAgent/Tests~/Standalone/PerfAgent.RuleRegression.csproj --configuration Release`

该入口直接编译 `PerfAgent/Editor/Core` 与 `PerfAgent/Editor/Analysis` 下的生产源码（仅由 `UnityStubs.cs` 补足 Unity 属性与编辑器类型桩），因此不依赖 Unity 许可证与编辑器负载，可在 CI 中稳定复现，且不会复制规则实现。

`Tests~` 目录以 `~` 结尾，Unity 会忽略它，不会把桩类型导入编辑器程序集。

回归结果应为 `43/43 passed`，任一用例失败时进程返回码为 `1`。