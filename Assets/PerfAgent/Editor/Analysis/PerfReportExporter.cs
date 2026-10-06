using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using UnityEngine;
using PerfAgent.Core;

namespace PerfAgent.Analysis
{
    /// <summary>
    /// 报告导出（Markdown / HTML）。
    ///
    /// 同时承担「幻觉校验」：报告里出现的每个数字都必须能在证据集中找到出处，
    /// 找不到的会被列进附录的「未验证数值」清单。这是防幻觉的最后一道闸门。
    /// </summary>
    public static class PerfReportExporter
    {
        // 数值对账的正则与口径已搬到 NumberVerifier（那部分不依赖 Unity，可离线回归）

        /// <summary>
        /// 报告里标注「这是哪个插件版本、程序集什么时候编出来的」。
        ///
        /// 为什么要写它：编辑器源码编译失败时，Unity 会**继续跑上一次编译成功的程序集**，
        /// 于是报告可能来自旧版插件 —— 实测踩过：两份报告都写着「采集还没开始就结束了」，
        /// 而那个字符串在当前源码里早就没有了（只剩注释）。有了戳就能一眼分辨报告的新旧。
        /// </summary>
        static string BuildStamp()
        {
            try
            {
                var asm = typeof(PerfReportExporter).Assembly;
                string ver = PackageVersion(asm);
                if (string.IsNullOrEmpty(ver)) ver = (asm.GetName().Version ?? new Version(0, 0)).ToString();
                string built = "未知";
                try
                {
                    string loc = asm.Location;
                    if (!string.IsNullOrEmpty(loc) && File.Exists(loc))
                    {
                        built = File.GetLastWriteTime(loc).ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
                    }
                }
                catch { }
                return "v" + ver + "（程序集 " + built + "）";
            }
            catch { return "未知"; }
        }

        /// <summary>
        /// 插件版本取 UPM 包的 <c>package.json</c> 里的 version，而不是程序集版本。
        ///
        /// 实测踩过：报告头一直印 <c>PerfAgent v0.0.0.0</c> —— 因为 asmdef 里没写版本，
        /// 程序集版本默认就是 0.0.0.0，看到这个数字等于没看到。
        /// </summary>
        static string PackageVersion(System.Reflection.Assembly asm)
        {
            try
            {
                var info = UnityEditor.PackageManager.PackageInfo.FindForAssembly(asm);
                if (info != null && !string.IsNullOrEmpty(info.version)) return info.version;
            }
            catch { }

            try
            {
                string pkg = Path.Combine(Path.Combine(Application.dataPath, "PerfAgent"), "package.json");
                if (File.Exists(pkg))
                {
                    var m = Regex.Match(File.ReadAllText(pkg), "\"version\"\\s*:\\s*\"([^\"]+)\"");
                    if (m.Success) return m.Groups[1].Value;
                }
            }
            catch { }
            return "";
        }

        // =====================================================================
        // Markdown
        // =====================================================================

