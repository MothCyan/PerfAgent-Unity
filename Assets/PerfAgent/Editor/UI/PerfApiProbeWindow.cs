using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;
using PerfAgent.Core;
using PerfAgent.Utils;
using PerfAgent.Collectors;

namespace PerfAgent.UI
{
    /// <summary>
    /// API 探针（开发计划里的 P0 交付物）。
    ///
    /// Unity 的 Profiler / 内存 API 跨版本差异极大，本插件所有不确定的调用都走反射。
    /// 这个窗口负责把「本机到底有哪些能力」一次性列清楚：
    /// 哪一个成员存在、哪一个 ProfilerRecorder 计数器有效、哪条数据链路是通的。
    /// 当某个工具返回空数据时，先看这里，而不是靠猜。
    /// </summary>
    public class PerfApiProbeWindow : EditorWindow
    {
        Label _output;
        ScrollView _scroll;

        /// <summary>
        /// 打开 API 探针窗口。
        ///
        /// 这里刻意**不**加 [MenuItem]：菜单入口统一由 PerfAgentWindow 聚合
        /// （Tools &gt; PerfAgent &gt; API 探针，优先级 103，和其他面板命令排在一起）。
        /// 两处都写会触发 Unity 的
        /// 「Cannot add menu item ... because a menu item with the same name already exists」
        /// 警告，而且其中一个入口会被静默丢弃 —— 每次都刷一条 Console 噪音。
        /// </summary>
        public static void Open()
        {
            var window = GetWindow<PerfApiProbeWindow>("PerfAgent API 探针");
            window.minSize = new Vector2(700, 500);
            window.Show();
        }

        public void CreateGUI()
        {
            var root = rootVisualElement;
            root.style.paddingLeft = 8;
            root.style.paddingRight = 8;
            root.style.paddingTop = 6;

            var bar = new VisualElement();
            bar.style.flexDirection = FlexDirection.Row;
            bar.style.marginBottom = 6;

            var run = new Button(Run);
            run.text = "重新探测";
            bar.Add(run);

            var copy = new Button(delegate
            {
                EditorGUIUtility.systemCopyBuffer = _output.text;
                Debug.Log("[PerfAgent] 探针结果已复制到剪贴板。");
            });
            copy.text = "复制";
            copy.style.marginLeft = 4;
            bar.Add(copy);

            var save = new Button(delegate
            {
                string path = System.IO.Path.Combine(PerfSnapshotStore.RootDir, "api_probe.txt");
                PerfSnapshotStore.EnsureDirs();
                System.IO.File.WriteAllText(path, _output.text, new UTF8Encoding(false));
                Debug.Log("[PerfAgent] 探针结果已保存: " + path);
                EditorUtility.RevealInFinder(path);
            });
            save.text = "保存到文件";
            save.style.marginLeft = 4;
            bar.Add(save);

            root.Add(bar);

            _scroll = new ScrollView(ScrollViewMode.Vertical);
            _scroll.style.flexGrow = 1;
            _scroll.style.backgroundColor = new Color(0.13f, 0.14f, 0.16f);
            _scroll.style.paddingLeft = 8;
            _scroll.style.paddingTop = 6;
            root.Add(_scroll);

            _output = new Label();
            _output.style.whiteSpace = WhiteSpace.Normal;
            _scroll.Add(_output);

            Run();
        }

