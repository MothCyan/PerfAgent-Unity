using System;
using System.Collections.Generic;
using System.Text;
using PerfAgent.Utils;
using UnityEngine.Profiling;

namespace PerfAgent.Core
{
    /// <summary>
    /// 内存 API 统一入口（全部走反射，避免跨版本编译问题）。
    /// </summary>
    public static class MemApi
    {
        static readonly Type T = typeof(Profiler);

        public static bool Has(string name) { return Reflect.Method(T, name, -1, true) != null; }

        static long Call(string name)
        {
            var v = Reflect.InvokeStatic(T, name);
            if (v == null) return 0L;
            try { return Convert.ToInt64(v); } catch { return 0L; }
        }

        public static long TotalAllocated { get { return Call("GetTotalAllocatedMemoryLong"); } }
        public static long TotalReserved { get { return Call("GetTotalReservedMemoryLong"); } }
        public static long MonoUsed { get { return Call("GetMonoUsedSizeLong"); } }
        public static long MonoHeap { get { return Call("GetMonoHeapSizeLong"); } }
        public static long TempAllocator { get { return Call("GetTempAllocatorSize"); } }
        public static long GraphicsDriver { get { return Call("GetAllocatedMemoryForGraphicsDriver"); } }

        /// <summary>单个对象的运行时内存占用（字节），不可用时返回 -1。</summary>
        public static long RuntimeSize(UnityEngine.Object obj)
        {
            if (obj == null) return -1L;
            var v = Reflect.InvokeStatic(T, "GetRuntimeMemorySizeLong", obj);
            if (v == null) return -1L;
            try { return Convert.ToInt64(v); } catch { return -1L; }
        }
    }

    /// <summary>
    /// UnityEditorInternal.ProfilerDriver / Profiling.FrameDataView 的反射封装。
    /// 这些类型跨版本差异最大，也最容易被标记为 internal，因此全部反射。
    /// </summary>
    public static class ProfilerApi
    {
        public static readonly Type Driver = Reflect.FindType("UnityEditorInternal.ProfilerDriver");
        public static readonly Type HierarchyView = Reflect.FindType("UnityEditor.Profiling.HierarchyFrameDataView");
        public static readonly Type RawView = Reflect.FindType("UnityEditor.Profiling.RawFrameDataView");
        public static readonly Type FrameDataView = Reflect.FindType("UnityEditor.Profiling.FrameDataView");
        static readonly Type ViewModes = Reflect.NestedType(HierarchyView, "ViewModes");

        /// <summary>抓帧（FrameDataView）能力是否可用。</summary>
        public static bool CanReadFrames { get { return Driver != null && HierarchyView != null; } }

        /// <summary>Profiler 是否正在记录。</summary>
        public static bool Enabled
        {
            get
            {
                var v = Reflect.GetStatic(Driver, "enabled");
                if (v is bool) return (bool)v;
                v = Reflect.GetStatic(Driver, "profileEditor");
                return v is bool && (bool)v;
            }
            set
            {
                if (!Reflect.SetStatic(Driver, "enabled", value))
                    Reflect.SetStatic(Driver, "profileEditor", value);
            }
        }

        public static int FirstFrameIndex
        {
            get { var v = Reflect.GetStatic(Driver, "firstFrameIndex"); return v == null ? -1 : SafeInt(v); }
        }

        public static int LastFrameIndex
        {
            get { var v = Reflect.GetStatic(Driver, "lastFrameIndex"); return v == null ? -1 : SafeInt(v); }
        }

        public static int FrameCount
        {
            get
            {
                int a = FirstFrameIndex, b = LastFrameIndex;
                if (a < 0 || b < a) return 0;
                return b - a + 1;
            }
        }

        static int SafeInt(object v) { try { return Convert.ToInt32(v); } catch { return -1; } }

        public static void ClearAllFrames()
        {
            Reflect.InvokeStatic(Driver, "ClearAllFrames");
        }

        public static int MaxHistoryLength
        {
            get { var v = Reflect.GetStatic(Driver, "maxHistoryLength"); return v == null ? 300 : SafeInt(v); }
            set { Reflect.SetStatic(Driver, "maxHistoryLength", value); }
        }

        /// <summary>把当前已抓到的帧存成 .raw 文件。</summary>
        public static bool SaveProfile(string path)
        {
            var r = Reflect.InvokeStatic(Driver, "SaveProfile", path);
            return r is bool && (bool)r;
        }

        /// <summary>加载 .raw 文件（离线分析路径），成功后可继续读 FrameDataView。</summary>
        public static bool LoadProfile(string path, bool keepExisting = false)
        {
            var r = InvokeWithFallback("LoadProfile", new object[] { path, keepExisting }, new object[] { path });
            return r is bool && (bool)r;
        }

        static object InvokeWithFallback(string name, object[] primary, object[] fallback)
        {
            var r = Reflect.InvokeStatic(Driver, name, primary);
            if (r == null) r = Reflect.InvokeStatic(Driver, name, fallback);
            return r;
        }

        /// <summary>取得指定帧的主线程 Hierarchy 视图；不可用返回 null。</summary>
        public static object GetHierarchyView(int frameIndex, int threadIndex = 0)
        {
            if (Driver == null || HierarchyView == null) return null;