        public static string ToMarkdown(PerfSnapshot s)
        {
            if (s == null) return "# 性能报告\n\n(无数据)\n";

            var sb = new StringBuilder();
            sb.Append("# 性能诊断报告\n\n");
            sb.Append("| 项 | 值 |\n|---|---|\n");
            sb.Append("| 快照 | ").Append(s.Label()).Append(" |\n");
            sb.Append("| 采集时间 (UTC) | ").Append(s.capturedUtc).Append(" |\n");
            sb.Append("| Unity | ").Append(s.unityVersion).Append(" |\n");
            sb.Append("| 平台 | ").Append(s.platform).Append(" / ").Append(s.buildTarget).Append(" |\n");
            sb.Append("| 显卡 | ").Append(s.graphicsDevice).Append(" |\n");
            sb.Append("| 渲染管线 | ").Append(s.renderPipeline).Append(" |\n");
            sb.Append("| 场景 | ").Append(s.scenePath).Append(" |\n");
            sb.Append("| PerfAgent | ").Append(BuildStamp()).Append(" |\n");

            // 「采样帧数」以前写的是逐帧明细条数（2 帧），而附录又写着「窗口共 10 帧」——
            // 同一个报告里两个帧数对不上。这里改成口径分开写：窗口看整体，明细看抽样。
            int windowFrames = s.WindowFrames();
            sb.Append("| 采集窗口 | ")
              .Append(windowFrames > 0
                  ? windowFrames.ToString(CultureInfo.InvariantCulture) + " 帧"
                  : "**无帧数据**")
              .Append("（逐帧明细抽样 ").Append(s.frames.Count).Append(" 帧） |\n\n");

            // 一帧都没采到时，最危险的不是「没数据」，而是读者把它当成「没发现问题」：
            // 实测踩过 ‐ 优化前后的两份报告长得一模一样（都只有资源类结论），
            // 原因就是两份都没采到帧。必须在最上面把话说死。
            if (windowFrames <= 0)
            {
                sb.Append("> **本次没有采到帧。**帧耗时 / 每帧分配 / Draw Call / SetPass / 三角面这些依赖帧数据的量"
                          + "**全部不可用**，下面剩下的只有工程级审计（资源、场景、代码）。\n"
                          + "> 所以这份数据**不能**用来判断「优化前 vs 优化后」：请先让 Profiler 处于记录状态再采一次"
                          + "（判断方法：采集期间面板会实时显示「已记录 N 帧」，它一直是 0 就是没录上）。\n\n");
            }

            // ---- 关键指标 ----
            sb.Append("## 关键指标\n\n");
            sb.Append("| 指标 | 值 | 预算 | 来源 |\n|---|---:|---:|---|\n");
            for (int i = 0; i < s.metrics.Count; i++)
            {
                var m = s.metrics[i];
                sb.Append("| ").Append(m.name).Append(" | ")
                  .Append(Fmt(m.value)).Append(' ').Append(m.unit).Append(" | ")
                  .Append(string.IsNullOrEmpty(m.budget) ? "-" : m.budget + " " + m.budgetUnit).Append(" | ")
                  .Append(m.source).Append(" |\n");
            }
            sb.Append('\n');

            // 窗口过短时，上面那些分位数/峰值是由很少的样本算出来的（P50 与 P95 甚至可能完全相同），
            // 必须在表格下面当场说明，否则读者会把这些数字当成可信读数引用。
            if (windowFrames > 0 && windowFrames < PerfSnapshot.MinFramesForStats)
            {
                sb.Append("*注意：本次采集窗口只有 ").Append(windowFrames)
                  .Append(" 帧（不足 ").Append(PerfSnapshot.MinFramesForStats)
                  .Append(" 帧）：上面的分位数、峰值与 FPS 由极少量样本算出，可能完全相同，仅供参考；"
                          + "规则侧没有据此下任何结论。*\n\n");
            }

            // ---- 结论 ----
            // 同时把「规则写的叙述」单独收集起来：后面的幻觉校验只应该看这些句子，
            // 而不是去扫表格里的日期、显卡名、帧号 —— 那些是采集器写进去的事实，不是谁的断言。
            var prose = new StringBuilder();
            sb.Append("## 诊断结论\n\n");
            if (s.findings.Count == 0)
            {
                sb.Append("未发现超出预算的问题。\n\n");
            }
            else
            {
                int index = 1;
                for (int i = 0; i < s.findings.Count; i++)
                {
                    var f = s.findings[i];
                    prose.Append(f.title).Append('\n').Append(f.detail).Append('\n').Append(f.recommendation).Append('\n');
                    sb.Append("### ").Append(index++).Append(". [").Append(SeverityLabel(f.severity)).Append("] ").Append(f.title).Append('\n');
                    sb.Append("- **分类**：").Append(f.category).Append("　**置信度**：").Append((f.confidence * 100).ToString("0", CultureInfo.InvariantCulture)).Append("%\n");
                    if (!string.IsNullOrEmpty(f.detail)) sb.Append("- **说明**：").Append(f.detail).Append('\n');
                    if (!string.IsNullOrEmpty(f.recommendation)) sb.Append("- **建议**：").Append(f.recommendation).Append('\n');
                    if (!string.IsNullOrEmpty(f.jumpTo)) sb.Append("- **定位**：`").Append(f.jumpTo).Append("`\n");
                    if (f.evidence.Count > 0)
                    {
                        sb.Append("- **证据**：\n");
                        for (int k = 0; k < f.evidence.Count; k++)
                        {
                            var e = f.evidence[k];
                            sb.Append("  ").Append(k + 1).Append(". `").Append(e.tool).Append("` ")
                              .Append(e.metric).Append(" = ").Append(e.value)
                              .Append(string.IsNullOrEmpty(e.unit) ? "" : " " + e.unit)
                              .Append(string.IsNullOrEmpty(e.threshold) ? "" : "（预算 " + e.threshold + "）")
                              .Append(string.IsNullOrEmpty(e.source) ? "" : " @" + e.source)
                              .Append('\n');
                        }
                    }
                    sb.Append('\n');
                }
            }

            // 闸门口径紧跟结论：那些「看着超标但没被算成问题」的数字必须比读者先看到。
            AppendGateSection(sb, s);

            // ---- 帧数据 ----
            if (s.frames.Count > 0)
            {
                sb.Append("## 帧数据\n\n");
                sb.Append("| 统计 | 值 |\n|---|---:|\n");
                sb.Append("| 均值 | ").Append(Fmt(s.FrameTimeAvgMs())).Append(" ms |\n");
                sb.Append("| P50 | ").Append(Fmt(s.FrameTimePercentileMs(50))).Append(" ms |\n");
                sb.Append("| P95 | ").Append(Fmt(s.FrameTimePercentileMs(95))).Append(" ms |\n");
                sb.Append("| 峰值 | ").Append(Fmt(s.FrameTimeMaxMs())).Append(" ms |\n");

                // 平均每帧分配：口径优先级与规则侧一致（面板/Recorder 的 GC Allocated In Frame 优先，
                // 退到 GC.GetTotalMemory 差值时标明，拿不到就写「不可用」）。
                // 以前这里恒为 0 B —— 与上面「每帧托管分配 14435 B」直接打架。
                double allocPerFrame = s.AverageAllocPerFrame(out string allocNote);
                sb.Append("| 平均每帧分配 | ")
                  .Append(double.IsNaN(allocPerFrame) ? "不可用" : Fmt(allocPerFrame) + " B " + allocNote)
                  .Append(" |\n");
                // 不再输出「GC 次数」：Profiler 面板没有 GC 事件计数这个序列（口径说明里已写明已移除），
                // 以前那行打的恒为 0，属于凭空造数。
                sb.Append('\n');

                if (s.WindowTooSmallForStats())
                {
                    sb.Append("*本次窗口只有 ").Append(windowFrames)
                      .Append(" 帧，以上统计量仅供参考；需要足够样本的结论已跳过（见诊断结论）。*\n\n");
                }

                var spikes = s.SpikeFrames(10);
                if (spikes.Count > 0)
                {
                    sb.Append("### 最慢的 10 帧\n\n");
                    sb.Append("| 帧号 | 耗时 (ms) | 分配 (B) | Draw Call | TempAlloc (MB) |\n|---:|---:|---:|---:|---:|\n");
                    var frames = new List<FrameStat>(s.frames);
                    frames.Sort((a, b) => b.deltaMs.CompareTo(a.deltaMs));
                    for (int i = 0; i < frames.Count && i < 10; i++)
                    {
                        var f = frames[i];
                        // 分配取面板序列（allocInFrameBytes），不是 GC.GetTotalMemory 差值那个弱口径 ——
                        // 后者在面板采集路径上恒为 0，直接印会让「分配」列全是 0（实测：133 ms 的尖峰被读成「那帧没分配」）。
                        long alloc = f.DisplayAllocBytes;
                        sb.Append("| ").Append(f.frame).Append(" | ").Append(Fmt(f.deltaMs)).Append(" | ")
                          .Append(alloc > 0 ? alloc.ToString(CultureInfo.InvariantCulture) : "—").Append(" | ")
                          .Append(f.drawCalls > 0 ? f.drawCalls.ToString(CultureInfo.InvariantCulture) : "—").Append(" | ")
                          .Append(f.tempAllocBytes > 0 ? Fmt(f.tempAllocBytes / 1048576.0) : "—").Append(" |\n");
                    }
                    sb.Append('\n');
                    sb.Append("> `—` = 该帧这一项没采到（逐帧明细只保证帧耗时可用）；整段的"
                              + "分配 / Draw Call 统计看上面的指标表。\n\n");
                }
            }

            // ---- Marker ----
            if (s.markers.Count > 0)
            {
                sb.Append("## Top Marker（自身耗时）\n\n");
                sb.Append("| # | Marker | 自身 (ms) | 总 (ms) | GC Alloc (KB) | 调用次数 |\n|---:|---|---:|---:|---:|---:|\n");
                for (int i = 0; i < s.markers.Count && i < 20; i++)
                {
                    var m = s.markers[i];
                    sb.Append("| ").Append(i + 1).Append(" | ").Append(EscapePipe(m.name)).Append(" | ")
                      .Append(Fmt(m.selfMs)).Append(" | ").Append(Fmt(m.totalMs)).Append(" | ")
                      .Append(Fmt(m.gcAllocBytes / 1024.0)).Append(" | ").Append(m.calls).Append(" |\n");
                }
                sb.Append('\n');
            }

            AppendAssetSection(sb, s);
            AppendSceneSection(sb, s);
            AppendCodeSection(sb, s);

            // ---- 附录 ----
            sb.Append("## 附录\n\n");
            if (s.capturedSources.Count > 0)
                sb.Append("- 数据来源：").Append(string.Join(", ", s.capturedSources.ToArray())).Append('\n');
            for (int i = 0; i < s.notes.Count; i++)
                sb.Append("- 提示：").Append(s.notes[i]).Append('\n');

            var text = sb.ToString();
            // 只校验规则写出来的叙述部分（prose），表格里的数字属于采集事实，用不着逐一对账。
            var unverified = UnverifiedNumbers(prose.ToString(), s, text);
            if (unverified.Count > 0)
            {
                sb.Append("\n### 未验证数值（未在证据集中找到出处，仅供人工复核）\n\n");
                for (int i = 0; i < unverified.Count && i < 30; i++)
                    sb.Append("- ").Append(unverified[i]).Append('\n');
            }

            return sb.ToString();
        }

