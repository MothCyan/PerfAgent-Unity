using System;
using System.Collections.Generic;
using System.Globalization;
using PerfAgent.Utils;

namespace PerfAgent.Analysis
{
    public enum CodePatchStatus
    {
        /// <summary>已生成，等人在面板里确认。</summary>
        Pending,
        Applied,
        Reverted,
        Failed,
    }

    /// <summary>
    /// 一份「由 LLM 生成、等人工确认」的代码改动建议。
    ///
    /// 为什么是 oldCode / newCode 而不是行号 diff：
    /// 让模型算行号几乎必然出错，而让它原样抄一遍要改的代码、再给出替换版本，
    /// 我们可以用**精确字符串匹配**来落地 —— 匹配不上就拒绝，绝不会改错位置。
    ///
    /// 三条安全底线（都在 TryApply 里强制）：
    ///  1. oldCode 必须在文件里**精确**匹配到；
    ///  2. 必须**唯一**匹配 —— 出现两次就拒绝，宁可不动也不能改错地方；
    ///  3. 行尾差异要归一化处理，但不能把文件改成混合行尾。
    ///
    /// 本类不引用 UnityEngine，便于被独立回归工程直接覆盖。
    /// </summary>
    public class CodePatch
    {
        public string id;
        public string findingId;

        /// <summary>工程相对路径，如 Assets/Scripts/Foo.cs。</summary>
        public string filePath;

        /// <summary>模型给的一句话改动理由，用于面板上展示。</summary>
        public string rationale;

        public string oldCode;
        public string newCode;

        public string createdAtUtc;
        public string appliedUtc;

        /// <summary>应用前的原文件备份路径，撤销时用。</summary>
        public string backupPath;

        public CodePatchStatus status = CodePatchStatus.Pending;
        public string error;

        public string StatusText()
        {
            switch (status)
            {
                case CodePatchStatus.Pending: return "待确认";
                case CodePatchStatus.Applied: return "已应用";
                case CodePatchStatus.Reverted: return "已撤销";
                case CodePatchStatus.Failed: return "失败";
                default: return "未知";
            }
        }

        /// <summary>改动规模：新增行 / 删除行，用于面板上快速判断改动大小。</summary>
        public string SizeText()
        {
            int oldLines = CountLines(oldCode);
            int newLines = CountLines(newCode);
            int delta = newLines - oldLines;

            if (delta == 0) return oldLines + " 行";
            return oldLines + " → " + newLines + " 行（" + (delta > 0 ? "+" : "") + delta + "）";
        }

        public static int CountLines(string s)
        {
            if (string.IsNullOrEmpty(s)) return 0;
            int lines = 1;
            for (int i = 0; i < s.Length; i++)
                if (s[i] == '\n') lines++;
            return lines;
        }

        // =====================================================================
        // 应用与撤销
        // =====================================================================

        /// <summary>把补丁应用到文件内容上。失败时返回 false 且不改动任何东西。</summary>
        public bool TryApply(string source, out string result, out string error)
        {
            result = null;

            if (!TryApplyInternal(source, out result, out error)) return false;
            return true;
        }

        bool TryApplyInternal(string source, out string result, out string error)
        {
            result = null;
            error = null;

            if (source == null) { error = "读不到文件内容。"; return false; }
            if (string.IsNullOrEmpty(oldCode)) { error = "补丁没有给出原始代码块，无法定位。"; return false; }
            if (string.Equals(oldCode, newCode, StringComparison.Ordinal))
            {
                error = "新旧代码完全一致，这个补丁没有任何改动。";
                return false;
            }

            int index;
            int found = Locate(source, oldCode, out index);

            if (found == 0)
            {
                error = "在文件里找不到补丁对应的原始代码 —— 文件可能已被改动，建议重新生成补丁。";
                return false;
            }
            if (found == 2)
            {
                error = "原始代码在文件里出现了多次，无法确定该改哪一处。请缩小改动范围后重新生成。";
                return false;
            }

            // 用文件里实际那段代码的行尾来统一新代码，避免改完变成混合行尾
            string matched = source.Substring(index, oldCode.Length);
            string normalizedNew = MatchLineEndings(newCode, matched);

            result = source.Substring(0, index) + normalizedNew + source.Substring(index + oldCode.Length);
            return true;
        }

