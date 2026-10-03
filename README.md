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
`perf_static_audit`、`perf_capture_start` / `perf_capture_status`、`perf_playmode_test_*`、
`perf_follow_capture_*`、`perf_list_snapshots`、`perf_get_findings` / `perf_get_metrics` / `perf_get_fix_plan`。

桥接层**刻意只读**：没有任何「让外部模型直接改你工程」的入口，修改必须由人在面板里点确认。

## 这个仓库里没有的东西

`Library/`、`Temp/`、`Logs/`、`UserSettings/`、`*.csproj`、`*.sln` 都由 Unity 本机生成，已被 `.gitignore` 排除。
拉下来直接用 Unity 打开即可，不需要额外步骤。

## 许可

尚未添加 LICENSE 文件。在添加之前，默认保留所有权利（All rights reserved）。
如果要开源给他人使用，建议补一个（MIT / Apache-2.0 等）。
