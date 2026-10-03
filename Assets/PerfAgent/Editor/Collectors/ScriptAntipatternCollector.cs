using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using UnityEngine;
using PerfAgent.Core;

namespace PerfAgent.Collectors
{
    /// <summary>
    /// 脚本反模式扫描。
    ///
    /// 做法：先用正则定位「每帧执行的方法」（Update/FixedUpdate/LateUpdate/OnGUI 及常用回调），
    /// 再用花括号配对取出方法体范围，最后只在方法体内匹配反模式。
    /// 这样能大幅降低误报（同类写法写在 Start/Awake 里往往没问题）。
    /// </summary>
    public class ScriptAntipatternCollector : IPerfCollector
    {
        public string Name { get { return "脚本反模式"; } }
        public string ToolName { get { return "scan_scripts"; } }
        public string Description { get { return "扫描 C# 源码，找出每帧执行方法中的堆分配、反射调用、查找类 API 等常见性能反模式。"; } }

        const long MaxFileBytes = 1024 * 1024;
        const int MaxIssues = 300;

        static readonly Regex PerFrameMethod = new Regex(
            @"\b(?:void|IEnumerator|async\s+void|protected\s+virtual\s+void|public\s+override\s+void|override\s+void)\s+" +
            @"(Update|FixedUpdate|LateUpdate|OnGUI|OnTriggerEnter\w*|OnTriggerStay\w*|OnCollisionEnter\w*|" +
            @"OnCollisionStay\w*|OnMouseDrag|OnMouseDown|OnMouseUp|OnPreRender|OnPostRender|" +
            @"OnRenderObject|OnWillRenderObject|OnBecameVisible|OnBecameInvisible|OnAudioFilterRead|" +
            @"OnAnimatorMove|OnAnimatorIK|OnDrawGizmos\w*|OnValidate)\s*\(",
            RegexOptions.Compiled);

        class Antipattern
        {
            public string id;
            public Regex regex;
            public string severity;
            public string suggestion;

            public Antipattern(string id, string pattern, string severity, string suggestion)
            {
                this.id = id;
                this.regex = new Regex(pattern, RegexOptions.Compiled);
                this.severity = severity;
                this.suggestion = suggestion;
            }
        }

