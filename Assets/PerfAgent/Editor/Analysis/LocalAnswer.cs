using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using PerfAgent.Core;

namespace PerfAgent.Analysis
{
    /// <summary>
    /// 「本地规则引擎」的问答入口：**纯本地模式 / 没配 API Key 时，对话追问走它。**
    ///
    /// <para><b>它是什么</b></para>
    /// 就是 <see cref="PerfRuleEngine"/> 的那套确定性规则 —— 拿快照跟预算比，
    /// 产出「带证据链的结论」。它不联网、不调用任何模型、也不做语言理解。
    /// 这个类只做两件事：
    ///   1. **关键词路由**：把问题里的词映射到诊断维度（帧率 / 渲染 / 内存 / 资源 / 代码 / 物理 / 场景 / 采集）；
    ///   2. **摆证据**：把该维度已经算好的结论、数字、证据链按顺序列出来。
    ///
    /// <para><b>为什么不做成「像 LLM 那样回答」</b></para>
    /// 工具的底座是确定性规则 —— 「结论可回溯」是它的卖点，也是它跟纯 LLM 问答的区别。
    /// 本地路线能保证的只有「这些结论和数字都来自快照」；理解自由表达、跨维度推理、写给人看的解释，
    /// 那是 LLM 的事（要联网，也就是关掉纯本地模式）。
    /// 所以这里的输出会**明确写出命中了哪个维度、以及自己做不到什么**，不装作听懂了。
    /// </summary>
    public static class LocalAnswer
    {
        /// <summary>一个诊断维度：关键词 → 结论分类 → 相关指标。</summary>
        class Dimension
        {
            public string name;
            public string[] keywords;
            public string[] categories;
            public string[] metrics;

            public Dimension(string name, string[] keywords, string[] categories, string[] metrics)
            {
                this.name = name; this.keywords = keywords;
                this.categories = categories; this.metrics = metrics;
            }
        }

        /// <summary>
        /// 维度表。关键词是「用户会怎么问」，分类是「规则引擎把结论归到哪一类」。
        /// 只做包含匹配（不分词），所以宁可多写几个同义词，也不要漏词。
        /// </summary>
        static readonly Dimension[] Dimensions =
        {
            new Dimension("帧率",
                new[] { "帧率", "帧耗时", "卡顿", "掉帧", "掉针", "fps", "延迟", "抖动", "尖峰", "流畅", "卡" },
                new[] { "帧率", "CPU" },
                new[] { "帧耗时均值", "帧耗时 P50", "帧耗时 P95", "帧耗时峰值", "实际 FPS", "CPU 帧耗时", "GPU 帧耗时" }),

            new Dimension("渲染",
                new[] { "渲染", "draw", "call", "批", "batch", "setpass", "三角", "顶点", "阴影", "shader", "着色器",
                        "材质", "灯光", "后处理", "粒子", "overdraw" },
                new[] { "渲染" },
                new[] { "Draw Calls 均值", "Draw Calls 峰值", "SetPass Calls", "Triangles", "Batches", "Vertices" }),

            new Dimension("内存",
                new[] { "内存", "memory", "分配", "alloc", "gc", "泄漏", "泄露", "堆", "驻留", "显存", "占用" },
                new[] { "内存" },
                new[] { "每帧托管分配", "项目每帧分配", "编辑器开销基线", "总分配内存", "总保留内存",
                        "Mono 已用", "Mono 堆", "TempAllocator", "GPU 驱动内存" }),

            new Dimension("资源",
                new[] { "资源", "纹理", "贴图", "图集", "模型", "音频", "压缩", "导入", "mipmap", "read/write", "asset" },
                new[] { "资源" },
                new[] { "资源总数", "纹理数", "模型数", "音频数", "纹理内存(估算)", "Resources 资源数" }),

            new Dimension("代码",
                new[] { "代码", "脚本", "函数", "方法", "update", "反模式", "字符串", "linq", "装箱", "闭包" },
                new[] { "代码" },
                new[] { "已扫描脚本", "代码问题数" }),

            new Dimension("物理",
                new[] { "物理", "碰撞", "刚体", "collider", "rigidbody", "射线", "固定步", "physics", "tick" },
                new[] { "物理" },
                new[] { "固定步长", "物理解算迭代" }),

            new Dimension("场景",
                new[] { "场景", "对象", "相机", "camera", "canvas", "ui", "组件", "层级", "光源" },
                new[] { "场景" },
                new[] { "Camera", "Canvas", "Light", "MeshRenderer", "SkinnedMeshRenderer", "Animator", "Collider" }),

            new Dimension("采集",
                new[] { "采样", "样本", "帧数", "窗口", "采集", "时长" },
                new[] { "采集" },
                new[] { "采集窗口帧数" })
        };

