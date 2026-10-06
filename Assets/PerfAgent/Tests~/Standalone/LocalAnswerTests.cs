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

        static void True(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
        static void Equal(int expected, int actual, string label)
        {
            if (expected != actual) throw new InvalidOperationException(label + ": expected " + expected + ", got " + actual);
        }
    }
}