        static readonly Antipattern[] Patterns =
        {
            new Antipattern("gc_collection_new",
                @"\bnew\s+(?:List|Dictionary|HashSet|Queue|Stack|SortedDictionary|SortedList)\s*<",
                Severity.Error, "把容器提升为字段并复用（.Clear() 而不是 new），或改用静态/池化缓冲"),

            new Antipattern("gc_array_new",
                @"\bnew\s+[A-Za-z_][\w\.<>]*\s*\[",
                Severity.Error, "数组分配同样进入 GC，提升为字段并用长度上限复用"),

            new Antipattern("gc_string_concat",
                @"(?:""[^""\\]*(?:\\.[^""\\]*)*""\s*\+)|(?:\+\s*"")",
                Severity.Warn, "字符串拼接每帧产生垃圾，改用 StringBuilder 字段或缓存结果"),

            new Antipattern("gc_string_format",
                @"\bstring\.Format\s*\(",
                Severity.Warn, "string.Format 会有装箱与分配，缓存格式化结果或使用 ZString/TextMeshPro SetText"),

            new Antipattern("linq",
                @"\.(?:Where|Select|SelectMany|OrderBy|OrderByDescending|ThenBy|First|FirstOrDefault|Last|Single|Any|All|Count|Sum|Min|Max|ToList|ToArray|ToDictionary|ToHashSet|Distinct|GroupBy|Aggregate)\s*[(<]",
                Severity.Error, "LINQ 每帧都会分配迭代器/闭包，改写为手写 for 循环"),

            new Antipattern("foreach_enumerator",
                @"\bforeach\s*\(",
                Severity.Info, "确认被遍历的是 List/数组（结构体枚举器）而非 IEnumerable/Dictionary 接口（会装箱分配）"),

            new Antipattern("new_waitfor",
                @"\bnew\s+WaitFor(?:Seconds|SecondsRealtime|EndOfFrame|FixedUpdate)\b",
                Severity.Error, "YieldInstruction 应缓存到字段，协程内每帧 new 会持续产生垃圾"),

            new Antipattern("getcomponent",
                @"\bGetComponent(?:InParent|InChildren)?\s*[(<]",
                Severity.Warn, "GetComponent 有原生调用开销，改为 Awake 中缓存引用"),

            new Antipattern("find_api",
                @"\b(?:GameObject\.Find|FindObjectOfType|FindObjectsOfType|FindGameObjectWithTag|FindGameObjectsWithTag|FindAnyObjectByType|FindFirstObjectByType)\b",
                Severity.Error, "场景查找是 O(n) 遍历，改为启动时缓存或由外部注入引用"),

            new Antipattern("camera_main",
                @"\bCamera\.main\b",
                Severity.Error, "Camera.main 内部是带 Tag 的全场景查找且会被缓存失效，改为字段缓存"),

            new Antipattern("tag_compare",
                @"\.tag\s*==|==\s*[A-Za-z_]\w*\.tag\b",
                Severity.Warn, "比较 tag 字符串会分配，改用 CompareTag(\"...\")"),

            new Antipattern("sendmessage",
                @"\bSendMessage\s*\(|\bBroadcastMessage\s*\(",
                Severity.Warn, "SendMessage 是反射调用，改为接口/委托直调"),

            new Antipattern("resources_load",
                @"\bResources\.(?:Load|LoadAll|LoadAsync|UnloadUnusedAssets)\b",
                Severity.Warn, "Resources.Load 会同步阻塞并绕过依赖管理，改用 Addressables"),

            new Antipattern("instantiate_destroy",
                @"\b(?:Object\.)?Instantiate\s*[<(]|\bDestroy\s*\(|\bDestroyImmediate\s*\(",
                Severity.Warn, "每帧实例化/销毁会造成分配与 GC 尖峰，改用对象池"),

            new Antipattern("physics_alloc",
                @"\bPhysics2?D?\.(?:RaycastAll|SphereCastAll|BoxCastAll|CapsuleCastAll|OverlapSphere|OverlapBox|OverlapCapsule|OverlapCircle|OverlapArea)\s*\(",
                Severity.Warn, "使用带 NonAlloc 的版本并复用结果数组"),

            new Antipattern("gc_collect",
                @"\bGC\.Collect\s*\(",
                Severity.Error, "手动触发 GC 会造成明显卡顿尖峰，移除该调用并改为减少分配"),

            new Antipattern("debug_log",
                @"\bDebug\.Log(?:Format|Warning|Error)?\s*\(",
                Severity.Info, "日志本身有字符串构造与 IO 成本，发布版本应剥离或用条件编译包裹"),

            new Antipattern("stringbuilder_new",
                @"\bnew\s+StringBuilder\s*\(",
                Severity.Warn, "StringBuilder 应提升为字段并复用（Clear 后继续用）"),

            new Antipattern("sort_alloc",
                @"\.Sort\s*\(\s*(?:\(|delegate|new\s+Comparison)",
                Severity.Warn, "lambda/delegate 形式的比较器会分配闭包，改为静态比较方法"),
        };

        public void Collect(CollectorContext ctx)
        {
            var s = ctx.snapshot;
            if (s == null) return;

            string root = Application.dataPath;
            List<string> files;
            try
            {
                files = new List<string>(Directory.GetFiles(root, "*.cs", SearchOption.AllDirectories));
            }
            catch (Exception e)
            {
                ctx.Note("脚本扫描失败: " + e.Message);
                return;
            }

            int scanned = 0, skipped = 0;
            for (int i = 0; i < files.Count; i++)
            {
                string full = files[i].Replace('\\', '/');
                if (ShouldSkip(full)) { skipped++; continue; }
                if (s.codeIssues.Count >= MaxIssues) break;

                try
                {
                    var fi = new FileInfo(full);
                    if (fi.Length > MaxFileBytes) { skipped++; continue; }
                    ScanFile(s, root, full, File.ReadAllText(full));
                    scanned++;
                }
                catch { skipped++; }
            }

            s.SetMetric("已扫描脚本", "个", scanned, "Assets/**/*.cs");
            s.SetMetric("代码问题数", "个", s.codeIssues.Count, "每帧方法体内的反模式");
            if (skipped > 0) s.AddNote("脚本扫描跳过 " + skipped + " 个文件（第三方/生成代码/超大文件）。");
        }

