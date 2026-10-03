using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
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

        /// <summary>
        /// 视图模式候选名 —— 不同版本成员命名不同，不能写死：
        ///   2019/2020：Tree / Flat
        ///   2022 起  ：Default / MergeSamplesWithTheSameName / HideEditorOnlySamples（**没有 Tree**）
        /// 按顺序取第一个存在的成员；<c>Default</c> 就是「树形展开」，语义等价于旧版的 Tree。
        /// 参考资料：本机 2022.3.62f2c1 的 ViewModes = Default=0, MergeSamplesWithTheSameName=1, HideEditorOnlySamples=2。
        /// </summary>
        static readonly string[] ViewModeNames = { "Tree", "Default", "Flat" };
        static object[] _viewModes;
        static string _lastHierarchyError;

        /// <summary>最近一次 GetHierarchyView 取视图失败的原因（成功时为 null），用于把采集失败写成可定位的 notes。</summary>
        public static string LastHierarchyError { get { return _lastHierarchyError; } }

        /// <summary>本机 ViewModes 实际存在的成员名（探针用）。</summary>
        public static string DescribeViewModes()
        {
            if (ViewModes == null) return "缺失";
            try { return string.Join(" / ", Enum.GetNames(ViewModes)); }
            catch { return "读取失败"; }
        }

        static object[] ResolveViewModes()
        {
            if (_viewModes != null) return _viewModes;

            var list = new List<object>();
            if (ViewModes != null && ViewModes.IsEnum)
            {
                for (int i = 0; i < ViewModeNames.Length; i++)
                {
                    try { list.Add(Enum.Parse(ViewModes, ViewModeNames[i])); }
                    catch { /* 该版本没有这个名字，跳过 */ }
                }
                if (list.Count == 0)
                {
                    // 兜底：枚举声明顺序的第一个成员（多数版本是数值 0）
                    try
                    {
                        var names = Enum.GetNames(ViewModes);
                        if (names.Length > 0) list.Add(Enum.Parse(ViewModes, names[0]));
                    }
                    catch { }
                }
            }
            _viewModes = list.ToArray();
            return _viewModes;
        }

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

        /// <summary>
        /// 取得指定帧的主线程 Hierarchy 视图；不可用返回 null。
        /// 本机只有 5 参数重载 GetHierarchyFrameDataView(frameIndex, threadIndex, viewMode, sortColumn, sortAscending)，
        /// 没有 2 参数重载，也没有 forceCollect —— 老代码在 viewMode 解析失败后回退到 2 参数重载必然拿到 null，
        /// 所以这里改成「逐个候选 viewMode × sortColumn 尝试」，任一成功即返回。
        /// </summary>
        public static object GetHierarchyView(int frameIndex, int threadIndex = 0)
        {
            _lastHierarchyError = null;

            if (Driver == null || HierarchyView == null)
            {
                _lastHierarchyError = "ProfilerDriver / HierarchyFrameDataView 缺失";
                return null;
            }
            if (frameIndex < 0)
            {
                _lastHierarchyError = "帧索引无效（" + frameIndex + "）";
                return null;
            }

            var modes = ResolveViewModes();
            int modeCount = modes.Length == 0 ? 1 : modes.Length;
            var sortColumns = new[] { 0, 2 };
            object firstAny = null;

            for (int m = 0; m < modeCount; m++)
            {
                object mode = modes.Length == 0 ? null : modes[m];
                for (int s = 0; s < sortColumns.Length; s++)
                {
                    object v = null;
                    if (mode != null)
                    {
                        v = Reflect.InvokeStatic(Driver, "GetHierarchyFrameDataView",
                                                 frameIndex, threadIndex, mode, sortColumns[s], true);
                    }
                    if (v == null)
                    {
                        // 兼容早期版本可能存在的 2 参数重载
                        v = Reflect.InvokeStatic(Driver, "GetHierarchyFrameDataView", frameIndex, threadIndex);
                    }
                    if (v == null) continue;

                    if (firstAny == null) firstAny = v;

                    bool valid;
                    if (!Reflect.TryGetBool(v, "valid", out valid) || valid)
                        return v;
                }
            }

            if (firstAny != null)
            {
                _lastHierarchyError = "视图取到但 valid=false（该帧没有记录到 FrameDataView 数据）";
                return firstAny;
            }

            _lastHierarchyError = "GetHierarchyFrameDataView 调用失败（ViewModes 候选: " + DescribeViewModes()
                                  + "；已试 sortColumn 0/2）";
            return null;
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
            if (view == null) return 0;

            foreach (var name in new[] { "columnCount", "ColumnCount" })
            {
                var v = Reflect.Get(view, name);
                if (v != null)
                {
                    try { return Convert.ToInt32(v); } catch { }
                }
            }

            // 2022.3 已确认：视图上没有 columnCount（也没有列名 API），只能探 ——
            // 拿根行的第一个子项逐列读文本，读到连续空列为止。
            // 写死 8 在这个版本碰巧对，但换版本就会错位，所以改成探测。
            int id = GetRootItemId(view);
            var children = GetChildren(view, id);
            if (children.Count > 0) id = children[0];

            int lastNonEmpty = -1, emptyRun = 0;
            for (int c = 0; c < 24; c++)
            {
                string cell = GetItemColumn(view, id, c);
                if (string.IsNullOrEmpty(cell))
                {
                    emptyRun++;
                    if (lastNonEmpty >= 0 && emptyRun >= 3) break;
                }
                else
                {
                    lastNonEmpty = c;
                    emptyRun = 0;
                }
            }
            return lastNonEmpty >= 0 ? lastNonEmpty + 1 : 8;
        }

        // =====================================================================
        // Profiler 面板序列（不自己逐帧采样，直接读面板已记录的数据）
        //
        // 面板的图表 / 计数器本质上都是「一帧一个值」的序列，Unity 给了批量读取接口：
        //   GetCounterValuesBatchByCategory(String category, String name, Int32 firstFrame,
        //                                   Single scale, Single[] buffer, ref Single maxValue)
        //   GetStatisticsValues(Int32 identifier, Int32 firstFrame, Single scale,
        //                       Single[] buffer, ref Single maxValue)
        // 两者最后一个参数都是 ref Single —— 走 Reflect 的自动转换会丢掉回写（CoerceArgs 会新建数组），
        // 所以这里直接拿 MethodInfo.Invoke 调用。
        // =====================================================================

        static MethodInfo _counterSeriesMethod;
        static MethodInfo _statisticsSeriesMethod;
        static string[] _statisticsProperties;

        static MethodInfo FindMethod(string name, int argCount, Type firstArgType, Type bufferType)
        {
            if (Driver == null) return null;
            var methods = Driver.GetMethods(Reflect.StaticAll);
            for (int i = 0; i < methods.Length; i++)
            {
                var m = methods[i];
                if (m.Name != name) continue;
                var ps = m.GetParameters();
                if (ps.Length != argCount) continue;
                if (firstArgType != null && ps[0].ParameterType != firstArgType) continue;
                if (bufferType != null && ps[argCount - 2].ParameterType != bufferType) continue;
                return m;
            }
            return null;
        }

        /// <summary>
        /// 读一段计数器序列（与 Profiler 面板图表同源）。category/name 用 ProfilerRecorder 那套名字，
        /// 例如 ("Memory", "GC Allocated In Frame")、("Render", "Draw Calls Count")。
        /// </summary>
        public static bool ReadCounterSeries(string category, string name, int firstFrame, float[] buffer)
        {
            if (Driver == null || buffer == null || buffer.Length == 0) return false;
            if (_counterSeriesMethod == null)
                _counterSeriesMethod = FindMethod("GetCounterValuesBatchByCategory", 6, typeof(string), typeof(float[]));
            if (_counterSeriesMethod == null) return false;

            object[] args = { category, name, firstFrame, 1f, buffer, 0f };
            try { _counterSeriesMethod.Invoke(null, args); return true; }
            catch { return false; }
        }

        /// <summary>读一段统计序列（面板图表的属性序列，如 CPU 帧耗时）。</summary>
        public static bool ReadStatisticSeries(int identifier, int firstFrame, float[] buffer)
        {
            if (Driver == null || identifier == 0 || buffer == null || buffer.Length == 0) return false;
            if (_statisticsSeriesMethod == null)
                _statisticsSeriesMethod = FindMethod("GetStatisticsValues", 5, typeof(int), typeof(float[]));
            if (_statisticsSeriesMethod == null) return false;

            object[] args = { identifier, firstFrame, 1f, buffer, 0f };
            try { _statisticsSeriesMethod.Invoke(null, args); return true; }
            catch { return false; }
        }

        /// <summary>面板可选的全部统计属性名（含 CPU 帧耗时之类的序列）。</summary>
        public static string[] AllStatisticsProperties()
        {
            if (_statisticsProperties == null)
                _statisticsProperties = Reflect.InvokeStatic(Driver, "GetAllStatisticsProperties") as string[] ?? new string[0];
            return _statisticsProperties;
        }

        /// <summary>统计属性名 → identifier；0 表示未注册。</summary>
        public static int StatisticsIdentifier(string property)
        {
            var v = Reflect.InvokeStatic(Driver, "GetStatisticsIdentifier", property);
            if (v == null) return 0;
            try { return Convert.ToInt32(v); } catch { return 0; }
        }

        /// <summary>诊断信息，用于探针窗口与报告 notes。</summary>
        public static string DescribeAvailability()
        {
            var sb = new StringBuilder();
            sb.Append("ProfilerDriver: ").Append(Driver != null ? "OK" : "缺失").Append('\n');
            sb.Append("HierarchyFrameDataView: ").Append(HierarchyView != null ? "OK" : "缺失").Append('\n');
            sb.Append("RawFrameDataView: ").Append(RawView != null ? "OK" : "缺失").Append('\n');
            sb.Append("FrameDataView: ").Append(FrameDataView != null ? "OK" : "缺失")
              .Append(" | ViewModes: ").Append(DescribeViewModes()).Append('\n');
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
            if (!string.IsNullOrEmpty(_lastHierarchyError))
                sb.Append("最近一次取视图失败: ").Append(_lastHierarchyError).Append('\n');
            return sb.ToString();
        }

        static bool Prop(Type t, string name) { return Reflect.Prop(t, name, true) != null || Reflect.Fld(t, name, true) != null; }
    }
}
