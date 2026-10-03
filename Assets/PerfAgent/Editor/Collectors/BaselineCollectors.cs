using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Unity.Profiling;
using UnityEditor;
using UnityEngine;
using UnityEngine.Profiling;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;
using PerfAgent.Core;
using PerfAgent.Utils;

namespace PerfAgent.Collectors
{
    internal static class StatRecorder
    {
        /// <summary>
        /// GC 分配计数器的候选名（跨版本有差异，第一个有效者胜）。
        /// 2022 叫 "GC Allocated In Frame"；它也是 Profiler 面板 GC Alloc 列的来源。
        /// </summary>
        public static readonly string[] GcAllocCounters = { "GC Allocated In Frame", "GC Alloc" };

        public static ProfilerRecorder Make(ProfilerCategory category, string stat, int capacity = 1)
        {
            try { return ProfilerRecorder.StartNew(category, stat, capacity); }
            catch { return default(ProfilerRecorder); }
        }

        /// <summary>
        /// 按候选名依次尝试，返回第一个有效的 recorder。
        /// 计数器名字跨 Unity 版本有变更（例如 GC 分配在 2022 叫 "GC Allocated In Frame"），
        /// 写死一个名字会让整个指标静默失效，所以这里逐一探测。
        /// </summary>
        public static ProfilerRecorder MakeFirstValid(ProfilerCategory category, string[] candidates)
        {
            if (candidates == null) return default(ProfilerRecorder);

            for (int i = 0; i < candidates.Length; i++)
            {
                var r = Make(category, candidates[i]);
                if (r.Valid) return r;
                try { r.Dispose(); } catch { }
            }
            return default(ProfilerRecorder);
        }

        public static long Last(ProfilerRecorder r)
        {
            if (!r.Valid) return 0L;
            try { return r.LastValue; } catch { return 0L; }
        }

        public static bool Has(ProfilerRecorder r) { return r.Valid; }

        public static void Dispose(ref ProfilerRecorder r)
        {
            try { if (r.Valid) r.Dispose(); } catch { }
            r = default(ProfilerRecorder);
        }

        /// <summary>
        /// 「点亮」一个计数器：有些计数器（典型是 Memory/GC Allocated In Frame）只在有人订阅时才逐帧记录，
        /// 没有订阅者时面板的序列根本是空的（实测 300 帧里只有 1 帧有值）。
        /// 所以读面板序列前要先建一个 recorder 挂着，读完再 <see cref="Dispose"/>。
        /// 注意：只是挂着订阅，不读它的值 —— 不构成逐帧采样。
        /// </summary>
        public static ProfilerRecorder Register(ProfilerCategory category, string[] candidates)
        {
            return MakeFirstValid(category, candidates);
        }
    }

    // =========================================================================
    // 运行环境
    // =========================================================================
    public class EnvironmentCollector : IPerfCollector
    {
        public string Name { get { return "运行环境"; } }
        public string ToolName { get { return "env_info"; } }
        public string Description { get { return "目标平台、图形设备、质量设置、渲染管线、物理配置等基准环境信息。"; } }

        public void Collect(CollectorContext ctx)
        {
            var s = ctx.snapshot;
            if (s == null) return;

            s.capturedUtc = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture);
            s.unityVersion = Application.unityVersion;
            s.platform = Application.platform.ToString();
            try { s.buildTarget = EditorUserBuildSettings.activeBuildTarget.ToString(); } catch { }

            var scene = SceneManager.GetActiveScene();
            s.scenePath = string.IsNullOrEmpty(scene.path) ? scene.name : scene.path;

            s.deviceModel = SystemInfo.deviceModel;
            s.graphicsDevice = SystemInfo.graphicsDeviceName;
            s.processor = SystemInfo.processorType;
            s.processorCount = SystemInfo.processorCount;
            s.systemMemoryMB = SystemInfo.systemMemorySize;
            s.screenWidth = Screen.width;
            s.screenHeight = Screen.height;

            s.targetFrameRate = Application.targetFrameRate;
            s.vSyncCount = QualitySettings.vSyncCount;
            s.qualityLevel = QualitySettings.GetQualityLevel();
            var names = QualitySettings.names;
            s.qualityName = (names != null && s.qualityLevel >= 0 && s.qualityLevel < names.Length) ? names[s.qualityLevel] : "";
            s.colorSpace = QualitySettings.activeColorSpace.ToString();