            object mode = null;
            if (ViewModes != null && ViewModes.IsEnum)
            {
                try { mode = Enum.Parse(ViewModes, "Tree"); }
                catch { mode = null; }
            }

            object r = null;
            if (mode != null)
                r = Reflect.InvokeStatic(Driver, "GetHierarchyFrameDataView", frameIndex, threadIndex, mode, 0, true);
            if (r == null)
                r = Reflect.InvokeStatic(Driver, "GetHierarchyFrameDataView", frameIndex, threadIndex);
            return r;
        }

        /// <summary>取得指定帧的原始采样视图；不可用返回 null。</summary>
        public static object GetRawView(int frameIndex, int threadIndex = 0)
        {
            if (Driver == null || RawView == null) return null;
            return Reflect.InvokeStatic(Driver, "GetRawFrameDataView", frameIndex, threadIndex);
        }

        /// <summary>
        /// 取出视图的子项 id 列表。
        /// 首选 GetItemChildren(int, List&lt;int&gt;)：反射调用会通过 object[] 把结果写回容器。
        /// </summary>
        public static List<int> GetChildren(object view, int id)
        {
            var result = new List<int>();
            if (view == null) return result;

            var args = new object[] { id, result };
            object ignored;
            if (Reflect.TryInvoke(view, "GetItemChildren", out ignored, args))
            {
                var filled = args[1] as List<int>;
                return filled ?? result;
            }

            // 回退：GetItemChildren(int) 返回数组
            var arr = Reflect.Invoke(view, "GetItemChildren", id) as Array;
            if (arr != null)
            {
                foreach (var o in arr)
                {
                    try { result.Add(Convert.ToInt32(o)); } catch { }
                }
            }
            return result;
        }

        public static int GetRootItemId(object view)
        {
            foreach (var name in new[] { "GetRootItemID", "GetRootItemId", "rootItemId", "RootItemID" })
            {
                var v = Reflect.Invoke(view, name);
                if (v == null) v = Reflect.Get(view, name);
                if (v == null) continue;
                try { return Convert.ToInt32(v); } catch { }
            }
            return 0;
        }

        public static string GetItemName(object view, int id)
        {
            foreach (var name in new[] { "GetItemName", "GetItemPath", "GetItemFullName" })
            {
                string s;
                if (Reflect.TryInvoke(view, name, out var r, id) && r != null)
                {
                    s = r as string;
                    if (!string.IsNullOrEmpty(s)) return s;
                }
            }
            return "(unknown)";
        }

        /// <summary>读取某行某列的字符串值。列语义按内容自动识别（见 ProfilerMarkerCollector）。</summary>
        public static string GetItemColumn(object view, int id, int column)
        {
            foreach (var name in new[] { "GetItemColumnData", "GetItemColumnDataString" })
            {
                string s;
                if (Reflect.TryInvoke(view, name, out var r, id, column) && r != null)
                {
                    s = r as string;
                    if (s != null) return s;
                }
            }
            return null;
        }

        public static int GetColumnCount(object view)
        {
            foreach (var name in new[] { "columnCount", "ColumnCount" })
            {
                var v = Reflect.Get(view, name);
                if (v != null)
                {
                    try { return Convert.ToInt32(v); } catch { }
                }
            }
            return 8;
        }

        /// <summary>诊断信息，用于探针窗口与报告 notes。</summary>
        public static string DescribeAvailability()
        {
            var sb = new StringBuilder();
            sb.Append("ProfilerDriver: ").Append(Driver != null ? "OK" : "缺失").Append('\n');
            sb.Append("HierarchyFrameDataView: ").Append(HierarchyView != null ? "OK" : "缺失").Append('\n');
            sb.Append("RawFrameDataView: ").Append(RawView != null ? "OK" : "缺失").Append('\n');
            sb.Append("FrameDataView: ").Append(FrameDataView != null ? "OK" : "缺失").Append('\n');
            if (Driver != null)
            {
                sb.Append("  enabled: ").Append(Prop(Driver, "enabled") ? "OK" : "-")
                  .Append(" | firstFrameIndex: ").Append(Prop(Driver, "firstFrameIndex") ? "OK" : "-")
                  .Append(" | lastFrameIndex: ").Append(Prop(Driver, "lastFrameIndex") ? "OK" : "-")
                  .Append(" | ClearAllFrames: ").Append(Reflect.Method(Driver, "ClearAllFrames", 0, true) != null ? "OK" : "-")
                  .Append(" | SaveProfile: ").Append(Reflect.Method(Driver, "SaveProfile", -1, true) != null ? "OK" : "-")
                  .Append(" | LoadProfile: ").Append(Reflect.Method(Driver, "LoadProfile", -1, true) != null ? "OK" : "-")
                  .Append(" | GetHierarchyFrameDataView: ").Append(Reflect.Method(Driver, "GetHierarchyFrameDataView", -1, true) != null ? "OK" : "-")
                  .Append('\n');
            }
            return sb.ToString();
        }

        static bool Prop(Type t, string name) { return Reflect.Prop(t, name, true) != null || Reflect.Fld(t, name, true) != null; }
    }
}