        static void AppendAssetSection(StringBuilder sb, PerfSnapshot s)
        {
            if (s.assetIssues.Count == 0) return;
            sb.Append("## 资源问题（前 30）\n\n");
            sb.Append("| 级别 | 资源 | 类型 | 问题 | 建议 |\n|---|---|---|---|---|\n");
            for (int i = 0; i < s.assetIssues.Count && i < 30; i++)
            {
                var a = s.assetIssues[i];
                sb.Append("| ").Append(SeverityLabel(a.severity)).Append(" | `").Append(EscapePipe(a.path)).Append("` | ")
                  .Append(a.assetType).Append(" | ").Append(EscapePipe(a.issue)).Append(" | ")
                  .Append(EscapePipe(a.suggestion)).Append(" |\n");
            }
            sb.Append('\n');
        }

        static void AppendSceneSection(StringBuilder sb, PerfSnapshot s)
        {
            if (s.sceneIssues.Count == 0) return;
            sb.Append("## 场景与物理问题（前 30）\n\n");
            sb.Append("| 级别 | 对象 | 组件 | 问题 | 建议 |\n|---|---|---|---|---|\n");
            for (int i = 0; i < s.sceneIssues.Count && i < 30; i++)
            {
                var v = s.sceneIssues[i];
                sb.Append("| ").Append(SeverityLabel(v.severity)).Append(" | `").Append(EscapePipe(v.hierarchyPath)).Append("` | ")
                  .Append(v.componentType).Append(" | ").Append(EscapePipe(v.issue)).Append(" | ")
                  .Append(EscapePipe(v.suggestion)).Append(" |\n");
            }
            sb.Append('\n');
        }