        void Run()
        {
            var sb = new StringBuilder();
            sb.Append("PerfAgent API 探针\n");
            sb.Append("Unity ").Append(Application.unityVersion).Append("  ·  ")
              .Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")).Append("\n");
            sb.Append("════════════════════════════════════\n\n");

            // ---- 1. Profiler 帧数据链路 ----
            sb.Append("【1】Profiler 帧数据链路\n");
            sb.Append(ProfilerApi.DescribeAvailability());
            sb.Append("Profiler 当前记录状态: ").Append(ProfilerApi.Enabled ? "开启" : "关闭").Append('\n');
            sb.Append("帧索引范围: ").Append(ProfilerApi.FirstFrameIndex).Append(" ~ ").Append(ProfilerApi.LastFrameIndex)
              .Append("（共 ").Append(ProfilerApi.FrameCount).Append(" 帧）\n");

            if (ProfilerApi.CanReadFrames && ProfilerApi.LastFrameIndex >= 0)
            {
                var view = ProfilerApi.GetHierarchyView(ProfilerApi.LastFrameIndex, 0);
                sb.Append("最近一帧的 HierarchyFrameDataView: ").Append(view == null ? "取不到" : "OK").Append('\n');
                if (view != null)
                {
                    bool valid;
                    Reflect.TryGetBool(view, "valid", out valid);
                    int count;
                    Reflect.TryGetInt(view, "sampleCount", out count);
                    sb.Append("  valid=").Append(valid).Append("  sampleCount=").Append(count)
                      .Append("  columnCount=").Append(ProfilerApi.GetColumnCount(view)).Append('\n');
                    var root = ProfilerApi.GetRootItemId(view);
                    var children = ProfilerApi.GetChildren(view, root);
                    sb.Append("  根项 id=").Append(root).Append("，子项 ").Append(children.Count).Append(" 个\n");
                    if (children.Count > 0)
                    {
                        sb.Append("  首个子项 name=\"").Append(ProfilerApi.GetItemName(view, children[0])).Append("\"");
                        for (int col = 0; col < ProfilerApi.GetColumnCount(view); col++)
                        {
                            string cell = ProfilerApi.GetItemColumn(view, children[0], col);
                            sb.Append("  [").Append(col).Append("]=").Append(cell ?? "-");
                        }
                        sb.Append('\n');

                        // 列语义是从内容反推的（该视图没有列名 API），这里把识别结果显示出来，
                        // 免得下次又靠人肉看 [0]=xxx [1]=0.0% 去猜。
                        var sampleRows = new List<string[]>();
                        for (int i = 0; i < children.Count; i++)
                        {
                            var row = new string[ProfilerApi.GetColumnCount(view)];
                            for (int col = 0; col < row.Length; col++)
                                row[col] = ProfilerApi.GetItemColumn(view, children[i], col);
                            sampleRows.Add(row);
                        }
                        var layout = ProfilerColumnLayout.Detect(sampleRows);
                        sb.Append("  识别到的列语义：").Append(layout.Describe()).Append('\n');
                    }
                }
            }
            else
            {
                sb.Append("（无可用帧：请先打开 Profiler 窗口并让编辑器运行几帧，或直接点抓帧）\n");
            }
            sb.Append('\n');

            // ---- 2. 内存 API ----
            sb.Append("【2】内存 API\n");
            sb.Append("可用性（反射探测）：\n");
            string[] memNames = {
                "GetTotalAllocatedMemoryLong", "GetTotalReservedMemoryLong", "GetMonoUsedSizeLong",
                "GetMonoHeapSizeLong", "GetTempAllocatorSize", "GetAllocatedMemoryForGraphicsDriver",
                "GetRuntimeMemorySizeLong"
            };
            for (int i = 0; i < memNames.Length; i++)
                sb.Append("  ").Append(memNames[i].PadRight(36)).Append(MemApi.Has(memNames[i]) ? "OK" : "缺失").Append('\n');

            sb.Append("实际读数：\n");
            sb.Append("  总分配      ").Append(MB(MemApi.TotalAllocated)).Append('\n');
            sb.Append("  总保留      ").Append(MB(MemApi.TotalReserved)).Append('\n');
            sb.Append("  Mono 已用   ").Append(MB(MemApi.MonoUsed)).Append('\n');
            sb.Append("  Mono 堆     ").Append(MB(MemApi.MonoHeap)).Append('\n');
            sb.Append("  TempAlloc   ").Append(MB(MemApi.TempAllocator)).Append('\n');
            sb.Append("  GPU 驱动    ").Append(MB(MemApi.GraphicsDriver)).Append('\n');
            sb.Append('\n');

            // ---- 3. ProfilerRecorder 计数器 ----
            sb.Append("【3】ProfilerRecorder 计数器有效性\n");
            sb.Append("（无效的计数器意味着对应指标拿不到数据，工具会优雅跳过而不是给出错误值）\n");
            string[] renderStats = {
                "Draw Calls Count", "Batches Count", "SetPass Calls Count", "Triangles Count", "Vertices Count"
            };
            string[] memoryStats = {
                "GC Allocated In Frame", "GC Alloc", "Total Used Memory", "Total Reserved Memory",
                "Texture Memory", "GC Reserved Memory", "GC Committed Memory"
            };
            string[] objectStats = {
                "Texture Count", "Mesh Count", "Material Count", "Object Count"
            };

            ProbeCounters(sb, "渲染", Unity.Profiling.ProfilerCategory.Render, renderStats);
            ProbeCounters(sb, "内存", Unity.Profiling.ProfilerCategory.Memory, memoryStats);
            ProbeCounters(sb, "对象", Unity.Profiling.ProfilerCategory.Render, objectStats);
            sb.Append('\n');

            // ---- 4. 其他数据源 ----
            sb.Append("【4】其他数据源\n");
            var unityStats = Reflect.FindType("UnityEditorInternal.UnityStats");
            sb.Append("  UnityEditorInternal.UnityStats       ").Append(unityStats != null ? "存在" : "缺失").Append('\n');
            if (unityStats != null)
            {
                string[] usNames = { "drawCalls", "batches", "setPassCalls", "triangles", "vertices" };
                for (int i = 0; i < usNames.Length; i++)
                {
                    var v = Reflect.GetStatic(unityStats, usNames[i]);
                    sb.Append("    ").Append(usNames[i].PadRight(16)).Append(v == null ? "缺失" : v.ToString()).Append('\n');
                }
            }
            sb.Append("  HierarchyFrameDataView               ").Append(ProfilerApi.HierarchyView != null ? "存在" : "缺失").Append('\n');
            sb.Append("  RawFrameDataView                     ").Append(ProfilerApi.RawView != null ? "存在" : "缺失").Append('\n');
            sb.Append("  FrameTimingManager                   ").Append(Reflect.FindType("UnityEngine.FrameTimingManager") != null ? "存在" : "缺失").Append('\n');
            sb.Append("  TMPro.TMP_Text                       ").Append(Reflect.FindType("TMPro.TMP_Text") != null ? "存在" : "缺失").Append('\n');
            sb.Append('\n');

            // ---- 5. Profiler 面板帧历史（可作为不自采样的数据源）----
            sb.Append("【5】Profiler 面板帧历史（不自己采样，直接读面板已记录的数据）\n");
            AppendPanelHistory(sb);
            sb.Append('\n');

            // ---- 6. 成员清单 ----
            sb.Append("【6】类型成员清单（供适配代码参考）\n\n");
            AppendType(sb, "UnityEditorInternal.ProfilerDriver", ProfilerApi.Driver);
            AppendType(sb, "UnityEditor.Profiling.HierarchyFrameDataView", ProfilerApi.HierarchyView);
            AppendType(sb, "UnityEditor.Profiling.FrameDataView", ProfilerApi.FrameDataView);
            AppendType(sb, "UnityEditor.Profiling.RawFrameDataView", ProfilerApi.RawView);
            AppendType(sb, "UnityEditorInternal.UnityStats", unityStats);

            _output.text = sb.ToString();
        }