        /// <summary>没有命中任何维度时，兜底展示的核心指标。</summary>
        static readonly string[] CoreMetrics =
        {
            "帧耗时均值", "帧耗时 P95", "帧耗时峰值", "实际 FPS",
            "每帧托管分配", "项目每帧分配", "TempAllocator", "总分配内存",
            "Draw Calls 均值", "SetPass Calls", "Triangles", "纹理内存(估算)",
            "资源总数", "已扫描脚本", "代码问题数"
        };

        /// <summary>
        /// 「解释类 / 决策类」问题的词。
        ///
        /// 这类问题的共性是：它们要的不是事实，而是因果、取舍与判断 ——
        /// 「为什么只有战斗时卡」「我该先修哪个」「这个数算高吗」。
        /// 规则引擎给不了这些（它只会比预算），所以命中时要在**开头**就说清楚，
        /// 而不是把「我不理解句子」藏在末尾 —— 那才是让人觉得「看不出用不用 AI 差别」的原因。
        /// </summary>
        static readonly string[] ExplanationWords =
        {
            "为什么", "原因", "怎么会", "怎么", "如何", "建议", "优先", "先修", "该不该",
            "值不值", "值得", "正常吗", "算高", "算不算", "对比", "优化方向", "怎么办", "解释", "合理"
        };

        /// <summary>这个问题是不是「要解释」而不是「要事实」。UI 与本地回答都靠它分流。</summary>
        public static bool IsExplanationQuestion(string question)
        {
            if (string.IsNullOrEmpty(question)) return false;
            for (int i = 0; i < ExplanationWords.Length; i++)
            {
                if (question.IndexOf(ExplanationWords[i], StringComparison.OrdinalIgnoreCase) >= 0) return true;
            }
            return false;
        }

        /// <summary>命中维度（可能多个）。question 为空时返回空列表（= 给全部结论）。</summary>
        public static List<string> MatchedDimensions(string question)
        {
            var hits = new List<Dimension>();
            Match(question, hits);
            var names = new List<string>();
            for (int i = 0; i < hits.Count; i++) names.Add(hits[i].name);
            return names;
        }