        /// <summary>
        /// 闸门口径：哪些数字看着超预算、但没被算成项目问题，以及为什么。
        ///
        /// 为什么必须在报告里写：这些数字散在各处（每帧托管分配、采集窗口帧数…），
        /// 不集中说明的话，读者会拿实测值除预算自己算出「超标 7.05 倍」——
        /// 而那个数因为含编辑器开销，规则引擎本来就把它压掉了（参见 PerfGate 的注释）。
        /// </summary>
        static void AppendGateSection(StringBuilder sb, PerfSnapshot s)
        {
            var suppressed = s.SuppressedGates();
            if (suppressed.Count == 0) return;

            sb.Append("## 闸门口径（这些数字没被算成问题）\n\n");
            sb.Append("下面这些数字看着超预算，但**不是**本次归因到的项目问题 —— 直接拿它们做对比或算倍数会得出错结论：\n\n");
            sb.Append("| 数字 | 值 | 阈值 | 闸门 | 为什么 |\n|---|---:|---:|---|---|\n");
            for (int i = 0; i < suppressed.Count; i++)
            {
                var g = suppressed[i];
                sb.Append("| ").Append(EscapePipe(g.metric)).Append(" | ")
                  .Append(g.value.ToString("0.##", CultureInfo.InvariantCulture)).Append(' ').Append(g.unit).Append(" | ")
                  .Append(g.threshold.ToString("0.##", CultureInfo.InvariantCulture)).Append(' ').Append(g.unit).Append(" | ")
                  .Append(EscapePipe(g.status)).Append(" | ").Append(EscapePipe(g.verdict)).Append(" |\n");
            }
            sb.Append('\n');
        }

