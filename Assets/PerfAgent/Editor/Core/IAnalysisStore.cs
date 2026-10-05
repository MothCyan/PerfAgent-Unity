using System;
using System.Collections.Generic;

namespace PerfAgent.Core
{
    /// <summary>
    /// 持久化后端的抽象 —— 目前只有 JSONL 实现，SQLite 按同一接口后期接入。
    ///
    /// <para><b>为什么要抽象这一层</b></para>
    /// 需求是两件事：**分析数据的目录**（有哪些快照、什么场景、多少结论）+ **操作日志**
    /// （每次做了什么、谁同意、结果如何、能不能撤销）。这两件事的查询都很浅，
    /// 但「谁来读」有两种：
    ///   · 人 —— 想直接打开文件看，或者 `type xxx.jsonl`；
    ///   · AI —— 需要一份可当上下文的、结构稳定的历史（做过什么、结论怎么变的）。
    /// 于是默认实现选 JSONL（零依赖、可读、可直接喂模型），SQLite 实现只需满足下面这几个方法，
    /// 上层（面板 / Agent / MCP）不用改一行。
    ///
    /// <para><b>接 SQLite 时要注意的</b></para>
    /// 1. Editor 程序集可以加载托管 SQLite，但要带原生库并按平台分发（Windows/macOS/Linux）；
    /// 2. 表结构直接照着下面两个 record 建即可（列名一样），迁移时用 JSONL 回灌；
    /// 3. **无论用哪个后端，都必须保持「追加写」语义** —— 审计日志不允许被覆盖或改写历史。
    /// </summary>
    public interface IAnalysisStore
    {
        /// <summary>后端名字（显示在设置面板里，便于确认当前用的是哪个）。</summary>
        string Backend { get; }

        /// <summary>追加一条「分析目录」记录：哪一份快照、什么场景、多少结论。</summary>
        void AppendAnalysis(AnalysisIndexRecord record);

        /// <summary>追加一条「操作日志」记录：做了什么、谁同意、结果、能否撤销。</summary>
        void AppendOperation(OperationRecord record);

        /// <summary>读最近的分析目录（新的在前）。</summary>
        List<AnalysisIndexRecord> RecentAnalyses(int max);

        /// <summary>读最近的操作日志（新的在前）。</summary>
        List<OperationRecord> RecentOperations(int max);
    }

    /// <summary>分析目录的一条记录（给人和 AI 看同一份）。</summary>
    [Serializable]
    public class AnalysisIndexRecord
    {
        public string utc = "";
        public string snapshotId = "";
        public string label = "";
        public string scene = "";
        /// <summary>数据来源：跟随采集 / 静态审计 / 快照载入。</summary>
        public string source = "";
        public int frames;
        public int metrics;
        public int findings;
        public string engineVersion = "";
    }

    /// <summary>
    /// 操作日志的一条记录。
    ///
    /// <para>为什么必须记「同意」这一栏</para>
    /// 「AI 操作了工程」这件事的追责点不在动作本身，而在**谁批准的**：
    /// `consent` 为空表示人自己在面板点过（默认），
    /// `"mcp:&lt;client&gt;"` 表示外部模型发起、且经过同意门批准。
    /// 事后要能回答「这条改动是谁授意的」。
    /// </summary>
    [Serializable]
    public class OperationRecord
    {
        public string utc = "";
        /// <summary>发起方：human（面板）/ ai（Agent）/ mcp（外部客户端）。</summary>
        public string actor = "";
        /// <summary>动作类别：fix（改工程）/ undo（撤销）/ capture（采集）/ export（导出）。</summary>
        public string kind = "";
        public string actionId = "";
        public string title = "";
        public string findingId = "";
        /// <summary>同意来源；空 = 面板上人工确认。</summary>
        public string consent = "";
        public bool success;
        public int changedCount;
        public string message = "";
        public bool canUndo;
        public bool undone;
        public string undoneUtc = "";
    }
}
