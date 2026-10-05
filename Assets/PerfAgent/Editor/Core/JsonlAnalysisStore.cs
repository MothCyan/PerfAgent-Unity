using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;

namespace PerfAgent.Core
{
    /// <summary>
    /// 默认持久化后端：JSONL（一行一条记录，落在 `ProjectSettings/PerfAgent/Log/`）。
    ///
    /// <list type="bullet">
    ///   <item>`index.jsonl` —— **分析目录**：每次分析产出的快照、场景、帧数、结论数；</item>
    ///   <item>`operations.jsonl` —— **操作日志**：每次改工程/撤销，谁同意的、结果如何、能不能撤销。</item>
    /// </list>
    ///
    /// 两个文件都能直接打开读，也能整份交给模型当上下文 —— 这正是「让 AI 有上下文」最省事的形态：
    /// 它不需要理解我们的数据库 schema，只需要读一行行 JSON。
    /// 换成 SQLite 时实现同一个 <see cref="IAnalysisStore"/> 即可，调用方不用改。
    /// </summary>
    public class JsonlAnalysisStore : IAnalysisStore
    {
        public const string IndexFile = "index.jsonl";
        public const string OperationFile = "operations.jsonl";

        /// <summary>全局后端；后期接 SQLite 时在启动处替换即可（面板里会显示当前用的是哪个）。</summary>
        public static IAnalysisStore Current = new JsonlAnalysisStore();

        public string Backend { get { return "JSONL（ProjectSettings/PerfAgent/Log）"; } }

        public void AppendAnalysis(AnalysisIndexRecord record)
        {
            if (record == null) return;
            if (string.IsNullOrEmpty(record.utc)) record.utc = UtcNow();
            JsonlLog.Append(IndexFile, JsonUtility.ToJson(record));
        }

        public void AppendOperation(OperationRecord record)
        {
            if (record == null) return;
            if (string.IsNullOrEmpty(record.utc)) record.utc = UtcNow();
            JsonlLog.Append(OperationFile, JsonUtility.ToJson(record));
        }

        public List<AnalysisIndexRecord> RecentAnalyses(int max)
        {
            return Parse<AnalysisIndexRecord>(JsonlLog.ReadLast(IndexFile, max));
        }

        public List<OperationRecord> RecentOperations(int max)
        {
            return Parse<OperationRecord>(JsonlLog.ReadLast(OperationFile, max));
        }

        /// <summary>单行坏掉不能影响其余行 —— 日志的价值在「能一直读下去」。</summary>
        static List<T> Parse<T>(List<string> lines) where T : class
        {
            var result = new List<T>();
            if (lines == null) return result;

            for (int i = 0; i < lines.Count; i++)
            {
                try
                {
                    var item = JsonUtility.FromJson<T>(lines[i]);
                    if (item != null) result.Add(item);
                }
                catch { }
            }
            result.Reverse();      // 新的在前
            return result;
        }

        static string UtcNow()
        {
            return DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture);
        }
    }

    /// <summary>调用方用的门面：不关心后端是谁，也不允许因为日志问题中断主流程。</summary>
    public static class PerfHistory
    {
        static IAnalysisStore Store { get { return JsonlAnalysisStore.Current ?? (JsonlAnalysisStore.Current = new JsonlAnalysisStore()); } }

        public static string BackendName { get { return Store.Backend; } }

        /// <summary>记一条分析目录。失败静默 —— 日志问题不该影响正在做的分析。</summary>
        public static void RecordAnalysis(string snapshotId, string label, string scene, string source,
                                          int frames, int metrics, int findings)
        {
            try
            {
                var r = new AnalysisIndexRecord();
                r.snapshotId = snapshotId;
                r.label = label;
                r.scene = scene;
                r.source = source;
                r.frames = frames;
                r.metrics = metrics;
                r.findings = findings;
                Store.AppendAnalysis(r);
            }
            catch { }
        }

        /// <summary>
        /// 记一条操作日志。`consent` 必须如实填：空 = 面板上人工确认，
        /// 其他值要能看出是谁批准的（例如 `mcp:claude-desktop`）。
        /// </summary>
        public static void RecordOperation(string actor, string kind, string actionId, string title,
                                           string findingId, string consent, bool success,
                                           int changedCount, string message, bool canUndo)
        {
            try
            {
                var r = new OperationRecord();
                r.actor = actor;
                r.kind = kind;
                r.actionId = actionId;
                r.title = title;
                r.findingId = findingId == null ? "" : findingId;
                r.consent = consent == null ? "" : consent;
                r.success = success;
                r.changedCount = changedCount;
                r.message = message == null ? "" : message;
                r.canUndo = canUndo;
                Store.AppendOperation(r);
            }
            catch { }
        }

        /// <summary>最近 N 条操作（新的在前），给面板与 AI 当上下文。</summary>
        public static List<OperationRecord> RecentOperations(int max)
        {
            try { return Store.RecentOperations(max); }
            catch { return new List<OperationRecord>(); }
        }

        /// <summary>最近 N 次分析（新的在前）。</summary>
        public static List<AnalysisIndexRecord> RecentAnalyses(int max)
        {
            try { return Store.RecentAnalyses(max); }
            catch { return new List<AnalysisIndexRecord>(); }
        }

        /// <summary>给模型上下文用的紧凑摘要（避免把整份日志塞进 prompt）。</summary>
        public static string Digest(int maxOps = 12, int maxAnalyses = 8)
        {
            var sb = new System.Text.StringBuilder();
            var analyses = RecentAnalyses(maxAnalyses);
            var ops = RecentOperations(maxOps);

            if (analyses.Count > 0)
            {
                sb.Append("### 最近的分析（新→旧）\n");
                for (int i = 0; i < analyses.Count; i++)
                {
                    var a = analyses[i];
                    sb.Append("- ").Append(a.utc).Append("  ").Append(a.snapshotId)
                      .Append("  [").Append(a.source).Append("]  ")
                      .Append(string.IsNullOrEmpty(a.scene) ? "(未记录场景)" : a.scene)
                      .Append("  帧 ").Append(a.frames)
                      .Append("  结论 ").Append(a.findings).Append('\n');
                }
            }

            if (ops.Count > 0)
            {
                sb.Append("### 最近的操作（新→旧，含是否同意）\n");
                for (int i = 0; i < ops.Count; i++)
                {
                    var o = ops[i];
                    sb.Append("- ").Append(o.utc).Append("  ").Append(o.kind).Append('/').Append(o.actionId)
                      .Append("  ").Append(o.title)
                      .Append("  发起=").Append(string.IsNullOrEmpty(o.actor) ? "human" : o.actor)
                      .Append("  同意=").Append(string.IsNullOrEmpty(o.consent) ? "面板人工确认" : o.consent)
                      .Append("  结果=").Append(o.success ? ("成功" + (o.changedCount > 0 ? ("(" + o.changedCount + " 项)") : "")) : "失败")
                      .Append(o.undone ? "  已撤销" : (o.canUndo ? "  可撤销" : ""))
                      .Append('\n');
                }
            }

            return sb.ToString();
        }
    }
}