        static void AppendCodeSection(StringBuilder sb, PerfSnapshot s)
        {
            if (s.codeIssues.Count == 0) return;

            bool anyScoped = false, anyOther = false;
            for (int i = 0; i < s.codeIssues.Count; i++)
            {
                if (s.codeIssues[i].inSceneScope) anyScoped = true; else anyOther = true;
            }

            sb.Append("## 代码反模式（前 40）\n\n");
            if (anyScoped && anyOther)
            {
                sb.Append("> ★ = 属于**本次采集场景**所在目录的脚本 —— 前后对比只看这些行，以及指标表里的"
                          + "「每帧类代码问题（当前场景目录）」。其余行来自同工程里的其它目录（另一份副本），本次采集用不到。\n\n");
            }            sb.Append("> 「时机」列：**每帧** = Update/FixedUpdate/LateUpdate/OnGUI 等每帧方法体内（稳态分配）；"
                      + "**事件** = 碰撞/触发回调里（只在事件发生时执行，里面的 Instantiate + Destroy 是正常游戏逻辑）。\n\n");            sb.Append("| 级别 | 位置 | 时机 | 模式 | 代码 | 建议 |\n|---|---|---|---|---|---|\n");
            for (int i = 0; i < s.codeIssues.Count && i < 40; i++)
            {
                var c = s.codeIssues[i];
                sb.Append("| ").Append(SeverityLabel(c.severity)).Append(" | ")
                  .Append(c.inSceneScope && anyOther ? "★ " : string.Empty)
                  .Append('`').Append(c.file).Append(':').Append(c.line).Append("` | ")
                  .Append(CodeIssue.IsPerFrameMethod(c.pattern) ? "每帧" : "事件")
                  .Append(" | ").Append(c.pattern).Append(" | `")
                  .Append(EscapePipe(EscapeTick(c.snippet))).Append("` | ")
                  .Append(EscapePipe(c.suggestion)).Append(" |\n");
            }
            sb.Append('\n');
        }

        // =====================================================================
        // HTML
        // =====================================================================

