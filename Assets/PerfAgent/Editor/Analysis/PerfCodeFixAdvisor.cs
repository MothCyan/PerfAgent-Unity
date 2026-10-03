using System;
using System.IO;
using System.Text;
using System.Globalization;
using UnityEditor;
using PerfAgent.Agent;

namespace PerfAgent.Analysis
{
    /// <summary>
    /// 用 LLM 为代码类结论生成修复补丁。
    ///
    /// 为什么不全部做成机械替换：那 20 种反模式里只有极少数能用正则安全改写
    /// （tag 比较、GC.Collect）。像「把 Camera.main 缓存成字段」需要插入字段 +
    /// 改初始化时机 —— 纯文本替换做不到，只能靠模型理解上下文。
    ///
    /// 为什么生成完不直接落盘：这是模型重写的代码，错一行就编译不过或运行时崩。
    /// 所以它只产出**建议**，必须由人对照前后代码之后再点应用。
    /// 这条线不能松：机械替换错了顶多是资源导入设置不对，代码改错了是整个工程编译不过。
    /// </summary>
    public static class PerfCodeFixAdvisor
    {
        /// <summary>送给模型的上下文行数（目标行前后各多少行）。</summary>
        public const int ContextLines = 25;

        const string SystemPromptText =
            "你是 Unity 性能优化专家，负责针对一处具体的代码反模式给出**最小改动**的修复方案。\n" +
            "你的输出会被人逐行审查之后才应用到工程里，所以：\n" +
            "- 宁可说「这里无法安全自动化」，也不要给一段看起来对、实际会改坏行为的代码；\n" +
            "- 不要顺手重构无关代码；\n" +
            "- 所有 API 必须真实存在于 Unity 2022.3，拿不准就别用。";

        public static bool Busy { get; private set; }

        // =====================================================================
        // 读上下文
        // =====================================================================

        /// <summary>
        /// 读取目标行附近的代码，**不做任何行号或箭头标记**。
        ///
        /// 为什么不加标记：模型会把标记连同代码一起抄回来，返回的片段就用不了了。
        /// 定位信息改用文字在提示词里说明（「第 N 行是 xxx」）。
        /// </summary>
        public static string ReadContext(string file, int line, out string targetLine)
        {
            targetLine = "";

            try
            {
                if (string.IsNullOrEmpty(file) || !File.Exists(file)) return "";

                var lines = File.ReadAllLines(file);
                int index = line - 1;
                if (index < 0 || index >= lines.Length) return "";

                targetLine = lines[index];

                int from = Math.Max(0, index - ContextLines);
                int to = Math.Min(lines.Length, index + ContextLines + 1);

                var sb = new StringBuilder();
                for (int i = from; i < to; i++) sb.Append(lines[i]).Append('\n');
                return sb.ToString();
            }
            catch
            {
                return "";
            }
        }

        // =====================================================================
        // 生成建议
        // =====================================================================

        public static void Propose(string findingId, string file, int line, string pattern,
            string suggestion, Action<PerfCodeFixProposal> onDone, Action<string> onError)
        {
            if (Busy) { onError("已有建议正在生成，等它结束再试。"); return; }

            var proposal = new PerfCodeFixProposal();
            proposal.findingId = findingId ?? "";
            proposal.file = file ?? "";
            proposal.line = line;
            proposal.pattern = pattern ?? "";

            string targetLine;
            string context = ReadContext(file, line, out targetLine);

            if (string.IsNullOrEmpty(context))
            {
                proposal.error = "读不到文件内容：" + file;
                onDone(proposal);
                return;
            }

            proposal.originalCode = context;

            string user = BuildPrompt(file, line, targetLine, pattern, suggestion, context);

            Busy = true;
            LlmClient.Ask(SystemPromptText, user, delegate (string text)
            {
                Busy = false;

                string code, note;
                if (PerfCodeFixParser.TryExtract(text, out code, out note))
                {
                    proposal.proposedCode = code;
                    proposal.explanation = note;
                }
                else
                {
                    // 模型判断「没法局部安全修」也是有效结果，如实告诉用户，
                    // 总比硬塞一段错代码强
                    proposal.explanation = note;
                    proposal.error = "模型没有给出代码改动，通常是它判断这里无法通过局部改动安全修复。";
                }

                onDone(proposal);
            }, delegate (string err)
            {
                Busy = false;
                proposal.error = err;
                onDone(proposal);
            });
        }