        /// <summary>
        /// 在文件里定位原始代码块。
        /// 返回 0 = 没找到，1 = 唯一命中，2 = 多处命中（调用方必须拒绝）。
        ///
        /// 会依次尝试三种行尾写法：原样、全 LF、全 CRLF。
        /// 原因是 LLM 返回的多半是 LF，而 Windows 上保存的文件是 CRLF ——
        /// 不做归一化的话，明明正确的补丁会一直「找不到原始代码」。
        /// </summary>
        static int Locate(string source, string needle, out int index)
        {
            index = -1;
            if (string.IsNullOrEmpty(needle) || source.Length < needle.Length) return 0;

            var variants = new List<string>(2);
            variants.Add(needle);

            if (needle.IndexOf('\r') >= 0)
            {
                string lf = needle.Replace("\r\n", "\n");
                if (!string.Equals(lf, needle, StringComparison.Ordinal)) variants.Add(lf);
            }
            else if (needle.IndexOf('\n') >= 0)
            {
                variants.Add(needle.Replace("\n", "\r\n"));
            }

            for (int v = 0; v < variants.Count; v++)
            {
                string candidate = variants[v];
                int first = source.IndexOf(candidate, StringComparison.Ordinal);
                if (first < 0) continue;

                int second = source.IndexOf(candidate, first + candidate.Length, StringComparison.Ordinal);
                if (second >= 0) return 2;

                index = first;
                return 1;
            }

            return 0;
        }

        /// <summary>把新代码的行尾统一成被替换那段代码实际用的行尾。</summary>
        static string MatchLineEndings(string newCode, string matchedOld)
        {
            if (string.IsNullOrEmpty(newCode) || string.IsNullOrEmpty(matchedOld)) return newCode;

            bool crlf = matchedOld.IndexOf('\r') >= 0;
            string lf = newCode.Replace("\r\n", "\n");
            return crlf ? lf.Replace("\n", "\r\n") : lf;
        }

        // =====================================================================
        // 解析 LLM 回复
        // =====================================================================

        public const string PatchBegin = "###PATCH";
        public const string OldMarker = "--- OLD";
        public const string NewMarker = "--- NEW";
        public const string PatchEnd = "--- END";