        public static string ToHtml(PerfSnapshot s)
        {
            var sb = new StringBuilder();
            sb.Append("<!DOCTYPE html><html lang=\"zh-CN\"><head><meta charset=\"utf-8\">");
            sb.Append("<title>性能诊断报告 - ").Append(Html(s.Label())).Append("</title>");
            sb.Append(@"<style>
body{font-family:-apple-system,'Segoe UI','Microsoft YaHei',sans-serif;margin:0;padding:24px;background:#14161a;color:#e6e8eb;line-height:1.6}
h1{font-size:22px;border-bottom:1px solid #2c313a;padding-bottom:8px}
h2{font-size:17px;margin-top:28px;color:#8ab4f8}
table{border-collapse:collapse;width:100%;font-size:13px;margin:8px 0}
th,td{border:1px solid #2c313a;padding:6px 8px;text-align:left;vertical-align:top}
th{background:#1c1f26}
.badge{display:inline-block;padding:1px 8px;border-radius:10px;font-size:12px;font-weight:600}
.error{background:#5c1f22;color:#ff9a9a}.warn{background:#5c4a1f;color:#ffd479}.info{background:#1f3a5c;color:#9ac4ff}
.card{border:1px solid #2c313a;border-left-width:4px;border-radius:6px;padding:12px 14px;margin:10px 0;background:#1a1d23}
.card.error{border-left-color:#ff6b6b;background:#1f1618}.card.warn{border-left-color:#ffd479;background:#1f1d16}.card.info{border-left-color:#6ba7ff;background:#161a1f}
.ev{font-size:12px;color:#9aa4b2;margin:2px 0 2px 16px}
.path{font-family:Consolas,monospace;font-size:12px;color:#8ab4f8}
code{font-family:Consolas,monospace;background:#22262e;padding:1px 4px;border-radius:3px}
.muted{color:#8a93a0;font-size:12px}
</style></head><body>");

            sb.Append("<h1>性能诊断报告 · ").Append(Html(s.Label())).Append("</h1>");
            sb.Append("<div class=\"muted\">").Append(Html(s.capturedUtc)).Append(" · Unity ").Append(Html(s.unityVersion))
              .Append(" · ").Append(Html(s.platform)).Append(" · ").Append(Html(s.graphicsDevice))
              .Append(" · 渲染管线 ").Append(Html(s.renderPipeline))
              // 与 Markdown 口径一致：窗口帧数（录了多少）与明细抽样数分开写
              .Append(" · 采集窗口 ").Append(s.WindowFrames() > 0 ? s.WindowFrames().ToString(CultureInfo.InvariantCulture) + " 帧" : "未知")
              .Append("（明细 ").Append(s.frames.Count).Append(" 帧）</div>");
            if (s.WindowTooSmallForStats())
                sb.Append("<p class=\"muted\">窗口帧数不足 ").Append(PerfSnapshot.MinFramesForStats)
                  .Append(" 帧：统计类结论已跳过，仅资源 / 场景 / 代码审计有效。</p>");

            sb.Append("<h2>关键指标</h2><table><tr><th>指标</th><th>值</th><th>预算</th><th>来源</th></tr>");
            for (int i = 0; i < s.metrics.Count; i++)
            {
                var m = s.metrics[i];
                sb.Append("<tr><td>").Append(Html(m.name)).Append("</td><td>")
                  .Append(Fmt(m.value)).Append(' ').Append(Html(m.unit))
                  .Append("</td><td>").Append(string.IsNullOrEmpty(m.budget) ? "-" : Html(m.budget + " " + m.budgetUnit))
                  .Append("</td><td class=\"muted\">").Append(Html(m.source)).Append("</td></tr>");
            }
            sb.Append("</table>");

            sb.Append("<h2>诊断结论</h2>");
            if (s.findings.Count == 0) sb.Append("<p>未发现超出预算的问题。</p>");
            for (int i = 0; i < s.findings.Count; i++)
            {
                var f = s.findings[i];
                sb.Append("<div class=\"card ").Append(f.severity).Append("\">");
                sb.Append("<div><span class=\"badge ").Append(f.severity).Append("\">").Append(SeverityLabel(f.severity))
                  .Append("</span> <b>").Append(Html(f.title)).Append("</b> <span class=\"muted\">置信度 ")
                  .Append((f.confidence * 100).ToString("0", CultureInfo.InvariantCulture)).Append("%</span></div>");
                if (!string.IsNullOrEmpty(f.detail)) sb.Append("<div>").Append(Html(f.detail)).Append("</div>");
                if (!string.IsNullOrEmpty(f.recommendation)) sb.Append("<div><b>建议：</b>").Append(Html(f.recommendation)).Append("</div>");
                if (!string.IsNullOrEmpty(f.jumpTo)) sb.Append("<div class=\"path\">").Append(Html(f.jumpTo)).Append("</div>");
                for (int k = 0; k < f.evidence.Count; k++)
                {
                    var e = f.evidence[k];
                    sb.Append("<div class=\"ev\">· <code>").Append(Html(e.tool)).Append("</code> ")
                      .Append(Html(e.metric)).Append(" = ").Append(Html(e.value))
                      .Append(string.IsNullOrEmpty(e.unit) ? "" : " " + Html(e.unit))
                      .Append(string.IsNullOrEmpty(e.threshold) ? "" : "（预算 " + Html(e.threshold) + "）")
                      .Append(string.IsNullOrEmpty(e.source) ? "" : " <span class=\"muted\">@" + Html(e.source) + "</span>")
                      .Append("</div>");
                }
                sb.Append("</div>");
            }

            if (s.assetIssues.Count > 0)
            {
                sb.Append("<h2>资源问题（前 30）</h2><table><tr><th>级别</th><th>资源</th><th>问题</th><th>建议</th></tr>");
                for (int i = 0; i < s.assetIssues.Count && i < 30; i++)
                {
                    var a = s.assetIssues[i];
                    sb.Append("<tr><td><span class=\"badge ").Append(a.severity).Append("\">").Append(SeverityLabel(a.severity))
                      .Append("</span></td><td class=\"path\">").Append(Html(a.path)).Append("</td><td>")
                      .Append(Html(a.issue)).Append("</td><td>").Append(Html(a.suggestion)).Append("</td></tr>");
                }
                sb.Append("</table>");
            }

            if (s.codeIssues.Count > 0)
            {
                sb.Append("<h2>代码反模式（前 40）</h2><table><tr><th>级别</th><th>位置</th><th>模式</th><th>代码</th></tr>");
                for (int i = 0; i < s.codeIssues.Count && i < 40; i++)
                {
                    var c = s.codeIssues[i];
                    sb.Append("<tr><td><span class=\"badge ").Append(c.severity).Append("\">").Append(SeverityLabel(c.severity))
                      .Append("</span></td><td class=\"path\">").Append(Html(c.file)).Append(':').Append(c.line)
                      .Append("</td><td>").Append(Html(c.pattern)).Append("</td><td><code>").Append(Html(c.snippet))
                      .Append("</code></td></tr>");
                }
                sb.Append("</table>");
            }

            if (s.notes.Count > 0)
            {
                sb.Append("<h2>采集提示</h2><ul class=\"muted\">");
                for (int i = 0; i < s.notes.Count; i++) sb.Append("<li>").Append(Html(s.notes[i])).Append("</li>");
                sb.Append("</ul>");
            }

            sb.Append("</body></html>");
            return sb.ToString();
        }

        public static string Save(PerfSnapshot s, bool html)
        {
            PerfSnapshotStore.EnsureDirs();
            string dir = Path.Combine(PerfSnapshotStore.RootDir, "Reports");
            if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);

            string name = (string.IsNullOrEmpty(s.id) ? DateTime.Now.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture) : s.id)
                        + (html ? ".html" : ".md");
            string path = Path.Combine(dir, name);
            File.WriteAllText(path, html ? ToHtml(s) : ToMarkdown(s), new UTF8Encoding(false));
            return path;
        }

        // =====================================================================
        // 幻觉校验
        // =====================================================================

        /// <summary>
        /// 找出报告文本中无法在证据集中回溯的数值。
        ///
        /// 口径全部在 <see cref="NumberVerifier"/>（那部分不依赖 Unity，可以离线回归）：
        /// 「能回溯」包括直接引用、仅差精度、单位换算、求和差、求倍数、百分比 ——
        /// 早期只做字符串比对，把「658,534,588」拆成三个数字、把 3.5469 当成没出处，附录里一半是误报。
        /// </summary>
        /// <param name="dataText">
        /// 报告里由采集器写入的数据部分（环境表、指标表、口径说明等）。
        /// 这些数字都是直接从快照里抄的，不需要对账 —— 传进来可以避免把
        /// 「2026」「5060（显卡型号）」「7632（帧号）」这种事实当成可疑数字列进附录。
        /// </param>
        public static List<string> UnverifiedNumbers(string text, PerfSnapshot s, string dataText = null)
        {
            return NumberVerifier.Unverified(text, s, dataText);
        }

        // =====================================================================
        // 小工具
        // =====================================================================

        static string SeverityLabel(string severity)
        {
            if (severity == Severity.Error) return "严重";
            if (severity == Severity.Warn) return "警告";
            return "提示";
        }

        static string Fmt(double v)
        {
            if (double.IsNaN(v)) return "-";
            if (double.IsInfinity(v)) return "∞";
            return v.ToString("0.##", CultureInfo.InvariantCulture);
        }

        static string EscapePipe(string s) { return string.IsNullOrEmpty(s) ? "" : s.Replace("|", "\\|"); }
        static string EscapeTick(string s) { return string.IsNullOrEmpty(s) ? "" : s.Replace("`", "'"); }

        static string Html(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            return s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;");
        }
    }
}
