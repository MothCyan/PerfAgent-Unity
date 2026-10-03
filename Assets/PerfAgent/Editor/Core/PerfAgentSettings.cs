using System;
using UnityEditor;
using UnityEngine;

namespace PerfAgent.Core
{
    /// <summary>性能预算：诊断的锚点。绝对数值没有意义，必须相对目标平台与预算判断。</summary>
    [Serializable]
    public class PerfBudget
    {
        [Header("帧率")]
        public float targetFrameRate = 60f;
        [Range(0.1f, 1.0f)] public float warnRatio = 0.8f;   // 占预算 80% 起 Warn

        [Header("CPU")]
        public long maxManagedAllocBytesPerFrame = 2048;      // 稳态每帧托管分配上限（0 = 零容忍）
        public double maxMainThreadMs = 10.0;                 // 主线程预算（0 = 由目标帧率推算）

        [Header("渲染")]
        public int maxDrawCalls = 300;
        public int maxSetPassCalls = 100;
        public long maxTriangles = 1000000;
        public int maxRealtimeShadowLights = 4;

        [Header("内存 (MB)")]
        public long maxTextureMemoryMB = 512;
        public long maxTotalMemoryMB = 2048;
        public long maxTempAllocatorMB = 64;
        public int maxAudioSourceCount = 24;

        [Header("采集")]
        public int captureFrames = 300;      // 每次抓帧采样帧数（时长模式下退化为安全上限）

        /// <summary>
        /// >0 表示按**时长**采集（秒），此时 captureFrames 变成安全上限。
        /// 「走完一整个游戏流程」是按秒描述的 —— 用帧数说既不准也难换算。
        /// </summary>
        public double captureSeconds = 0;

        public double FrameBudgetMs()
        {
            if (maxMainThreadMs > 0) return maxMainThreadMs;
            return targetFrameRate > 0 ? 1000.0 / targetFrameRate : double.NaN;
        }

        public PerfBudget Clone()
        {
            return (PerfBudget)MemberwiseClone();
        }
    }

    /// <summary>
    /// 插件配置。持久化在 ProjectSettings/ 下（不进 Assets，避免资源导入与误提交）。
    /// API Key 单独存在 EditorPrefs，绝不出现在配置资产里。
    /// </summary>
    // 注意路径写法：FilePathAttribute.Location 只有 ProjectFolder，它的含义是**工程根目录**，
    // 并不存在 ProjectSettingsFolder 这个枚举值（试过，编译报 CS0117）。
    // 所以要让配置落在 ProjectSettings 下，必须在字符串里显式加上 "ProjectSettings/" 前缀 ——
    // 否则文件会写进包目录（d:\<proj>\PerfAgent\PerfAgentSettings.asset），污染包、还可能被提交。
    [FilePath("ProjectSettings/PerfAgent/PerfAgentSettings.asset", FilePathAttribute.Location.ProjectFolder)]
    public class PerfAgentSettings : ScriptableSingleton<PerfAgentSettings>
    {
        /// <summary>
        /// 全局配置入口。
        ///
        /// 重要约束：**不要在 ScriptableObject 的构造函数或实例字段初始化器里访问它**。
        /// ScriptableSingleton 首次访问会调用 LoadSerializedFileAndForget，
        /// 而 Unity 禁止在 ScriptableObject 构造期加载资产，会抛：
        ///   UnityException: LoadSerializedFileAndForget is not allowed to be called from a
        ///   ScriptableObject constructor (or instance field initializer)
        /// 后果不只是抛异常：字段初始化会中途失败（对象半初始化），
        /// 并且同一次资产加载会被拒绝，连 UI Toolkit 的默认主题都加载不出来（控件变成白底白字）。
        /// EditorWindow 请把配置读取放到 OnEnable 之后，或惰性到用户交互时（参考 AgentLoop 的惰性系统提示词）。
        /// </summary>
        public static PerfAgentSettings Config { get { return instance; } }

        [Header("预算")]
        public PerfBudget budget = new PerfBudget();

        [Header("LLM（OpenAI 兼容协议：OpenAI / Azure / Ollama / 自建网关）")]
        public string endpoint = "https://api.openai.com/v1/chat/completions";
        public string model = "gpt-4o-mini";
        [Range(0f, 1f)] public float temperature = 0.2f;
        public int maxSteps = 8;             // 工具调用最大轮数，防失控
        public int maxOutputTokens = 1500;

