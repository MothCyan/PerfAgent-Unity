using System;
using NUnit.Framework;
using PerfAgent.Core;

namespace PerfAgent.Tests
{
    /// <summary>
    /// 数据保真度回归。
    ///
    /// 背景：Unity Profiler 内部有一部分数据第三方拿不到，所以「可靠性」不能靠拿到全部数据，
    /// 只能靠**分层 + 明确降级**：能用与 Profiler 窗口同源的公开口径就用它，
    /// 用不了时必须让上层拿到 NaN 去显式降级，而不是拿一个弱口径的数字冒充。
    /// </summary>
    public class PerfDataFidelityTests
    {
        [Test]
        public void RecorderGcAlloc_IsPreferred_AndDiffMethodStaysAsCrossCheck()
        {
            var snapshot = new PerfSnapshot();
            for (int i = 0; i < 10; i++)
            {
                snapshot.frames.Add(new FrameStat
                {
                    frame = i,
                    deltaMs = 8,
                    managedAllocBytes = 1000,   // GC.GetTotalMemory 差值：弱口径
                    allocInFrameBytes = 4000    // ProfilerRecorder：与 Profiler 窗口 GC Alloc 同源
                });
            }

            Assert.IsTrue(snapshot.HasRecorderGcAlloc(), "应能识别出 ProfilerRecorder 口径可用");
            Assert.AreEqual(4000.0, snapshot.AvgRecorderAllocPerFrame(), 1e-6, "主口径应取 ProfilerRecorder");
            Assert.AreEqual(1000.0, snapshot.AvgManagedAllocBytesPerFrame(), 1e-6, "差值口径应保留用于交叉校验");
        }

        [Test]
        public void MissingRecorderGcAlloc_DegradesExplicitlyInsteadOfFakingANumber()
        {
            var snapshot = new PerfSnapshot();
            for (int i = 0; i < 10; i++)
                snapshot.frames.Add(new FrameStat { frame = i, deltaMs = 8, managedAllocBytes = 1000 });

            Assert.IsFalse(snapshot.HasRecorderGcAlloc(), "计数器拿不到时必须如实报告不可用");
            Assert.IsTrue(double.IsNaN(snapshot.AvgRecorderAllocPerFrame()),
                "拿不到主口径必须返回 NaN，让上层显式降级，而不是悄悄用一个偏小的数字");
        }

        [Test]
        public void GcEvents_AreNegativeDeltas_NotZeroAllocations()
        {
            var snapshot = new PerfSnapshot();
            snapshot.frames.Add(new FrameStat { frame = 0, managedAllocBytes = 512 });
            snapshot.frames.Add(new FrameStat { frame = 1, managedAllocBytes = -4096 }); // 差值变负 = 发生了 GC
            snapshot.frames.Add(new FrameStat { frame = 2, managedAllocBytes = 512 });

            Assert.AreEqual(1, snapshot.GcEventCount(), "负差值应被识别为 GC 事件");
            Assert.AreEqual(512.0, snapshot.AvgManagedAllocBytesPerFrame(), 1e-6, "GC 事件帧不应污染分配均值");
        }

        [Test]
        public void MissingData_IsRecordedInNotes_NotSilentlyDropped()
        {
            var snapshot = new PerfSnapshot();
            snapshot.AddNote("未能从 Profiler 层级视图解析出 marker（该版本 API 形态可能不同，请运行 API 探针）。");

            Assert.AreEqual(1, snapshot.notes.Count);
            StringAssert.Contains("API 探针", snapshot.notes[0], "拿不到数据必须留下可操作的原因说明");
        }
    }
}
