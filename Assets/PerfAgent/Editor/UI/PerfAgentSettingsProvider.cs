using UnityEditor;
using UnityEngine;
using PerfAgent.Agent;
using PerfAgent.Core;

namespace PerfAgent.UI
{
    /// <summary>
    /// Project Settings &gt; PerfAgent 配置页（IMGUI，因为是 SettingsProvider 的要求）。
    /// API Key 存在 EditorPrefs（本机），不进工程目录，避免误提交。
    /// </summary>
    public class PerfAgentSettingsProvider : SettingsProvider
    {
        public PerfAgentSettingsProvider(string path, SettingsScope scopes) : base(path, scopes) { }

        [SettingsProvider]
        public static SettingsProvider Create()
        {
            var provider = new PerfAgentSettingsProvider("Project/PerfAgent", SettingsScope.Project);
            provider.keywords = new[] { "perf", "performance", "profiler", "agent", "性能", "AI" };
            return provider;
        }

        public override void OnGUI(string searchContext)
        {
            var cfg = PerfAgentSettings.Config;

            EditorGUILayout.Space(6);
            EditorGUILayout.LabelField("性能预算（诊断判定标准）", EditorStyles.boldLabel);
            EditorGUI.indentLevel++;
            cfg.budget.targetFrameRate = EditorGUILayout.FloatField("目标帧率 (fps)", cfg.budget.targetFrameRate);
            cfg.budget.warnRatio = EditorGUILayout.Slider("预警比例", cfg.budget.warnRatio, 0.1f, 1f);
            cfg.budget.maxManagedAllocBytesPerFrame = EditorGUILayout.LongField("每帧托管分配上限 (B)", cfg.budget.maxManagedAllocBytesPerFrame);
            cfg.budget.keepSnapshots = EditorGUILayout.IntField("保留快照个数（0 = 不清理）", cfg.budget.keepSnapshots);
            cfg.budget.maxDrawCalls = EditorGUILayout.IntField("Draw Call 预算", cfg.budget.maxDrawCalls);
            cfg.budget.maxSetPassCalls = EditorGUILayout.IntField("SetPass Call 预算", cfg.budget.maxSetPassCalls);
            cfg.budget.maxTriangles = EditorGUILayout.LongField("三角面预算", cfg.budget.maxTriangles);
            cfg.budget.maxTextureMemoryMB = EditorGUILayout.LongField("纹理内存预算 (MB)", cfg.budget.maxTextureMemoryMB);
            cfg.budget.maxTotalMemoryMB = EditorGUILayout.LongField("总内存预算 (MB)", cfg.budget.maxTotalMemoryMB);
            cfg.budget.maxTempAllocatorMB = EditorGUILayout.LongField("TempAllocator 预算 (MB)", cfg.budget.maxTempAllocatorMB);
            cfg.budget.maxRealtimeShadowLights = EditorGUILayout.IntField("实时阴影方向光上限", cfg.budget.maxRealtimeShadowLights);
            cfg.budget.maxAudioSourceCount = EditorGUILayout.IntField("AudioSource 数量上限", cfg.budget.maxAudioSourceCount);
            cfg.budget.captureFrames = EditorGUILayout.IntField("默认记录帧数", cfg.budget.captureFrames);
            EditorGUI.indentLevel--;

            EditorGUILayout.Space(10);
            EditorGUILayout.LabelField("LLM（OpenAI 兼容协议）", EditorStyles.boldLabel);
            EditorGUI.indentLevel++;
            cfg.endpoint = EditorGUILayout.TextField("Endpoint", cfg.endpoint);
            EditorGUILayout.LabelField(" ", "实际请求：" + LlmClient.NormalizeEndpoint(cfg.endpoint) +
                "（只填域名会自动补 /v1/chat/completions）", EditorStyles.miniLabel);
            cfg.model = EditorGUILayout.TextField("模型", cfg.model);

            // 只回显本机存储的 Key：环境变量里的 Key 不应该被显示到界面上
            var storedKey = cfg.StoredApiKey;
            var newKey = EditorGUILayout.PasswordField("API Key", storedKey);
            if (newKey != storedKey) cfg.ApiKey = newKey;

            EditorGUILayout.LabelField(" ",
                string.IsNullOrEmpty(cfg.ApiKeySource)
                    ? "界面填写优先；留空则回退到环境变量：PERF_AGENT_API_KEY / DEEPSEEK_API_KEY / OPENAI_API_KEY"
                    : "当前 Key 来源：" + cfg.ApiKeySource + "。把输入框清空即可回退到环境变量。",
                EditorStyles.miniLabel);

            cfg.temperature = EditorGUILayout.Slider("Temperature", cfg.temperature, 0f, 1f);
            cfg.maxSteps = EditorGUILayout.IntSlider("工具调用最大轮数", cfg.maxSteps, 1, 20);
            cfg.maxOutputTokens = EditorGUILayout.IntField("最大输出 tokens", cfg.maxOutputTokens);
            EditorGUI.indentLevel--;

            EditorGUILayout.Space(10);
            EditorGUILayout.LabelField("隐私", EditorStyles.boldLabel);
            EditorGUI.indentLevel++;
            cfg.localOnlyNoLlm = EditorGUILayout.Toggle("纯本地模式（不调用任何外部服务）", cfg.localOnlyNoLlm);
            cfg.allowSourceCodeUpload = EditorGUILayout.Toggle("允许上传代码片段", cfg.allowSourceCodeUpload);
            cfg.allowAssetPathUpload = EditorGUILayout.Toggle("允许上传资源路径", cfg.allowAssetPathUpload);
            EditorGUILayout.LabelField(" ", "关闭后，Agent 只能看到模式名与位置，看不到源码内容。", EditorStyles.miniLabel);
            EditorGUI.indentLevel--;

            EditorGUILayout.Space(10);
            EditorGUILayout.LabelField("其他", EditorStyles.boldLabel);
            EditorGUI.indentLevel++;
            cfg.autoAnalyzeAfterCapture = EditorGUILayout.Toggle("抓帧后自动分析", cfg.autoAnalyzeAfterCapture);
            EditorGUI.indentLevel--;

            EditorGUILayout.Space(12);
            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("保存", GUILayout.Width(100)))
                {
                    cfg.Save();
                    Debug.Log("[PerfAgent] 配置已保存。当前：" + cfg.Describe());
                }
                if (GUILayout.Button("运行 API 探针", GUILayout.Width(140)))
                {
                    PerfApiProbeWindow.Open();
                }
                if (GUILayout.Button("LLM 配置（含预设与测试连接）", GUILayout.Width(220)))
                {
                    PerfAgentSettingsWindow.Open();
                }
            }

            EditorGUILayout.Space(6);
            EditorGUILayout.HelpBox(
                "提示：诊断在编辑器内进行，测得的数据包含编辑器本身的开销。\n" +
                "「每帧托管分配」会自动扣除一个「编辑器开销基线」（采集前在编辑模式空转一小会儿实测），" +
                "只对扣除后的「项目每帧分配」下结论；没有基线时不报该结论。\n" +
                "用于最终确认时，请使用 Development Build + Autoconnect Profiler，或在真机上读取 ProfilerRecorder。",
                MessageType.Info);
        }
    }
}