        /// <summary>
        /// 【5】Profiler 面板帧历史。
        ///
        /// 这一节能回答「不自采样能不能拿到数据」：面板已经记录了帧历史，
        ///   帧耗时 → HierarchyFrameDataView.frameTimeMs（引擎自己测的，不用 Stopwatch 估）
        ///   各类计数器 → ProfilerDriver.GetCounterValuesBatchByCategory（与面板图表同源）
        ///   面板图表属性 → GetAllStatisticsProperties / GetStatisticsValues
        /// 名字/类别对不上时这里会直接显示「无有效样本」，不会默默给 0。
        /// </summary>
        void AppendPanelHistory(StringBuilder sb)
        {
            int first = ProfilerApi.FirstFrameIndex;
            int last = ProfilerApi.LastFrameIndex;
            int count = last >= first ? last - first + 1 : 0;

            sb.Append("  帧范围: ").Append(first).Append(" ~ ").Append(last)
              .Append("（共 ").Append(count).Append(" 帧）")
              .Append("  maxHistoryLength=").Append(ProfilerApi.MaxHistoryLength).Append('\n');

            if (!ProfilerApi.CanReadFrames || count <= 0)
            {
                sb.Append("  面板还没有可用帧（先让 Profiler 录一段）。\n");
                return;
            }

            // 抽查三帧：只需确认 frameTimeMs 能拿到
            sb.Append("  抽查帧（引擎自测的 frameTimeMs / sampleCount / maxDepth）：\n");
            int step = Math.Max(1, count / 3);
            for (int i = 0; i < 3; i++)
            {
                int f = last - i * step;
                if (f < first) break;
                var view = ProfilerApi.GetHierarchyView(f, 0);
                if (view == null)
                {
                    sb.Append("    frame ").Append(f).Append("  取不到视图");
                    if (!string.IsNullOrEmpty(ProfilerApi.LastHierarchyError))
                        sb.Append("（").Append(ProfilerApi.LastHierarchyError).Append("）");
                    sb.Append('\n');
                    continue;
                }

                var msRaw = Reflect.Get(view, "frameTimeMs");
                int samples, depth;
                Reflect.TryGetInt(view, "sampleCount", out samples);
                Reflect.TryGetInt(view, "maxDepth", out depth);
                sb.Append("    frame ").Append(f).Append("  frameTimeMs=")
                  .Append(msRaw == null ? "缺失" : Convert.ToDouble(msRaw).ToString("0.###"))
                  .Append("  sampleCount=").Append(samples)
                  .Append("  maxDepth=").Append(depth).Append('\n');
            }

            // 计数器序列（和 ProfilerRecorder 用的是同一套 category/name）
            sb.Append("  计数器序列（GetCounterValuesBatchByCategory）：\n");
            string[,] series =
            {
                { "Render", "Draw Calls Count" },
                { "Render", "SetPass Calls Count" },
                { "Render", "Triangles Count" },
                { "Memory", "GC Allocated In Frame" },
                { "Memory", "Total Used Memory" },
                { "Memory", "Texture Memory" }
            };
            for (int i = 0; i < series.GetLength(0); i++)
                ProbeSeries(sb, series[i, 0], series[i, 1], first, count);

            // 面板图表属性（CPU 帧耗时之类的序列名在这里）
            sb.Append("  面板图表属性（GetAllStatisticsProperties）：\n");
            var props = Reflect.InvokeStatic(ProfilerApi.Driver, "GetAllStatisticsProperties") as string[];
            if (props == null || props.Length == 0)
            {
                sb.Append("    取不到属性列表\n");
            }
            else
            {
                for (int i = 0; i < props.Length; i++)
                {
                    int id = 0;
                    var idRaw = Reflect.InvokeStatic(ProfilerApi.Driver, "GetStatisticsIdentifier", props[i]);
                    if (idRaw != null) { try { id = Convert.ToInt32(idRaw); } catch { } }
                    sb.Append("    [").Append(id).Append("] ").Append(props[i])
                      .Append("  ").Append(ReadStatisticsSample(props[i], first, count)).Append('\n');
                }
            }
        }

