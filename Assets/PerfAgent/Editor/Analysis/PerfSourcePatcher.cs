using System;
using System.Text.RegularExpressions;

namespace PerfAgent.Analysis
{
    /// <summary>
    /// 源码补丁的纯逻辑部分：给定一行代码，算出改写后的一行。
    ///
    /// 刻意不引用 Unity、不碰文件系统 —— 改源码是所有修复动作里风险最高的，
    /// 必须能被回归测试逐条钉死。读文件与写回由 PerfFixExecutor 负责。
    ///
    /// 总原则：**能安全机械替换的才做，拿不准的一律返回 false 交给人工**。
    /// 返回 false 不是失败，而是「这行不该由机器擅自改」。
    /// </summary>
    public static class PerfSourcePatcher
    {
        // x.tag == "Foo"  /  x.tag != "Foo"
        static readonly Regex TagCompare = new Regex(
            @"(?<target>[A-Za-z_][\w\.\[\]<>]*)\s*\.\s*tag\s*(?<op>==|!=)\s*""(?<tag>[^""]*)""",
            RegexOptions.Compiled);

        // "Foo" == x.tag  /  "Foo" != x.tag
        static readonly Regex TagCompareReversed = new Regex(
            @"""(?<tag>[^""]*)""\s*(?<op>==|!=)\s*(?<target>[A-Za-z_][\w\.\[\]<>]*)\s*\.\s*tag\b",
            RegexOptions.Compiled);

        // GC.Collect(...) —— 行尾分号可选，因为可能是 if 里的单语句
        static readonly Regex GcCollect = new Regex(
            @"(?<indent>[ \t]*)(?<call>(?:System\.)?GC\.Collect\s*\([^)]*\)\s*;?)",
            RegexOptions.Compiled);

        /// <summary>x.tag == "A" → x.CompareTag("A")，!= 保留取反语义。</summary>
        public static bool TryPatchTagCompare(string line, out string patched)
        {
            patched = null;
            if (string.IsNullOrEmpty(line)) return false;
            if (IsComment(line)) return false;

            // 已经用了 CompareTag 的行不要再动，避免套娃
            if (line.Contains(".CompareTag(")) return false;

            string result = TagCompare.Replace(line, MatchTagCompare);
            if (result != line) { patched = result; return true; }

            result = TagCompareReversed.Replace(line, MatchTagCompare);
            if (result != line) { patched = result; return true; }

            return false;
        }

        static string MatchTagCompare(Match m)
        {
            string call = m.Groups["target"].Value + ".CompareTag(\"" + m.Groups["tag"].Value + "\")";
            return m.Groups["op"].Value == "!=" ? ("!" + call) : call;
        }

        /// <summary>
        /// 把独占一行的 GC.Collect(...) 注释掉（不删行，便于人工复核与逐行还原）。
        ///
        /// 只处理「整行除了这次调用什么都没有」的情况：混在表达式里
        /// （比如 if (x || GC.Collect() != null)）注释掉会直接改坏语法，交给人工。
        /// </summary>
        public static bool TryCommentGcCollect(string line, out string patched)
        {
            patched = null;
            if (string.IsNullOrEmpty(line)) return false;
            if (IsComment(line)) return false;

            var m = GcCollect.Match(line);
            if (!m.Success) return false;

            string rest = line.Remove(m.Index, m.Length);
            if (rest.Trim().Length != 0) return false;

            string indent = m.Groups["indent"].Value;
            patched = indent + "// [PerfAgent] 已注释：手动 GC 会把卡顿集中到调用点，应改为减少分配\n"
                    + indent + "// " + line.TrimStart();
            return true;
        }

        static bool IsComment(string line)
        {
            string trimmed = line.TrimStart();
            return trimmed.StartsWith("//", StringComparison.Ordinal)
                || trimmed.StartsWith("*", StringComparison.Ordinal)
                || trimmed.StartsWith("/*", StringComparison.Ordinal);
        }
    }
}