        /// <summary>
        /// 从模型回复里抽出改动。约定格式：
        ///
        ///   ###PATCH
        ///   一句话理由
        ///   --- OLD
        ///   原始代码
        ///   --- NEW
        ///   替换后的代码
        ///   --- END
        ///
        /// 按行扫描而不是正则：模型总爱在前后加解释、把标记写歪，
        /// 逐行找标记最能容忍这些噪声。任何一处缺失都返回 false，
        /// 让上层提示「重新生成」，而不是拿半截数据去改用户的文件。
        /// </summary>
        public static bool TryParseReply(string reply,
            out string rationale, out string oldCode, out string newCode, out string error)
        {
            rationale = null;
            oldCode = null;
            newCode = null;
            error = null;

            if (string.IsNullOrEmpty(reply)) { error = "模型没有返回内容。"; return false; }

            var lines = reply.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');

            int begin = -1;
            int oldAt = -1;
            int newAt = -1;
            int endAt = -1;

            for (int i = 0; i < lines.Length; i++)
            {
                string trimmed = lines[i].Trim();

                if (begin < 0)
                {
                    if (trimmed.StartsWith(PatchBegin, StringComparison.OrdinalIgnoreCase)) begin = i;
                    continue;
                }

                // 必须按顺序出现，避免把正文里的同名文字误判成标记
                if (oldAt < 0 && trimmed.StartsWith(OldMarker, StringComparison.OrdinalIgnoreCase)) { oldAt = i; continue; }
                if (oldAt >= 0 && newAt < 0 && trimmed.StartsWith(NewMarker, StringComparison.OrdinalIgnoreCase)) { newAt = i; continue; }
                if (newAt >= 0 && endAt < 0 && trimmed.StartsWith(PatchEnd, StringComparison.OrdinalIgnoreCase)) { endAt = i; continue; }
            }

            if (begin < 0) { error = "模型回复里没有 " + PatchBegin + " 标记，无法解析。"; return false; }
            if (oldAt < 0) { error = "模型回复里缺少 " + OldMarker + " 段。"; return false; }
            if (newAt < 0) { error = "模型回复里缺少 " + NewMarker + " 段。"; return false; }

            // 没有 --- END 也接受：让它一直取到结尾，比直接判失败更宽容
            if (endAt < 0) endAt = lines.Length;

            rationale = Join(lines, begin + 1, oldAt).Trim();
            oldCode = Join(lines, oldAt + 1, newAt);
            newCode = Join(lines, newAt + 1, endAt);

            if (oldCode.Trim().Length == 0) { error = "补丁的原始代码段是空的。"; return false; }
            if (newCode.Trim().Length == 0) { error = "补丁的替换代码段是空的。"; return false; }
            if (string.IsNullOrEmpty(rationale)) rationale = "(模型未给出理由)";

            return true;
        }

        static string Join(string[] lines, int from, int to)
        {
            if (from >= to) return "";

            var sb = new System.Text.StringBuilder();
            for (int i = from; i < to; i++)
            {
                if (sb.Length > 0) sb.Append('\n');
                sb.Append(lines[i]);
            }
            return sb.ToString();
        }

        // =====================================================================
        // 序列化
        // =====================================================================

        public Dictionary<string, object> ToDict()
        {
            var d = new Dictionary<string, object>();
            d["id"] = id ?? "";
            d["finding_id"] = findingId ?? "";
            d["file_path"] = filePath ?? "";
            d["rationale"] = rationale ?? "";
            d["old_code"] = oldCode ?? "";
            d["new_code"] = newCode ?? "";
            d["created_utc"] = createdAtUtc ?? "";
            d["applied_utc"] = appliedUtc ?? "";
            d["backup_path"] = backupPath ?? "";
            d["status"] = status.ToString();
            d["error"] = error ?? "";
            return d;
        }

        public static CodePatch FromDict(Dictionary<string, object> d)
        {
            if (d == null) return null;

            var p = new CodePatch();
            p.id = Str(d, "id", "");
            p.findingId = Str(d, "finding_id", "");
            p.filePath = Str(d, "file_path", "");
            p.rationale = Str(d, "rationale", "");
            p.oldCode = Str(d, "old_code", "");
            p.newCode = Str(d, "new_code", "");
            p.createdAtUtc = Str(d, "created_utc", "");
            p.appliedUtc = Str(d, "applied_utc", "");
            p.backupPath = Str(d, "backup_path", "");
            p.error = Str(d, "error", "");

            string status = Str(d, "status", "Pending");
            try { p.status = (CodePatchStatus)Enum.Parse(typeof(CodePatchStatus), status, true); }
            catch { p.status = CodePatchStatus.Pending; }

            return p;
        }

        public string ToJson() { return MiniJson.Serialize(ToDict()); }

        public static CodePatch Parse(string json)
        {
            if (string.IsNullOrEmpty(json)) return null;
            return FromDict(MiniJson.ParseObjectSafe(json));
        }

        static string Str(Dictionary<string, object> d, string key, string def)
        {
            object v;
            if (!d.TryGetValue(key, out v) || v == null) return def;
            string s = Convert.ToString(v, CultureInfo.InvariantCulture);
            return s ?? def;
        }
    }
}
