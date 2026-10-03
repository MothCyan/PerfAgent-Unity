using System;
using System.Collections.Generic;
using System.Text;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;
using PerfAgent.Core;
using PerfAgent.Utils;

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

        [MenuItem("Tools/PerfAgent/API 探针", false, 200)]
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

            // ---- 5. 成员清单 ----
            sb.Append("【5】类型成员清单（供适配代码参考）\n\n");
            AppendType(sb, "UnityEditorInternal.ProfilerDriver", ProfilerApi.Driver);
            AppendType(sb, "UnityEditor.Profiling.HierarchyFrameDataView", ProfilerApi.HierarchyView);
            AppendType(sb, "UnityEditor.Profiling.FrameDataView", ProfilerApi.FrameDataView);
            AppendType(sb, "UnityEditor.Profiling.RawFrameDataView", ProfilerApi.RawView);
            AppendType(sb, "UnityEditorInternal.UnityStats", unityStats);

            _output.text = sb.ToString();
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
