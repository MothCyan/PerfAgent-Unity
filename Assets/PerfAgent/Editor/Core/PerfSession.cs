using System;
using PerfAgent.Core;

namespace PerfAgent.Core
{
    /// <summary>
    /// 会话状态：当前快照 + 抓帧结果，UI / 工具 / Agent 共享。
    /// 用静态类而非 ScriptableSingleton，避免域重载时序列化大对象。
    /// </summary>
    public static class PerfSession
    {
        public static PerfSnapshot Current;
        public static string CurrentPath = "";
        public static bool Capturing;

        /// <summary>快照或分析结果发生变化时触发，供 UI 刷新。</summary>
        public static event Action Changed;

        public static void NotifyChanged()
        {
            var handler = Changed;
            if (handler != null) handler();
        }

        public static void SetCurrent(PerfSnapshot snapshot, string path)
        {
            Current = snapshot;
            CurrentPath = path ?? "";
            NotifyChanged();
        }
    }
}