        [Header("隐私")]
        public bool allowSourceCodeUpload = false;   // 是否允许把脚本片段发给远端
        public bool allowAssetPathUpload = true;     // 是否允许把资源路径发给远端
        public bool localOnlyNoLlm = true;           // 纯本地规则模式（不调用任何外部服务）

        [Header("其他")]
        public bool autoAnalyzeAfterCapture = true;

        internal const string ApiKeyPref = "PerfAgent.ApiKey";

        /// <summary>本插件专用的环境变量名。</summary>
        public const string ApiKeyEnvVar = "PERF_AGENT_API_KEY";

        /// <summary>
        /// 按优先级尝试的环境变量名。
        /// 除了本插件专用变量，也认常见的厂商变量 —— 用户本机往往已经设了
        /// DEEPSEEK_API_KEY 之类，没必要让人再设一遍。
        /// </summary>
        public static readonly string[] ApiKeyEnvVars =
        {
            ApiKeyEnvVar,
            "DEEPSEEK_API_KEY",
            "OPENAI_API_KEY",
        };

        /// <summary>从环境变量取到的 Key；没设置则返回空串。</summary>
        public string EnvApiKey
        {
            get
            {
                for (int i = 0; i < ApiKeyEnvVars.Length; i++)
                {
                    var value = Environment.GetEnvironmentVariable(ApiKeyEnvVars[i]);
                    if (!string.IsNullOrEmpty(value)) return value;
                }
                return "";
            }
        }

        /// <summary>实际生效的环境变量名（用于界面提示）；没设置则返回空串。</summary>
        public string EnvApiKeySource
        {
            get
            {
                for (int i = 0; i < ApiKeyEnvVars.Length; i++)
                {
                    var value = Environment.GetEnvironmentVariable(ApiKeyEnvVars[i]);
                    if (!string.IsNullOrEmpty(value)) return ApiKeyEnvVars[i];
                }
                return "";
            }
        }

        /// <summary>环境变量里是否提供了 Key（作为界面未填写时的回退）。</summary>
        public bool HasEnvApiKey { get { return !string.IsNullOrEmpty(EnvApiKey); } }

        /// <summary>只读本机 EditorPrefs 里存的 Key（不含环境变量），供配置界面回显用。</summary>
        public string StoredApiKey
        {
            get { return EditorPrefs.GetString(ApiKeyPref, ""); }
        }

        /// <summary>
        /// Key 的取值顺序：**界面上显式填过的（EditorPrefs）优先**，其次环境变量。
        ///
        /// 为什么不是「环境变量优先」：用户刚在配置窗口填了 Key，却被环境变量里的旧值静默覆盖，
        /// 表现是「填了没用、一直 401」，这类问题极难排查（实测踩过）。
        /// 环境变量作为回退已经够用：CI / 共用机器上不会有人去填 EditorPrefs。
        /// 想回到环境变量，把输入框清空即可。
        /// </summary>
        public string ApiKey
        {
            get
            {
                string stored = EditorPrefs.GetString(ApiKeyPref, "");
                if (!string.IsNullOrEmpty(stored)) return stored;

                var env = EnvApiKey;
                return string.IsNullOrEmpty(env) ? "" : env;
            }
            set { EditorPrefs.SetString(ApiKeyPref, value ?? ""); }
        }

        /// <summary>当前 Key 实际来自哪里（供配置界面显示）；都没配置时返回空串。</summary>
        public string ApiKeySource
        {
            get
            {
                if (!string.IsNullOrEmpty(StoredApiKey)) return "本机 EditorPrefs（界面填写）";
                if (HasEnvApiKey) return "环境变量 " + EnvApiKeySource;
                return "";
            }
        }

        public bool HasApiKey { get { return !string.IsNullOrEmpty(ApiKey); } }

        public void Save()
        {
            Save(true);
        }

        /// <summary>脱敏后的上下文摘要，便于排查配置问题（不含 Key）。</summary>
        public string Describe()
        {
            return "endpoint=" + endpoint + ", model=" + model
                 + ", apiKey=" + (HasApiKey ? ("已设置（来源：" + ApiKeySource + "）") : "未设置")
                 + ", localOnlyNoLlm=" + localOnlyNoLlm
                 + ", steps=" + maxSteps;
        }
    }
}
