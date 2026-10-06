using System;
using System.Collections.Generic;
using System.Globalization;
using PerfAgent.Analysis;
using PerfAgent.Core;

namespace PerfAgent.RuleRegression
{
    /// <summary>
    /// 「本地规则引擎怎么回答提问」的回归。
    ///
    /// 这批用例的存在意义很实际：纯本地模式必须**真的**不联网，而它同时又得给出有用的回答。
    /// 所以这里钉住两件事：
    ///   1. 关键词路由确实把问题带到对应维度（问内存就答内存的结论与数字，而不是把全部结论倒一遍）；
    ///   2. 回答里必须写明「按关键词匹配、不做语义理解」这条边界，不装作听懂了。
    /// </summary>
    static class LocalAnswerTests
    {
        public static void Register(List<Action> tests)
        {
            tests.Add(LocalAnswerRoutesQuestionToDimension);
            tests.Add(LocalAnswerUnmatchedQuestionFallsBackToAll);
            tests.Add(LocalAnswerWithoutSnapshotExplainsWhatToDo);
            tests.Add(LocalAnswerWarnsWhenWindowTooSmall);
            tests.Add(LocalAnswerFlagsExplanationQuestionsUpFront);
            tests.Add(LocalAnswerBriefIsGroundedAndCompact);
        }

        static PerfSnapshot SnapshotWithFindings()
        {
            var s = new PerfSnapshot { id = "s1", label = "跟随采集", scenePath = "Assets/Scenes/SampleScene.unity" };
            s.capturedFrameCount = 600;
            s.SetMetric("帧耗时均值", "ms", 3.1, "test");
            s.SetMetric("实际 FPS", "fps", 322.0, "test");
            s.SetMetric("每帧托管分配", "B", 14435, "test");
            s.SetMetric("项目每帧分配", "B", 13970, "test");
            s.SetMetric("Draw Calls 均值", "次", 24, "test");
            s.SetMetric("纹理内存(估算)", "B", 4194304.0, "test");
            s.SetMetric("代码问题数", "个", 3, "test");

            var alloc = new PerfFinding
            {
                id = "gc_alloc_per_frame", category = "内存", severity = Severity.Error,
                title = "项目每帧托管分配约 13970 B，超出预算 2048 B",
                detail = "已扣除编辑器自身开销。",
                recommendation = "扫描脚本反模式列表。",
                confidence = 0.8f
            };
            alloc.evidence.Add(new PerfEvidence("frame_capture", "项目每帧分配", "13970", "B", "2048", "估算"));
            s.findings.Add(alloc);

            var draws = new PerfFinding
            {
                id = "draw_calls_over", category = "渲染", severity = Severity.Error,
                title = "Draw Calls 超预算（800 > 300）",
                recommendation = "图集化与合批。",
                confidence = 0.9f
            };
            draws.evidence.Add(new PerfEvidence("render_stats", "Draw Calls", "800", "次", "300", "ProfilerRecorder"));
            s.findings.Add(draws);

            return s;
        }

        /// <summary>问「每帧分配从哪来」只能命中内存维度：回答里应有内存结论与分配数字，且不含渲染结论。</summary>
        static void LocalAnswerRoutesQuestionToDimension()
        {
            var s = SnapshotWithFindings();
            string answer = LocalAnswer.Answer(s, "每帧的分配是从哪来的？");

            var dims = LocalAnswer.MatchedDimensions("每帧的分配是从哪来的？");
            True(dims.Contains("内存"), "question about allocation must route to 内存，got: " + string.Join(",", dims.ToArray()));
            True(answer.IndexOf("命中维度") >= 0, "answer must state which dimension was matched");
            True(answer.IndexOf("gc_alloc") < 0, "answer must not dump internal ids");
            True(answer.IndexOf("13970") >= 0, "answer must quote the attributable number");
            True(answer.IndexOf("Draw Calls 超预算") < 0, "unmatched dimension findings must not be listed");
            True(answer.IndexOf("不联网", StringComparison.Ordinal) >= 0, "answer must state it does not use the network");
            True(answer.IndexOf("不理解句子意思", StringComparison.Ordinal) >= 0,
                "answer must be honest about the keyword-only routing");
        }

        /// <summary>问「有没有卡顿」命中帧率维度但没有结论时，仍要给出帧率数字，不能只回一句「没有」。</summary>
        static void LocalAnswerUnmatchedQuestionFallsBackToAll()
        {
            var s = SnapshotWithFindings();
            string answer = LocalAnswer.Answer(s, "这个工程到底有什么问题？");

            var dims = LocalAnswer.MatchedDimensions("这个工程到底有什么问题？");
            Equal(0, dims.Count, "a question without keywords must not pretend to match a dimension");
            True(answer.IndexOf("没有匹配到具体维度", StringComparison.Ordinal) >= 0,
                "unmatched question must say so instead of faking understanding");
            True(answer.IndexOf("Draw Calls 超预算") >= 0, "unmatched question must still show all findings");
            True(answer.IndexOf("换几个关键词", StringComparison.Ordinal) >= 0,
                "unmatched question must suggest how to get a sharper answer");
        }