        static void Match(string question, List<Dimension> hits)
        {
            string q = string.IsNullOrEmpty(question) ? "" : question.ToLowerInvariant();
            if (q.Length == 0) return;

            for (int d = 0; d < Dimensions.Length; d++)
            {
                var dim = Dimensions[d];
                for (int k = 0; k < dim.keywords.Length; k++)
                {
                    if (q.IndexOf(dim.keywords[k], StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        hits.Add(dim);
                        break;
                    }
                }
            }
        }

        /// <summary>本地回答（Markdown，给报告/复制用）。不联网、不调用任何模型。</summary>
        public static string Answer(PerfSnapshot s, string question)
        {
            return Answer(s, question, false);
        }

        /// <summary>
        /// 本地回答。
        ///
        /// <param name="chatStyle">
        /// true = 「聊天版」：小标题用 `**粗体**`、数字用列表 —— 对话记录是一个 Label，
        /// 只认 `**粗体**` 与 `` `代码` ``：`###` 会原样显示成「### 结论」，
        /// Markdown 表格会显示成一堆竖线（实测就是这个问题，很难读）。
        /// </param>
        /// </summary>
        public static string Answer(PerfSnapshot s, string question, bool chatStyle)
        {
            if (s == null)
                return "当前没有快照：先点「跟随采集」（自己进 Play 操作），或从左上列表载入一份历史快照。\n";

            var hits = new List<Dimension>();
            Match(question, hits);

            var sb = new StringBuilder();
            // 聊天版不重复写名字：面板已经在上面写过一行「**本地规则引擎**（…）」，再写就重了
            if (!chatStyle) sb.Append("**本地规则引擎** · ");
            if (hits.Count == 0)
            {
                // 没命中时说清「为什么列了全部」，并给出能提高命中率的词 ——
                // 不然用户会以为本地引擎只会倒垃圾
                sb.Append("没有匹配到具体维度，下面给出全部结论（换几个关键词会更聚焦：")
                  .Append("帧率 / 渲染 / 内存 / 资源 / 代码 / 物理 / 场景 / 采集）\n");
            }
            else
            {
                sb.Append("命中维度：");
                for (int i = 0; i < hits.Count; i++)
                {
                    if (i > 0) sb.Append('、');
                    sb.Append(hits[i].name);
                }
                sb.Append("（按关键词匹配，不做语义理解）\n");
            }

            sb.Append("快照 ").Append(string.IsNullOrEmpty(s.id) ? "（未命名）" : s.id);
            int window = s.WindowFrames();
            if (window > 0) sb.Append(" · 窗口 ").Append(window).Append(" 帧");
            if (!string.IsNullOrEmpty(s.scenePath)) sb.Append(" · ").Append(s.scenePath);
            sb.Append("\n\n");

            // 样本不足会「吃掉」几乎所有统计类结论，这时候必须先说清楚，
            // 否则用户问「为什么卡」只会得到一句「没有结论」，看着像工具坏了。
            if (s.WindowTooSmallForStats())
            {
                sb.Append("⚠ 本次只采到 ").Append(window).Append(" 帧（门槛 ")
                  .Append(PerfSnapshot.MinFramesForStats)
                  .Append(" 帧）：帧耗时 / 分配这类结论已被跳过（样本不足，数字会被个别帧主导）。\n")
                  .Append("　 跟随采集会一直录到你退出 Play，多操作几秒再问一次，结论才有意义。\n\n");
            }

            // 解释类问题要在开头说一句：本地只能给事实。
            // 只说一句 —— 三行的「边界说明」每条回答都重复一遍就成噪声了（用户反馈过），
            // 那段话现在放在对话卡的 tooltip 里，只讲一次。
            if (IsExplanationQuestion(question))
            {
                sb.Append("⚠ 解释类问题（为什么 / 怎么改 / 先修哪个）：本地只能给事实与证据，给不出因果与取舍");
                sb.Append(hits.Count > 0 ? "；下面先把它找到的事实摆出来。\n\n" : "，而这个问题也没命中关键词。\n\n");
            }

            bool any = AppendFindings(sb, s, hits, chatStyle);
            AppendMetrics(sb, s, hits, any, chatStyle);
            return sb.ToString();
        }

        /// <summary>列出该维度的结论（带证据链）。返回是否给出了结论。</summary>
        static bool AppendFindings(StringBuilder sb, PerfSnapshot s, List<Dimension> hits, bool chatStyle)
        {
            var list = new List<PerfFinding>();
            for (int i = 0; i < s.findings.Count; i++)
            {
                var f = s.findings[i];
                if (hits.Count == 0) { list.Add(f); continue; }
                if (Covers(hits, f.category)) { list.Add(f); continue; }

                // 「采集」这类结论是**数据质量元信息**：它解释了为什么别的维度没有结论。
                // 样本不足时必须一并给出，否则用户问「为什么卡」只得到一句「你问的维度上没有结论」，
                // 看起来像工具坏了，而真正的原因（只录到 10 帧）就在快照里躺着。
                if (s.WindowTooSmallForStats() && f.category == "采集") list.Add(f);
            }

            // 命中的维度上只有提示级结论时，也要把它们带上 ——
            // 「只有提示」本身就是一个结论（例如「采集窗口过短」）。
            var majors = list.FindAll(delegate (PerfFinding f) { return f.severity != Severity.Info; });
            var show = majors.Count > 0 ? majors : list;

            sb.Append(chatStyle ? "**结论**\n\n" : "### 结论\n\n");
            if (show.Count == 0)
            {
                sb.Append(hits.Count > 0
                    ? "你问的这几个维度上，当前快照没有超预算的结论。\n\n"
                    : "当前快照没有任何结论（也可能只是没采集到数据，看附录里的提示）。\n\n");
                return false;
            }

            for (int i = 0; i < show.Count && i < 8; i++)
            {
                var f = show[i];
                sb.Append(i + 1).Append(". **[").Append(Label(f.severity)).Append("] ").Append(f.title).Append("**\n");
                sb.Append("   分类 ").Append(f.category)
                  .Append("　置信度 ").Append((f.confidence * 100).ToString("0", CultureInfo.InvariantCulture)).Append("%\n");
                if (!string.IsNullOrEmpty(f.detail)) sb.Append("   为什么：").Append(f.detail).Append('\n');
                if (!string.IsNullOrEmpty(f.recommendation)) sb.Append("   怎么改：").Append(f.recommendation).Append('\n');
                if (!string.IsNullOrEmpty(f.jumpTo)) sb.Append("   定位：").Append(f.jumpTo).Append('\n');

                for (int k = 0; k < f.evidence.Count && k < 3; k++)
                {
                    var e = f.evidence[k];
                    sb.Append("   证据").Append(k + 1).Append("：").Append(e.tool).Append(' ').Append(e.metric)
                      .Append(" = ").Append(e.value)
                      .Append(string.IsNullOrEmpty(e.unit) ? "" : " " + e.unit)
                      .Append(string.IsNullOrEmpty(e.threshold) ? "" : "（预算 " + e.threshold + "）")
                      .Append('\n');
                }
                sb.Append('\n');
            }
            return true;
        }

        /// <summary>该维度相关的数字。命中维度时按维度列，没命中时列核心指标。</summary>
        static void AppendMetrics(StringBuilder sb, PerfSnapshot s, List<Dimension> hits, bool hasFindings, bool chatStyle)
        {
            var names = new List<string>();
            if (hits.Count == 0)
            {
                names.AddRange(CoreMetrics);
            }
            else
            {
                for (int i = 0; i < hits.Count; i++)
                {
                    for (int k = 0; k < hits[i].metrics.Length; k++)
                    {
                        if (!names.Contains(hits[i].metrics[k])) names.Add(hits[i].metrics[k]);
                    }
                }
            }

            var rows = new StringBuilder();
            for (int i = 0; i < names.Count; i++)
            {
                var m = s.FindMetric(names[i]);
                if (m == null) continue;

                if (chatStyle)
                {
                    // 聊天版：一行一个，不做表格
                    rows.Append("- ").Append(m.name).Append(" = ")
                        .Append(m.value.ToString("0.##", CultureInfo.InvariantCulture)).Append(' ').Append(m.unit)
                        .Append(string.IsNullOrEmpty(m.budget) ? "" : "（预算 " + m.budget + " " + m.budgetUnit + "）")
                        .Append('\n');
                    continue;
                }

                rows.Append("| ").Append(m.name).Append(" | ")
                    .Append(m.value.ToString("0.##", CultureInfo.InvariantCulture)).Append(' ').Append(m.unit)
                    .Append(" | ").Append(string.IsNullOrEmpty(m.budget) ? "-" : m.budget + " " + m.budgetUnit)
                    .Append(" |\n");
            }
            if (rows.Length == 0) return;

            sb.Append(chatStyle
                ? (hasFindings ? "**相关数字**\n\n" : "**数字**\n\n")
                : (hasFindings ? "### 相关数字\n\n" : "### 数字\n\n"));
            if (!chatStyle) sb.Append("| 指标 | 值 | 预算 |\n|---|---:|---:|\n");
            sb.Append(rows);
            sb.Append('\n');

            // 闸门口径：这些数字看着超预算，但规则引擎没把它算成项目问题。
            // 不写出来，用户（和 AI）就会自己拿实测值除预算，得出一个工具本来已经否决的结论
            //（实测：有人拿「每帧托管分配 14443.6 B ÷ 预算 2048 B」报了「超标 7.05 倍」，
            // 而那是含编辑器开销的口径）。
            var gateLines = new StringBuilder();
            for (int i = 0; i < names.Count; i++)
            {
                var g = s.FindGate(names[i]);
                if (g == null || g.passed) continue;
                gateLines.Append("- **").Append(g.metric).Append("**（").Append(g.status).Append("）：").Append(g.verdict).Append('\n');
            }
            var windowGate = s.FindGate("采集窗口帧数");
            if (windowGate != null && !windowGate.passed && names.IndexOf("采集窗口帧数") < 0)
                gateLines.Append("- **采集窗口帧数**（").Append(windowGate.status).Append("）：").Append(windowGate.verdict).Append('\n');

            if (gateLines.Length > 0)
            {
                sb.Append(chatStyle ? "**这些数字没被算成问题（闸门口径）**\n\n" : "### 闸门口径\n\n");
                sb.Append(gateLines);
                sb.Append('\n');
            }
        }

        /// <summary>
        /// 给 LLM 的事实底稿。
        ///
        /// 用途：AI 模式下把这份文本附在问题后面一起发出去，让模型**在确定性结论之上做解释**，
        /// 而不是自己从头猜一遍（也少一轮工具调用、省 token）。
        ///
        /// 所以它有意识地和 <see cref="Answer"/> 不同：不带「边界（实话）」这类给人看的说明、
        /// 不用表格，只给维度、结论要点与可引用的数字。
        /// </summary>
        public static string BriefForPrompt(PerfSnapshot s, string question)
        {
            if (s == null) return "（当前没有快照）";

            var hits = new List<Dimension>();
            Match(question, hits);

            var sb = new StringBuilder();
            sb.Append("快照 ").Append(string.IsNullOrEmpty(s.id) ? "（未命名）" : s.id);
            int window = s.WindowFrames();
            if (window > 0) sb.Append("，窗口 ").Append(window).Append(" 帧");
            if (s.WindowTooSmallForStats())
                sb.Append("（不足 ").Append(PerfSnapshot.MinFramesForStats).Append(" 帧，统计类结论已被规则侧跳过）");
            sb.Append('\n');

            sb.Append("问的问题命中的维度：");
            if (hits.Count == 0)
            {
                sb.Append("（无关键词命中，下面是全部结论）");
            }
            else
            {
                for (int i = 0; i < hits.Count; i++)
                {
                    if (i > 0) sb.Append('、');
                    sb.Append(hits[i].name);
                }
            }
            sb.Append('\n');

            sb.Append("规则引擎的结论：\n");
            int shown = 0;
            for (int i = 0; i < s.findings.Count; i++)
            {
                var f = s.findings[i];
                if (hits.Count > 0 && !Covers(hits, f.category) && f.category != "采集") continue;
                if (shown++ >= 10) break;
                sb.Append("- [").Append(Label(f.severity)).Append("] ").Append(f.title)
                  .Append("（分类 ").Append(f.category)
                  .Append("，置信度 ").Append((f.confidence * 100).ToString("0", CultureInfo.InvariantCulture)).Append("%");
                if (!string.IsNullOrEmpty(f.recommendation)) sb.Append("，建议：").Append(f.recommendation);
                sb.Append("）\n");
                for (int k = 0; k < f.evidence.Count && k < 3; k++)
                {
                    var e = f.evidence[k];
                    sb.Append("    证据：").Append(e.metric).Append('=').Append(e.value)
                      .Append(string.IsNullOrEmpty(e.unit) ? "" : " " + e.unit)
                      .Append(string.IsNullOrEmpty(e.threshold) ? "" : "（预算 " + e.threshold + "）")
                      .Append('\n');
                }
            }
            if (shown == 0) sb.Append("- （命中的维度上没有超出预算的结论）\n");

            sb.Append("相关数字：");
            var names = new List<string>();
            if (hits.Count == 0) names.AddRange(CoreMetrics);
            else for (int i = 0; i < hits.Count; i++)
                for (int k = 0; k < hits[i].metrics.Length; k++)
                    if (!names.Contains(hits[i].metrics[k])) names.Add(hits[i].metrics[k]);

            int printed = 0;
            for (int i = 0; i < names.Count; i++)
            {
                var m = s.FindMetric(names[i]);
                if (m == null) continue;
                sb.Append(printed++ == 0 ? "" : "，")
                  .Append(m.name).Append('=').Append(m.value.ToString("0.##", CultureInfo.InvariantCulture)).Append(m.unit);
                if (!string.IsNullOrEmpty(m.budget)) sb.Append("（预算 ").Append(m.budget).Append(m.budgetUnit).Append("）");
            }
            if (printed == 0) sb.Append("（无）");
            sb.Append('\n');

            // 闸门口径也要进底稿 —— 它决定了上面哪些数字**不能**被当成问题引用。
            // 只给数字不给口径，模型会自己算倍数，写出一条规则引擎早就否决的「严重超标」。
            int gateLines = 0;
            for (int i = 0; i < s.gates.Count; i++)
            {
                var g = s.gates[i];
                if (g.passed) continue;
                sb.Append(gateLines++ == 0
                    ? "闸门口径（这些数字看着超预算，但规则引擎未归因到项目，不要当成问题，也不要自己用「实测值 ÷ 预算」算倍数）：\n"
                    : "");
                sb.Append("- [").Append(g.status).Append("] ").Append(g.metric)
                  .Append(" = ").Append(g.value.ToString("0.##", CultureInfo.InvariantCulture)).Append(g.unit)
                  .Append("（阈值 ").Append(g.threshold.ToString("0.##", CultureInfo.InvariantCulture)).Append(g.unit).Append("）：")
                  .Append(g.verdict).Append('\n');
            }

            sb.Append("注意：以上数字由规则引擎从快照算出，可直接引用；没出现的维度就是没有超预算结论，"
                      + "不要凭空推测那部分。\n");
            return sb.ToString();
        }

        static bool Covers(List<Dimension> hits, string category)
        {
            for (int i = 0; i < hits.Count; i++)
            {
                var cats = hits[i].categories;
                for (int k = 0; k < cats.Length; k++)
                    if (cats[k] == category) return true;
            }
            return false;
        }

        static string Label(string severity)
        {
            if (severity == Severity.Error) return "严重";
            if (severity == Severity.Warn) return "警告";
            return "提示";
        }
    }
}
