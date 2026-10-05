using System;
using System.Collections.Generic;

namespace PerfAgent.Core
{
    /// <summary>
    /// 实时帧耗时波形：定长环形缓冲 + 分位统计。
    ///
    /// 纯 C#（不碰 Unity API），所以可以在离线回归里直接测 —— 这是「实时波形」这类
    /// UI 相关代码里唯一值得也必须覆盖的部分：环缓冲写满之后**必须**只丢最旧的样本，
    /// 统计口径必须与快照里的一致（P50 / P95 / 峰值），否则界面上的曲线和事后报告会对不上。
    ///
    /// 约定：**只接受正数样本**。0 / 负数 / NaN 都代表「这一帧没有有效读数」，
    /// 不是「极快的一帧」——把它们算进统计会把分位数拉低，凭空造出好数据。
    /// </summary>
    public class FrameWaveform
    {
        readonly double[] _ms;
        int _head;      // 下一个写入位置
        int _count;     // 已有样本数（≤ Capacity）

        public FrameWaveform(int capacity)
        {
            _ms = new double[capacity < 1 ? 1 : capacity];
        }

        public int Capacity { get { return _ms.Length; } }
        public int Count { get { return _count; } }
        public bool IsFull { get { return _count >= _ms.Length; } }

        public void Clear()
        {
            _head = 0;
            _count = 0;
        }

        /// <summary>写入一个样本（毫秒）。非正数 / NaN / 无穷大一律忽略并返回 false。</summary>
        public bool Push(double ms)
        {
            if (double.IsNaN(ms) || double.IsInfinity(ms) || ms <= 0) return false;

            _ms[_head] = ms;
            _head = (_head + 1) % _ms.Length;
            if (_count < _ms.Length) _count++;
            return true;
        }

        /// <summary>第 i 个样本，0 = 最旧，Count-1 = 最新。越界返回 0。</summary>
        public double this[int i]
        {
            get
            {
                if (i < 0 || i >= _count) return 0;
                int start = IsFull ? _head : 0;     // 写满后最旧样本就在 _head
                return _ms[(start + i) % _ms.Length];
            }
        }

        public double MeanMs()
        {
            if (_count == 0) return 0;
            double sum = 0;
            for (int i = 0; i < _count; i++) sum += this[i];
            return sum / _count;
        }

        /// <summary>分位（p 取 0~1）。样本不足时退化为最大/最小值，绝不插值编数据。</summary>
        public double Percentile(double p)
        {
            if (_count == 0) return 0;
            if (p < 0) p = 0;
            if (p > 1) p = 1;

            var copy = new List<double>(_count);
            for (int i = 0; i < _count; i++) copy.Add(this[i]);
            copy.Sort();

            int idx = (int)Math.Round(p * (copy.Count - 1));
            if (idx < 0) idx = 0;
            if (idx >= copy.Count) idx = copy.Count - 1;
            return copy[idx];
        }

        public double MinMs() { return _count == 0 ? 0 : Percentile(0); }
        public double MaxMs() { return _count == 0 ? 0 : Percentile(1); }
        public double P50Ms() { return Percentile(0.5); }
        public double P95Ms() { return Percentile(0.95); }

        /// <summary>由 P50 帧耗时换算的帧率（0 = 还没样本）。</summary>
        public double Fps()
        {
            double p50 = P50Ms();
            return p50 > 0 ? 1000.0 / p50 : 0;
        }

        /// <summary>
        /// 画图用的纵轴上界：至少 33 ms（30 FPS），否则一个 60 FPS 的平稳过程会被画成满格噪点，
        /// 看不出任何波动。超过 33 ms 时按实际峰值撑满。
        /// </summary>
        public double ChartCeilingMs()
        {
            double peak = MaxMs();
            return peak > 33.0 ? peak : 33.0;
        }
    }
}
