using System;

namespace PerfAgent.Analysis
{
    /// <summary>
    /// 一份「代码修复建议」。
    ///
    /// 与 PerfFixExecutor 的关系：这是**建议**，不是动作。
    /// 它不会自己落盘 —— 必须由人看过原文与建议的对比之后才会写回文件。
    /// 为什么这么保守：机械替换（如 tag 比较）语义等价，机器可以自己做；
    /// 而这一批改动是模型「理解上下文后重写」的，错一行就是编译不过或者运行时崩，
    /// 不能跟前者享受同样的信任级别。
    /// </summary>
    public class PerfCodeFixProposal
    {
        public string findingId = "";
        public string file = "";
        public int line;

        /// <summary>触发这次建议的反模式 id（如 camera_main）。</summary>
        public string pattern = "";

        /// <summary>原文片段 —— 同时也是应用改动时的定位锚点。</summary>
        public string originalCode = "";

        /// <summary>建议替换成的内容。</summary>
        public string proposedCode = "";

        /// <summary>模型给出的改动说明（为什么这么改、有什么影响）。</summary>
        public string explanation = "";

        /// <summary>生成失败时的原因。</summary>
        public string error = "";

        public bool IsUsable
        {
            get
            {
                return string.IsNullOrEmpty(error)
                    && !string.IsNullOrEmpty(originalCode)
                    && !string.IsNullOrEmpty(proposedCode);
            }
        }
    }

    /// <summary>
    /// 从 LLM 回复里抽代码与说明。纯逻辑、零 Unity 依赖 —— 解析错了会让整条链路失效，
    /// 而模型的输出格式又不受控，所以这部分必须能被回归测试逐条钉死。
    /// </summary>
    public static class PerfCodeFixParser
    {
        /// <summary>
        /// 约定：第一个 fenced code block 是建议代码，其余文字是说明。
        ///
        /// 模型判断「无法安全自动化」时会不带代码块、只给文字说明 ——
        /// 这种情况返回 false，但说明照样带出来给用户看（这也是有价值的结果，
        /// 比强行给一段错代码好）。
        /// </summary>
        public static bool TryExtract(string response, out string code, out string explanation)
        {
            code = null;
            explanation = null;
            if (string.IsNullOrEmpty(response)) return false;

            int start = response.IndexOf("```", StringComparison.Ordinal);
            if (start < 0)
            {
                explanation = response.Trim();
                return false;
            }

            // 跨过 ``` 后面可能的语言标记（csharp / cs / C#）
            int lineEnd = response.IndexOf('\n', start);
            if (lineEnd < 0)
            {
                explanation = response.Trim();
                return false;
            }

            int end = response.IndexOf("```", lineEnd, StringComparison.Ordinal);
            if (end < 0)
            {
                explanation = response.Trim();
                return false;
            }

            string body = response.Substring(lineEnd + 1, end - lineEnd - 1)
                .Trim('\r', '\n', ' ', '\t');

            if (body.Length == 0)
            {
                // 空代码块当作「没给建议」处理
                explanation = response.Trim();
                return false;
            }

            code = body;

            string before = response.Substring(0, start).Trim();
            string after = response.Substring(end + 3).Trim();
            explanation = (before.Length == 0 ? "" : before + "\n\n")
                        + (after.Length == 0 ? "" : after);
            explanation = explanation.Trim();

            return true;
        }
    }
}
