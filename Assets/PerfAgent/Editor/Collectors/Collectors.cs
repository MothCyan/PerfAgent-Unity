using System;
using System.Collections.Generic;
using PerfAgent.Core;

namespace PerfAgent.Collectors
{
    /// <summary>采集上下文：所有采集器共享同一份快照与配置。</summary>
    public class CollectorContext
    {
        public PerfSnapshot snapshot;
        public PerfBudget budget;
        public Action<string> progress = delegate { };
        public bool deep;          // 深度模式（更慢，扫描更多资源）
        public int topN = 20;

        public CollectorContext(PerfSnapshot snapshot, PerfBudget budget)
        {
            this.snapshot = snapshot;
            this.budget = budget;
        }

        public void Note(string message)
        {
            if (snapshot != null) snapshot.AddNote(message);
        }
    }

    /// <summary>
    /// 采集器契约。每个采集器对应 Agent 的一个工具，两者一一映射。
    /// </summary>
    public interface IPerfCollector
    {
        string Name { get; }          // 中文名，给 UI 用
        string ToolName { get; }      // 英文名，给 Agent 工具用
        string Description { get; }   // 给 LLM 看的说明
        void Collect(CollectorContext ctx);
    }

    public static class CollectorRegistry
    {
        /// <summary>所有「可独立运行」的采集器。帧采样由 FrameCapture 单独编排。</summary>
        public static List<IPerfCollector> All()
        {
            return new List<IPerfCollector>
            {
                new EnvironmentCollector(),
                new MemoryCollector(),
                new RenderStatsCollector(),
                new FrameTimingCollector(),
                new ProfilerMarkerCollector(),
                new AssetAuditCollector(),
                new SceneAuditCollector(),
                new PhysicsAuditCollector(),
                new ScriptAntipatternCollector(),
            };
        }

        public static IPerfCollector Find(string toolName)
        {
            var all = All();
            for (int i = 0; i < all.Count; i++)
                if (all[i].ToolName == toolName) return all[i];
            return null;
        }

        /// <summary>按工具名运行单个采集器到给定快照。</summary>
        public static bool RunOne(string toolName, PerfSnapshot snapshot, PerfBudget budget, bool deep = false)
        {
            var c = Find(toolName);
            if (c == null) return false;
            var ctx = new CollectorContext(snapshot, budget);
            ctx.deep = deep;
            c.Collect(ctx);
            if (!snapshot.capturedSources.Contains(c.ToolName)) snapshot.capturedSources.Add(c.ToolName);
            return true;
        }
    }
}