        /// <summary>读一段计数器序列，报告有效样本数与最新值；名字/类别不对时会直接说出来。</summary>
        void ProbeSeries(StringBuilder sb, string category, string name, int first, int count)
        {
            string label = (category + " / " + name).PadRight(34);
            if (ProfilerApi.Driver == null || count <= 0)
            {
                sb.Append("    ").Append(label).Append("无帧范围\n");
                return;
            }

            var buffer = new float[count];
            string error = CallWithBuffer("GetCounterValuesBatchByCategory", category, name, first, buffer);
            if (error != null)
            {
                sb.Append("    ").Append(label).Append(error).Append('\n');
                return;
            }

            int valid;
            float latest;
            Summarize(buffer, out valid, out latest);
            sb.Append("    ").Append(label);
            if (valid == 0) sb.Append("无有效样本（类别或名字不对？）");
            else sb.Append("有效 ").Append(valid).Append('/').Append(count).Append("  最新 ").Append(latest.ToString("0.##"));
            sb.Append('\n');
        }

        /// <summary>读一条统计序列的一个样本（确认属性名可用）。</summary>
        static string ReadStatisticsSample(string property, int first, int count)
        {
            if (ProfilerApi.Driver == null || count <= 0) return "";
            var idRaw = Reflect.InvokeStatic(ProfilerApi.Driver, "GetStatisticsIdentifier", property);
            if (idRaw == null) return "";
            int id;
            try { id = Convert.ToInt32(idRaw); } catch { return ""; }
            if (id == 0) return "（未注册）";

            var buffer = new float[count];
            string error = CallWithBuffer("GetStatisticsValues", id, null, first, buffer);
            if (error != null) return error;

            int valid;
            float latest;
            Summarize(buffer, out valid, out latest);
            return valid == 0 ? "无有效样本" : ("有效 " + valid + "/" + count + "  最新 " + latest.ToString("0.##"));
        }