            try
            {
                var rp = GraphicsSettings.currentRenderPipeline;
                s.renderPipeline = rp == null ? "Built-in" : rp.GetType().Name;
            }
            catch { s.renderPipeline = "未知"; }

            var inputHandling = Reflect.GetStatic(typeof(PlayerSettings), "activeInputHandling");
            s.activeInputHandling = inputHandling == null ? "" : inputHandling.ToString();

            // 物理
            s.fixedDeltaTime = Time.fixedDeltaTime;
            try { s.physicsSimulationMode = Physics.simulationMode.ToString(); }
            catch { s.physicsSimulationMode = ""; }
            var autoSync = Reflect.GetStatic(typeof(Physics), "autoSyncTransforms");
            s.physicsAutoSyncTransforms = autoSync is bool && (bool)autoSync;
            s.physicsSolverIterations = Physics.defaultSolverIterations;
            s.physicsSolverVelocityIterations = Physics.defaultSolverVelocityIterations;

            // 作为指标暴露一部分（便于 diff）
            s.SetMetric("目标帧率", "fps", s.targetFrameRate, "Application.targetFrameRate");
            s.SetMetric("VSync", "级", s.vSyncCount, "QualitySettings.vSyncCount");
            s.SetMetric("固定步长", "s", s.fixedDeltaTime, "Time.fixedDeltaTime");
            s.SetMetric("物理解算迭代", "次", s.physicsSolverIterations, "Physics.defaultSolverIterations");
        }
    }

    // =========================================================================
    // 内存
    // =========================================================================
    public class MemoryCollector : IPerfCollector
    {
        const double MB = 1024.0 * 1024.0;

        public string Name { get { return "内存"; } }
        public string ToolName { get { return "memory"; } }
        public string Description { get { return "总分配/保留内存、Mono 堆、Temp Allocator、GPU 驱动内存、各类对象计数（纹理/网格/材质）。"; } }

        public void Collect(CollectorContext ctx)
        {
            var s = ctx.snapshot;
            if (s == null) return;

            s.SetMetric("总分配内存", "B", MemApi.TotalAllocated, "Profiler.GetTotalAllocatedMemoryLong");
            s.SetMetric("总保留内存", "B", MemApi.TotalReserved, "Profiler.GetTotalReservedMemoryLong");
            s.SetMetric("Mono 已用", "B", MemApi.MonoUsed, "Profiler.GetMonoUsedSizeLong");
            s.SetMetric("Mono 堆", "B", MemApi.MonoHeap, "Profiler.GetMonoHeapSizeLong");
            s.SetMetric("TempAllocator", "B", MemApi.TempAllocator, "Profiler.GetTempAllocatorSize");
            s.SetMetric("GPU 驱动内存", "B", MemApi.GraphicsDriver, "Profiler.GetAllocatedMemoryForGraphicsDriver");

            // 对象计数（stat name 不存在时 recorder.Valid == false，自动跳过，不会产生脏数据）
            AddCount(s, ProfilerCategory.Render, "Texture Count", "纹理数量");
            AddCount(s, ProfilerCategory.Render, "Mesh Count", "网格数量");
            AddCount(s, ProfilerCategory.Render, "Material Count", "材质数量");
            AddCount(s, ProfilerCategory.Memory, "Texture Memory", "纹理内存");
            AddCount(s, ProfilerCategory.Memory, "Object Count", "对象总数");

            s.SetMetric("托管堆占用", "B", GC.GetTotalMemory(false), "GC.GetTotalMemory");
            s.SetMetric("托管堆上限", "B", (double)GC.MaxGeneration, "GC.MaxGeneration (GC 代数上限)");
        }

        static void AddCount(PerfSnapshot s, ProfilerCategory cat, string stat, string label)
        {
            var r = StatRecorder.Make(cat, stat);
            try
            {
                if (r.Valid) s.SetMetric(label, stat.IndexOf("Memory", StringComparison.Ordinal) >= 0 ? "B" : "个", r.LastValue, "ProfilerRecorder:" + stat);
            }
            finally { if (r.Valid) r.Dispose(); }
        }
    }

    // =========================================================================
    // 渲染统计
    // =========================================================================
    public class RenderStatsCollector : IPerfCollector
    {
        public string Name { get { return "渲染统计"; } }
        public string ToolName { get { return "render_stats"; } }
        public string Description { get { return "Draw Call / Batches / SetPass Call / 三角面 / 顶点数，用于判断是否为提交批次瓶颈。"; } }

        public void Collect(CollectorContext ctx)
        {
            var s = ctx.snapshot;
            if (s == null) return;

            bool got = false;
            got |= Add(s, "Draw Calls", "次", ProfilerCategory.Render, "Draw Calls Count");
            got |= Add(s, "Batches", "次", ProfilerCategory.Render, "Batches Count");
            got |= Add(s, "SetPass Calls", "次", ProfilerCategory.Render, "SetPass Calls Count");
            got |= Add(s, "Triangles", "个", ProfilerCategory.Render, "Triangles Count");
            got |= Add(s, "Vertices", "个", ProfilerCategory.Render, "Vertices Count");

            if (!got)
            {
                // 回退：Game View 统计栏用的 UnityStats
                if (!AddFromUnityStats(s))
                    s.AddNote("渲染统计不可用：ProfilerRecorder 的 Render 计数器与 UnityStats 均未取到数据。");
            }
        }

        static bool Add(PerfSnapshot s, string label, string unit, ProfilerCategory cat, string stat)
        {
            var r = StatRecorder.Make(cat, stat);
            try
            {
                if (!r.Valid) return false;
                s.SetMetric(label, unit, r.LastValue, "ProfilerRecorder:" + stat);
                return true;
            }
            finally { if (r.Valid) r.Dispose(); }
        }

        /// <summary>UnityEditorInternal.UnityStats 是 Game View 统计栏的数据源，走反射以免版本差异。</summary>
        static bool AddFromUnityStats(PerfSnapshot s)
        {
            var t = Reflect.FindType("UnityEditorInternal.UnityStats");
            if (t == null) return false;

            bool any = false;
            any |= TryStat(s, t, "drawCalls", "Draw Calls", "次");
            any |= TryStat(s, t, "batches", "Batches", "次");
            any |= TryStat(s, t, "setPassCalls", "SetPass Calls", "次");
            any |= TryStat(s, t, "triangles", "Triangles", "个");
            any |= TryStat(s, t, "vertices", "Vertices", "个");
            return any;
        }

        static bool TryStat(PerfSnapshot s, Type t, string member, string label, string unit)
        {
            var v = Reflect.GetStatic(t, member);
            if (v == null) return false;
            double d;
            try { d = Convert.ToDouble(v, CultureInfo.InvariantCulture); } catch { return false; }
            s.SetMetric(label, unit, d, "UnityStats:" + member);
            return true;
        }
    }

    // =========================================================================
    // 帧耗时（FrameTimingManager）
    // =========================================================================
    public class FrameTimingCollector : IPerfCollector
    {
        public string Name { get { return "帧耗时分解"; } }
        public string ToolName { get { return "frame_timing"; } }
        public string Description { get { return "CPU/GPU 帧耗时分解（主线程、渲染线程、Present 等待）。需在 Player Settings 中开启 Frame Timing Stats。"; } }

        public void Collect(CollectorContext ctx)
        {
            var s = ctx.snapshot;
            if (s == null) return;
            try
            {
                var timings = new FrameTiming[1];
                uint count = FrameTimingManager.GetLatestTimings(1, timings);
                if (count == 0)
                {
                    s.AddNote("FrameTimingManager 无数据：请确认 Player Settings 已开启 Frame Timing Stats。");
                    return;
                }

                object boxed = timings[0];
                AddTiming(s, boxed, "cpuFrameTime", "CPU 帧耗时", "ms");
                AddTiming(s, boxed, "cpuMainThreadFrameTime", "CPU 主线程耗时", "ms");
                AddTiming(s, boxed, "cpuRenderThreadFrameTime", "CPU 渲染线程耗时", "ms");
                AddTiming(s, boxed, "cpuMainThreadPresentWaitTime", "Present 等待耗时", "ms");
                AddTiming(s, boxed, "gpuFrameTime", "GPU 帧耗时", "ms");
            }
            catch (Exception e)
            {
                s.AddNote("FrameTimingManager 采集失败: " + e.Message);
            }
        }

        static void AddTiming(PerfSnapshot s, object timing, string field, string label, string unit)
        {
            double v;
            if (Reflect.TryGetDouble(timing, field, out v) && v > 0)
                s.SetMetric(label, unit, v, "FrameTiming." + field);
        }
    }
}