        /// <summary>没有快照时，回答要告诉用户下一步做什么，而不是抛异常或空字符串。</summary>
        static void LocalAnswerWithoutSnapshotExplainsWhatToDo()
        {
            string answer = LocalAnswer.Answer(null, "为什么卡");
            True(!string.IsNullOrEmpty(answer), "must not return empty");
            True(answer.IndexOf("跟随采集", StringComparison.Ordinal) >= 0, "must point at the next action");
        }

        /// <summary>样本不足时必须先解释「为什么没有统计结论」，否则用户会以为工具坏了。</summary>
        static void LocalAnswerWarnsWhenWindowTooSmall()
        {
            var s = SnapshotWithFindings();
            s.capturedFrameCount = 10;
            s.findings.Clear();
            s.findings.Add(new PerfFinding
            {
                id = "sample_too_small", category = "采集", severity = Severity.Info,
                title = "采集窗口只有 10 帧，统计类结论已跳过",
                recommendation = "多操作几秒。",
                confidence = 0.9f
            });

            string answer = LocalAnswer.Answer(s, "为什么这么卡？");
            True(answer.IndexOf("样本不足", StringComparison.Ordinal) >= 0,
                "tiny window must be explained before listing findings");
            True(answer.IndexOf("采集窗口只有 10 帧", StringComparison.Ordinal) >= 0,
                "the info-level finding of the matched dimension must still be shown");
        }

        /// <summary>
        /// 「为什么 / 我该先修哪个」这类问题要的是因果与取舍，本地引擎给不了。
        /// 必须**在开头**就说清楚 —— 藏在末尾的话，用户看到的就是一段看着很像答案的结论列表，
        /// 这正是「看不出用不用 AI 区别」的来源。
        /// </summary>
        static void LocalAnswerFlagsExplanationQuestionsUpFront()
        {
            var s = SnapshotWithFindings();
            True(LocalAnswer.IsExplanationQuestion("为什么只有战斗时才卡？"), "为什么 类问题必须被识别为解释类");
            True(LocalAnswer.IsExplanationQuestion("我该先修哪一个？"), "决策类问题必须被识别为解释类");
            True(!LocalAnswer.IsExplanationQuestion("每帧分配是多少？"), "事实类问题不该被误判为解释类");

            string answer = LocalAnswer.Answer(s, "为什么只有战斗时才卡？");
            int flag = answer.IndexOf("解释类", StringComparison.Ordinal);
            int findings = answer.IndexOf("### 结论", StringComparison.Ordinal);
            True(flag >= 0, "解释类问题必须明确提示本地答不了");
            True(findings < 0 || flag < findings, "这条提示必须在结论之前出现");
            True(answer.IndexOf("因果", StringComparison.Ordinal) >= 0, "要说明本地给不出什么（因果 / 取舍）");
        }

        /// <summary>
        /// 给 LLM 的事实底稿：要带得上可引用的数字与证据，且不要把「给人看的说明」也塞进去
        /// （底稿会跟着每一条提问发给服务商，写废话就是花钱）。
        /// </summary>
        static void LocalAnswerBriefIsGroundedAndCompact()
        {
            var s = SnapshotWithFindings();
            string brief = LocalAnswer.BriefForPrompt(s, "每帧的分配是从哪来的？");

            True(brief.IndexOf("13970", StringComparison.Ordinal) >= 0, "brief must carry the attributable number");
            True(brief.IndexOf("内存", StringComparison.Ordinal) >= 0, "brief must name the matched dimension");
            True(brief.IndexOf("证据", StringComparison.Ordinal) >= 0, "brief must carry evidence lines");
            True(brief.IndexOf("不要凭空推测", StringComparison.Ordinal) >= 0, "brief must tell the model not to invent numbers");
            True(brief.IndexOf("本地引擎的边界", StringComparison.Ordinal) < 0,
                "brief is for the model, not for the user — human-facing sections must stay out");
            True(brief.IndexOf("|---", StringComparison.Ordinal) < 0, "brief must not carry markdown tables");

            string empty = LocalAnswer.BriefForPrompt(null, "随便问问");
            True(!string.IsNullOrEmpty(empty), "brief without a snapshot must still be safe to send");
        }

        static void True(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
        static void Equal(int expected, int actual, string label)
        {
            if (expected != actual) throw new InvalidOperationException(label + ": expected " + expected + ", got " + actual);
        }
    }
}
