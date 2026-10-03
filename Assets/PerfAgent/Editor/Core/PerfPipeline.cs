using System;
using System.Collections.Generic;
using System.Globalization;
using PerfAgent.Analysis;
using PerfAgent.Collectors;

namespace PerfAgent.Core
{
    /// <summary>
    /// 采集 → 分析 → 落盘 的编排。UI 与 Agent 工具共用同一条流水线，
    /// 保证「人工点按钮」与「Agent 自己调用」得到的结果完全一致。
    /// </summary>
    public static class PerfPipeline
    {
        /// <summary>
        /// 单次采集的帧数上限。30 万帧 @60fps ≈ 83 分钟，够跑完长流程；
        /// 再长建议分多次采集 —— 否则单份快照的统计值会把不同关卡混在一起。
        /// </summary>
        public const int MaxCaptureFrames = 300000;

        /// <summary>单次采集的时长上限（秒）—— 1 小时。</summary>
        public const double MaxCaptureSeconds = 3600;

        public static PerfSnapshot CreateSnapshot(string label = null)
        {
            var snap = new PerfSnapshot();
            snap.id = DateTime.Now.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture);
            snap.label = label ?? ("快照 " + snap.id);
            return snap;
        }

        /// <summary>运行所有「被动」采集器（不需要等待若干帧的那些）。</summary>
        public static void RunCollectors(PerfSnapshot snap, bool deep, Action<string> progress = null, string only = null)
        {
            var all = CollectorRegistry.All();
            for (int i = 0; i < all.Count; i++)
            {
                var c = all[i];
                if (only != null && c.ToolName != only) continue;
                if (progress != null) progress("采集中：" + c.Name);

                try
                {
                    var ctx = new CollectorContext(snap, PerfAgentSettings.Config.budget);
                    ctx.deep = deep;
                    c.Collect(ctx);
                    if (!snap.capturedSources.Contains(c.ToolName)) snap.capturedSources.Add(c.ToolName);
                }
                catch (Exception e)
                {
                    snap.AddNote(c.Name + " 采集失败: " + e.Message);
                }
            }
        }

        public static List<PerfFinding> Analyze(PerfSnapshot snap)
        {
            var engine = new PerfRuleEngine();
            var findings = engine.Evaluate(snap, PerfAgentSettings.Config.budget);
            PerfSnapshotStore.ApplyBudget(snap, PerfAgentSettings.Config.budget);
            return findings;
        }

        /// <summary>采集 N 帧（异步，需要真实时间流逝），完成后回调。</summary>
        public static FrameCapture CaptureFrames(int frames, Action<PerfSnapshot> onDone, Action<float> onProgress = null, string label = null)
        {
            var snap = CreateSnapshot(label);
            var capture = new FrameCapture(frames, Finisher(snap, onDone), onProgress);
            BeginAfterBaseline(capture);
            return capture;
        }

        /// <summary>
        /// 按**时长**采集（秒）。maxFrames 只是安全上限。
        ///
        /// 用于「跑完一整个游戏流程」这类需求：流程长度是以秒描述的，
        /// 用帧数描述既不准（帧率变了就不是同一段时间）也难算。
        /// </summary>
        public static FrameCapture CaptureFrames(int maxFrames, double seconds, Action<PerfSnapshot> onDone, Action<float> onProgress = null, string label = null)
        {
            var snap = CreateSnapshot(label);

            snap.AddNote(string.Format(CultureInfo.InvariantCulture,
                "本次为按时长采集：目标 {0:0.#} 秒（安全上限 {1} 帧）。时长模式下帧数由实际帧率决定，不固定。",
                seconds, maxFrames));

            var capture = new FrameCapture(maxFrames, seconds, Finisher(snap, onDone), onProgress);
            BeginAfterBaseline(capture);
            return capture;
        }

        /// <summary>
        /// 抓帧前先把「编辑器空闲开销基线」量出来。
        ///
        /// 不量的话，「每帧托管分配」里编辑器自身的开销会被算到项目头上 ——
        /// 一个空工程也能报出上百 KB/帧（实测同一空工程两次采集差 6 倍，差的就是编辑器状态）。
        /// 已经在 Play 里时测不到基线，直接开始并如实降级：本次不对该指标下结论。
        /// 注意返回的 handle 仍然是立即可用的，只是 Start 会晚 ~0.4 秒。
        /// </summary>
        static void BeginAfterBaseline(FrameCapture capture)
        {
            double value;
            string why;
            if (!EditorOverheadBaseline.TryGet(out value, out why) && !UnityEditor.EditorApplication.isPlaying)
            {
                EditorOverheadBaseline.Measure(delegate
                {
                    PerfSession.Capturing = true;
                    capture.Start();
                });
                return;
            }

            PerfSession.Capturing = true;
            capture.Start();
        }

        /// <summary>采集结束后的统一收尾：汇总 → 跑采集器 → 分析 → 落盘 → 设为当前快照。</summary>
        static Action<List<FrameStat>> Finisher(PerfSnapshot snap, Action<PerfSnapshot> onDone)
        {
            return delegate (List<FrameStat> list)
            {
                PerfSession.Capturing = false;
                FrameCapture.Summarize(snap, list);
                RunCollectors(snap, false, null);
                Analyze(snap);

                string path = PerfSnapshotStore.Save(snap);
                PerfSession.SetCurrent(snap, path);
                if (onDone != null) onDone(snap);
            };
        }

        /// <summary>不抓帧，只做静态审计（资源/场景/代码/内存），秒级完成。</summary>
        public static PerfSnapshot StaticAudit(bool deep, Action<string> progress = null)
        {
            var snap = CreateSnapshot("静态审计");
            RunCollectors(snap, deep, progress, null);

            // 去掉需要真实帧数据的采集器带来的空指标噪声
            Analyze(snap);
            string path = PerfSnapshotStore.Save(snap);
            PerfSession.SetCurrent(snap, path);
            return snap;
        }

        public static string SaveAndSetCurrent(PerfSnapshot snap)
        {
            string path = PerfSnapshotStore.Save(snap);
            PerfSession.SetCurrent(snap, path);
            return path;
        }
    }
}