        /// <summary>
        /// 这两种 API 的最后一个参数是 <c>ref Single maxValue</c>，
        /// 走 <see cref=“Reflect”/> 的自动转换会丢掉回写，所以这里直招 MethodInfo.Invoke。
        /// </summary>
        static string CallWithBuffer(string method, object firstArg, object secondArg, int firstFrame, float[] buffer)
        {
            if (ProfilerApi.Driver == null) return "ProfilerDriver 缺失";

            var methods = ProfilerApi.Driver.GetMethods(Reflect.StaticAll);
            MethodInfo target = null;
            for (int i = 0; i < methods.Length; i++)
            {
                var m = methods[i];
                if (m.Name != method) continue;
                var ps = m.GetParameters();
                if (ps.Length != 5 && ps.Length != 6) continue;
                if (ps[0].ParameterType != typeof(string) && ps[0].ParameterType != typeof(int)) continue;
                if (ps[ps.Length - 2].ParameterType != typeof(float[])) continue;
                target = m;
                break;
            }
            if (target == null) return (method + " 重载不存在");

            object[] args = target.GetParameters().Length == 6
                ? new object[] { firstArg, secondArg, firstFrame, 1f, buffer, 0f }
                : new object[] { firstArg, firstFrame, 1f, buffer, 0f };
            try { target.Invoke(null, args); }
            catch (Exception e) { return "调用异常 " + e.GetType().Name; }
            return null;
        }

        static void Summarize(float[] buffer, out int valid, out float latest)
        {
            valid = 0;
            latest = 0f;
            for (int i = 0; i < buffer.Length; i++)
            {
                if (buffer[i] <= 0f) continue;
                valid++;
                latest = buffer[i];
            }
        }

        /// <summary>
        /// 探测一组 ProfilerRecorder 计数器。
        /// 必须用对应的 ProfilerCategory —— 内存类计数器挂在 Memory 类别下，
        /// 用 Render 类别去建会一律返回「不可用」，从而把可用能力误报成缺失。
        /// </summary>
        static void ProbeCounters(StringBuilder sb, string label, Unity.Profiling.ProfilerCategory category, string[] stats)
        {
            sb.Append("  -- ").Append(label).Append(" --\n");
            for (int i = 0; i < stats.Length; i++)
            {
                var rec = Collectors.StatRecorder.Make(category, stats[i]);
                bool valid = rec.Valid;
                long value = 0;
                if (valid)
                {
                    try { value = rec.LastValue; } catch { }
                    try { rec.Dispose(); } catch { }
                }
                sb.Append("  ").Append(stats[i].PadRight(28)).Append(valid ? "OK" : "不可用");
                if (valid) sb.Append("   读数 ").Append(value);
                sb.Append('\n');
            }
        }

        static void AppendType(StringBuilder sb, string label, Type type)
        {
            sb.Append("── ").Append(label).Append('\n');
            if (type == null)
            {
                sb.Append("   （类型不存在）\n\n");
                return;
            }
            sb.Append(Reflect.Describe(type, false, 80));
            sb.Append('\n');
        }

        static string MB(long bytes)
        {
            return (bytes / 1048576.0).ToString("0.00") + " MB  (" + bytes + " B)";
        }
    }
}
