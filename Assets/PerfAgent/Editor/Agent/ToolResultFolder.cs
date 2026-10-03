using System;
using System.Collections.Generic;
using PerfAgent.Utils;

namespace PerfAgent.Agent
{
    /// <summary>
    /// 对话上下文的压缩：把较早的工具结果折叠成占位符。
    ///
    /// 为什么需要：每轮请求都要把**整个消息历史**发给模型，而工具结果动辄几千字符。
    /// 一次 8 轮工具调用的诊断，输入 token 会按平方级增长 —— 这是整个流程里最贵的部分，
    /// 而且越到后面越贵（前面的结果被重复计费 N 次）。
    ///
    /// 为什么不能直接删消息：OpenAI 协议要求每个 assistant.tool_calls 都有配对的
    /// tool 消息，少一条后续请求直接 400。所以只换 content，结构原样保留。
    ///
    /// 为什么不把原文存档、再提供「取回」工具：所有工具都是**幂等**的，
    /// 模型想重看数据重新调一次就行，不值得为它加一套存储与生命周期管理。
    ///
    /// 纯逻辑、零 Unity 依赖，便于回归测试。
    /// </summary>
    public static class ToolResultFolder
    {
        /// <summary>保留最近这么多条工具结果的原文。</summary>
        public const int KeepRecent = 2;

        /// <summary>折叠时保留的预览长度。</summary>
        public const int PreviewChars = 200;

        /// <summary>返回被折叠的条数。</summary>
        public static int Fold(List<object> messages)
        {
            return Fold(messages, KeepRecent, PreviewChars);
        }

        public static int Fold(List<object> messages, int keepRecent, int previewChars)
        {
            if (messages == null || messages.Count == 0) return 0;

            var names = CallNames(messages);
            int folded = 0;
            int seen = 0;

            // 从后往前扫：最近的 keepRecent 条保持原文
            for (int i = messages.Count - 1; i >= 0; i--)
            {
                // 直接转换而不是走 MiniJson.AsDict —— 必须拿到**同一个**对象引用才能改写
                var target = messages[i] as Dictionary<string, object>;
                if (target == null) continue;
                if (MiniJson.Str(target, "role", "") != "tool") continue;

                seen++;
                if (seen <= keepRecent) continue;

                string content = MiniJson.Str(target, "content", "");
                if (content.Length <= previewChars) continue;   // 本来就很短，没必要动

                // 已经是折叠占位符就别再动：占位符本身比 previewChars 长，
                // 不挡一下的话每轮都会把它再包一层 —— 体积越滚越大，预览还会层层嵌套。
                if (content.IndexOf("\"folded\":true", StringComparison.Ordinal) >= 0) continue;

                string tool;
                if (!names.TryGetValue(MiniJson.Str(target, "tool_call_id", ""), out tool)
                    || string.IsNullOrEmpty(tool))
                    tool = "该工具";

                var stub = new Dictionary<string, object>();
                stub["folded"] = true;
                stub["tool"] = tool;
                stub["original_chars"] = content.Length;
                stub["preview"] = content.Substring(0, Math.Min(previewChars, content.Length));
                stub["note"] = "较早的工具结果已折叠以节省上下文。需要重看完整数据请重新调用 " + tool + "。";

                target["content"] = MiniJson.Serialize(stub);
                folded++;
            }

            return folded;
        }

        /// <summary>tool_call_id → 工具名（从 assistant 消息的 tool_calls 反查）。</summary>
        static Dictionary<string, string> CallNames(List<object> messages)
        {
            var names = new Dictionary<string, string>();

            for (int i = 0; i < messages.Count; i++)
            {
                var message = messages[i] as Dictionary<string, object>;
                if (message == null) continue;
                if (MiniJson.Str(message, "role", "") != "assistant") continue;

                var calls = MiniJson.AsList(MiniJson.Get(message, "tool_calls"));
                if (calls == null) continue;

                for (int k = 0; k < calls.Count; k++)
                {
                    var call = calls[k] as Dictionary<string, object>;
                    if (call == null) continue;

                    string id = MiniJson.Str(call, "id", "");
                    if (string.IsNullOrEmpty(id)) continue;

                    var fn = MiniJson.AsDict(MiniJson.Get(call, "function"));
                    names[id] = fn == null ? "" : MiniJson.Str(fn, "name", "");
                }
            }

            return names;
        }
    }
}
