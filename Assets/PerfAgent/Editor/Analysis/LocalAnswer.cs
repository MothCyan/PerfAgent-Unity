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

        /// <summary>本地回答（Markdown）。不联网、不调用任何模型。</summary>
        public static string Answer(PerfSnapshot s, string question)
        {
            if (s == null)
                return "当前没有快照：先点「跟随采集」（自己进 Play 操作），或从左上列表载入一份历史快照。\n";

            var hits = new List<Dimension>();
            Match(question, hits);

            var sb = new StringBuilder();
            sb.Append("**本地规则引擎** · ");
            if (hits.Count == 0)
            {
                sb.Append("没有匹配到具体维度，下面给出全部结论\n");
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

            bool any = AppendFindings(sb, s, hits);
            AppendMetrics(sb, s, hits, any);
            AppendLimits(sb, hits);
            return sb.ToString();
        }

        /// <summary>列出该维度的结论（带证据链）。返回是否给出了结论。</summary>
        static bool AppendFindings(StringBuilder sb, PerfSnapshot s, List<Dimension> hits)
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

            sb.Append("### 结论\n\n");
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
        static void AppendMetrics(StringBuilder sb, PerfSnapshot s, List<Dimension> hits, bool hasFindings)
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
                rows.Append("| ").Append(m.name).Append(" | ")
                    .Append(m.value.ToString("0.##", CultureInfo.InvariantCulture)).Append(' ').Append(m.unit)
                    .Append(" | ").Append(string.IsNullOrEmpty(m.budget) ? "-" : m.budget + " " + m.budgetUnit)
                    .Append(" |\n");
            }
            if (rows.Length == 0) return;

            sb.Append(hasFindings ? "### 相关数字\n\n" : "### 数字\n\n");
            sb.Append("| 指标 | 值 | 预算 |\n|---|---:|---:|\n");
            sb.Append(rows);
            sb.Append('\n');
        }

        /// <summary>把本地引擎做不到的事写清楚 —— 这是它跟联网 LLM 的分界线。</summary>
        static void AppendLimits(StringBuilder sb, List<Dimension> hits)
        {
            sb.Append("### 本地引擎的边界（实话）\n\n");
            sb.Append("- 它**不联网、不调用任何模型**，回答内容全部来自当前快照的规则结论，可以逐条回溯到证据。\n");
            sb.Append("- 它只按关键词找维度（帧率 / 渲染 / 内存 / 资源 / 代码 / 物理 / 场景 / 采集），");
            sb.Append("**不理解句子意思**，也不做跨维度推理（比如「为什么只有在战斗时才卡」）。\n");
            if (hits.Count == 0)
                sb.Append("- 这次没匹配到维度，所以把全部结论都列了出来；换几个关键词再问会更聚焦。\n");
            sb.Append("- 需要解释、对比、写给人看的结论时，用「启用 AI」走 LLM（会联网，且由你决定发什么）。\n");
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
