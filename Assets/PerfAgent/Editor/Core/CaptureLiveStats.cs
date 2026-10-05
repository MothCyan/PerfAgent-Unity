using System;
using UnityEngine;

namespace PerfAgent.Core
{
    /// <summary>
    /// 跟随采集期间的**实时**读数：帧率 / 帧耗时曲线。
    ///
    /// <para><b>为什么用「面板帧号 + 墙钟」这个口径</b></para>
    /// 用户要的是「按下跟随采集后能看到帧率在抖」。能逐帧拿到帧耗时的两条路在这里都不通：
    ///   · 面板统计序列（`GetAllStatisticsProperties` → identifier）在 2022.3 全为 -1，不可用；
    ///   · 帧耗时计数器（`GetCounterValuesBatchByCategory`）**名字不存在时也不会报错**，
    ///     只会返回一列 0 —— 拿它当数据源等于把「猜中的名字」当成事实。
    /// 所以实时曲线用**面板帧号增量 / 墙钟增量**算：面板每记录一个播放器帧，帧号就 +1，
    /// 于是 Δ帧 / Δ秒 就是这段时间的真实帧率。它**包含编辑器自身的停顿**，
    /// 所以界面上必须如实写出这个口径（`Source`），并且：
    ///   · 只在采集期间采样，采集结束就冻结；
    ///   · 单次采样超过 5 秒（编辑器被拖停、断点、资源导入）时**丢弃**这一段，
    ///     不把编辑器停顿算成一个「巨慢帧」——这是这个项目反复踩过的假数据来源。
    ///
    /// 逐帧精度的实时曲线是后续计划（要用探针先实测出正确的计数器名再接，见开发计划 P7-6）。
    /// </summary>
    public class CaptureLiveStats
    {
        /// <summary>曲线点数。刷新率 4 Hz → 覆盖最近约 30 秒。</summary>
        public const int Capacity = 120;

        /// <summary>界面上的刷新间隔（秒）。刷太快会让界面重绘本身变成被测对象的开销。</summary>
        public const double SampleIntervalSeconds = 0.25;

        /// <summary>单次采样间隔超过这么多秒就丢弃：那是编辑器停顿，不是帧耗时。</summary>
        const double MaxGapSeconds = 5.0;

        /// <summary>单帧耗时闸门（毫秒）：超过这个值一定不是「游戏在跑」，而是编辑器卡住/被挂起。</summary>
        const double MaxPlausibleFrameMs = 5000.0;

        public readonly FrameWaveform waveform = new FrameWaveform(Capacity);

        /// <summary>本段数据是哪个口径来的（界面上照实显示，不 shorthand）。</summary>
        public string Source = "尚未开始";

        public int StartFrame = -1;
        public int LastFrameIndex = -1;

        /// <summary>自采集开始，面板一共记录了多少帧。</summary>
        public int PanelFrames { get { return (StartFrame < 0 || LastFrameIndex < StartFrame) ? 0 : LastFrameIndex - StartFrame; } }

        /// <summary>最近一次采样算出的单帧耗时（毫秒）；0 = 这一次没采到。</summary>
        public double LastFrameMs { get; private set; }

        int _lastFrame = -1;
        double _lastTime;

        /// <summary>采集开始时调用。<paramref name="startFrame"/> = 采集起点（之前的帧不算）。</summary>
        public void Begin(int startFrame, double now)
        {
            waveform.Clear();
            StartFrame = startFrame;
            LastFrameIndex = startFrame;
            LastFrameMs = 0;
            _lastFrame = startFrame;
            _lastTime = now;
            Source = "面板帧号 / 墙钟（Δ面板帧 / Δ秒，含编辑器开销）";
        }

        public void Reset()
        {
            waveform.Clear();
            StartFrame = -1;
            LastFrameIndex = -1;
            LastFrameMs = 0;
            _lastFrame = -1;
            _lastTime = 0;
            Source = "尚未开始";
        }

        /// <summary>
        /// 采样一次；返回是否产生了新样本。由界面按 <see cref="SampleIntervalSeconds"/> 调用。
        /// 内部全部 try/catch —— 实时面板绝不能因为读数异常而把整个窗口搞坏。
        /// </summary>
        public bool Sample(int lastFrameIndex, double now)
        {
            try
            {
                if (StartFrame < 0) return false;

                LastFrameIndex = lastFrameIndex;

                double dt = now - _lastTime;
                int frames = lastFrameIndex - _lastFrame;
                _lastFrame = lastFrameIndex;
                _lastTime = now;

                if (frames <= 0 || dt <= 0) return false;
                if (dt > MaxGapSeconds) return false;        // 编辑器停顿：丢掉这一段

                double ms = dt * 1000.0 / frames;
                if (ms <= 0 || ms > MaxPlausibleFrameMs) return false;

                LastFrameMs = ms;
                return waveform.Push(ms);
            }
            catch
            {
                return false;
            }
        }

        /// <summary>界面上那行摘要文字（含口径），没有样本时返回空串。</summary>
        public string Summary(bool capturing)
        {
            if (waveform.Count == 0) return "";

            var w = waveform;
            return (capturing ? "● 采集监视中" : "■ 采集已结束")
                 + "　" + w.Fps().ToString("0.#", System.Globalization.CultureInfo.InvariantCulture) + " FPS"
                 + "　帧耗时 P50 " + w.P50Ms().ToString("0.##", System.Globalization.CultureInfo.InvariantCulture)
                 + " / P95 " + w.P95Ms().ToString("0.##", System.Globalization.CultureInfo.InvariantCulture)
                 + " / 峰值 " + w.MaxMs().ToString("0.##", System.Globalization.CultureInfo.InvariantCulture) + " ms"
                 + "　已记录 " + PanelFrames + " 帧"
                 + "　口径：" + Source;
        }
    }
}