        static string BuildPrompt(string file, int line, string targetLine, string pattern,
            string suggestion, string context)
        {
            var sb = new StringBuilder();
            sb.Append("【反模式】").Append(pattern).Append('\n');
            if (!string.IsNullOrEmpty(suggestion)) sb.Append("【常见修法】").Append(suggestion).Append('\n');

            sb.Append("【文件】").Append(Path.GetFileName(file)).Append('\n');
            sb.Append("【问题所在行】第 ").Append(line.ToString(CultureInfo.InvariantCulture)).Append(" 行，内容是：\n");
            sb.Append("    ").Append(targetLine).Append("\n\n");

            sb.Append("【周围代码】\n```csharp\n").Append(context).Append("```\n\n");

            sb.Append("请给出**替换后的完整代码片段**，范围与我给出的「周围代码」完全一致。\n");
            sb.Append("约束：\n");
            sb.Append("1. 只改必要的部分，保持原有命名风格、缩进与注释\n");
            sb.Append("2. 不得改变外部行为（除非该行为本身就是问题所在）\n");
            sb.Append("3. 需要新增字段 / 方法时一并给出，放在同一段替换内容里\n");
            sb.Append("4. 不引入任何新依赖；工程是 Unity 2022，可用 C# 9\n");
            sb.Append("5. 用 ```csharp 代码块返回代码，代码块外用一两句话说明改了什么、有什么风险\n");
            sb.Append("6. 如果这里无法通过局部改动安全解决（比如需要引入对象池、改动架构），\n");
            sb.Append("   不要勉强给代码，直接说明原因与建议方向\n");
            sb.Append("7. 不要输出 diff 格式\n");
            return sb.ToString();
        }

        // =====================================================================
        // 应用 / 还原
        // =====================================================================

        /// <summary>
        /// 把建议写回文件。返回 null 表示成功，否则是错误信息。
        ///
        /// 定位方式是用 originalCode（**从文件读出来的原文**）做精确匹配，而不是行号：
        /// 生成建议之后用户可能又改了几行，行号早就偏了；精确匹配要么命中要么明确拒绝，
        /// 不会改错地方。命中多处时也直接拒绝 —— 猜错一处就是改坏别人的代码。
        /// </summary>
        public static string Apply(PerfCodeFixProposal proposal, out string undoContent)
        {
            undoContent = null;
            if (proposal == null || !proposal.IsUsable) return "没有可应用的改动。";

            try
            {
                string original = File.ReadAllText(proposal.file);

                // 统一成 \n 做匹配，写完再还原原行尾 —— 否则改一次会让整个文件的 diff 翻转
                string newline = original.Contains("\r\n") ? "\r\n" : "\n";
                string target = original.Replace("\r\n", "\n");
                string needle = proposal.originalCode.Replace("\r\n", "\n");

                int at = target.IndexOf(needle, StringComparison.Ordinal);
                if (at < 0) return "原文片段在当前文件里找不到（文件可能已被改动）。请重新生成建议。";

                if (target.IndexOf(needle, at + 1, StringComparison.Ordinal) >= 0)
                    return "原文片段在文件里出现了多次，无法确定改哪一处。请手工处理。";

                string replacement = proposal.proposedCode.Replace("\r\n", "\n");
                string updated = target.Substring(0, at) + replacement + target.Substring(at + needle.Length);

                if (newline == "\r\n") updated = updated.Replace("\n", "\r\n");

                undoContent = original;
                File.WriteAllText(proposal.file, updated);
                AssetDatabase.Refresh();
                return null;
            }
            catch (Exception e)
            {
                return "写入失败：" + e.Message;
            }
        }

        /// <summary>用应用前的整文件内容还原。代码改动一律整文件回滚，不做反向替换。</summary>
        public static string Revert(string file, string undoContent)
        {
            if (string.IsNullOrEmpty(file) || undoContent == null) return "没有可用的还原信息。";

            try
            {
                File.WriteAllText(file, undoContent);
                AssetDatabase.Refresh();
                return null;
            }
            catch (Exception e)
            {
                return "还原失败：" + e.Message;
            }
        }
    }
}
