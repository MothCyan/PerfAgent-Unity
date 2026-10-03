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

        /// <summary>采集结束后的统一收尾：汇总 → 跑采集器 → 分析 → 落盘 → 设为当前快照。</summary>
        static Action<PanelCaptureData> Finisher(PerfSnapshot snap, Action<PerfSnapshot> onDone)
        {
            return delegate (PanelCaptureData data)
            {
                PerfSession.Capturing = false;
                PanelCapture.Summarize(snap, data);
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