        static bool ShouldSkip(string fullPath)
        {
            string p = fullPath.ToLowerInvariant();
            if (p.Contains("/textmesh pro/")) return true;
            if (p.Contains("/plugins/")) return false;          // 插件仍需扫描，但下面会按扩展/后缀过滤
            if (p.Contains("/packages/")) return true;
            if (p.EndsWith(".designer.cs") || p.EndsWith(".g.cs") || p.EndsWith(".generated.cs")) return true;
            if (p.EndsWith(".meta")) return true;
            if (p.Contains("/library/")) return true;
            if (p.Contains("/obj/")) return true;
            return false;
        }

        void ScanFile(PerfSnapshot s, string root, string fullPath, string text)
        {
            foreach (Match m in PerFrameMethod.Matches(text))
            {
                if (s.codeIssues.Count >= MaxIssues) return;

                int bodyStart = FindBodyStart(text, m.Index + m.Length - 1);
                if (bodyStart < 0) continue;
                int bodyEnd = FindBodyEnd(text, bodyStart);
                if (bodyEnd <= bodyStart) continue;

                string methodName = m.Groups[1].Value;
                string body = text.Substring(bodyStart, bodyEnd - bodyStart + 1);

                for (int p = 0; p < Patterns.Length; p++)
                {
                    var pattern = Patterns[p];
                    foreach (Match hit in pattern.regex.Matches(body))
                    {
                        if (s.codeIssues.Count >= MaxIssues) return;

                        int absolute = bodyStart + hit.Index;
                        var issue = new CodeIssue();
                        issue.file = RelativePath(root, fullPath);
                        issue.line = LineOf(text, absolute);
                        issue.pattern = methodName + " + " + pattern.id;
                        issue.snippet = Snippet(text, absolute, methodName);
                        issue.suggestion = pattern.suggestion;
                        issue.severity = pattern.severity;
                        s.codeIssues.Add(issue);
                    }
                }
            }
        }

        static string RelativePath(string root, string fullPath)
        {
            string normalized = fullPath.Replace('\\', '/');
            string rootNorm = root.Replace('\\', '/');
            return normalized.StartsWith(rootNorm, StringComparison.Ordinal)
                ? "Assets" + normalized.Substring(rootNorm.Length)
                : normalized;
        }

        static string Snippet(string text, int index, string methodName)
        {
            int start = text.LastIndexOf('\n', Math.Max(0, index - 1));
            int end = text.IndexOf('\n', index);
            if (start < 0) start = 0; else start++;
            if (end < 0) end = text.Length;
            string line = text.Substring(start, Math.Max(0, end - start)).Trim();
            if (line.Length > 160) line = line.Substring(0, 157) + "...";
            return methodName + ": " + line;
        }

        static int LineOf(string text, int index)
        {
            int line = 1;
            for (int i = 0; i < index && i < text.Length; i++)
                if (text[i] == '\n') line++;
            return line;
        }

        // ---------------- 花括号配对 ----------------

        static int FindBodyStart(string text, int from)
        {
            for (int i = from; i < text.Length && i < from + 400; i++)
            {
                char c = text[i];
                if (c == '{') return i;
                if (c == ';') return -1;                        // 接口方法 / 抽象声明
                if (c == '=' && i + 1 < text.Length && text[i + 1] == '>') return -1; // 表达式体（暂不分析）
            }
            return -1;
        }

        static int FindBodyEnd(string text, int start)
        {
            int depth = 0;
            for (int i = start; i < text.Length; i++)
            {
                char c = text[i];
                if (c == '"')
                {
                    i = SkipStringLiteral(text, i, '"');
                    continue;
                }
                if (c == '\'')
                {
                    i = SkipStringLiteral(text, i, '\'');
                    continue;
                }
                if (c == '/' && i + 1 < text.Length && text[i + 1] == '/')
                {
                    int nl = text.IndexOf('\n', i);
                    i = nl < 0 ? text.Length : nl;
                    continue;
                }
                if (c == '/' && i + 1 < text.Length && text[i + 1] == '*')
                {
                    int close = text.IndexOf("*/", i + 2, StringComparison.Ordinal);
                    i = close < 0 ? text.Length : close + 1;
                    continue;
                }
                if (c == '{') depth++;
                else if (c == '}')
                {
                    depth--;
                    if (depth == 0) return i;
                }
            }
            return text.Length - 1;
        }

        static int SkipStringLiteral(string text, int start, char quote)
        {
            for (int i = start + 1; i < text.Length; i++)
            {
                if (text[i] == '\\') { i++; continue; }
                if (text[i] == quote) return i;
                if (text[i] == '\n') return i;
            }
            return text.Length - 1;
        }
    }
}
