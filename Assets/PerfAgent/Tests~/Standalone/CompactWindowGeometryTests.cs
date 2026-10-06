using System;
using System.Collections.Generic;
using PerfAgent.Utils;

namespace PerfAgent.RuleRegression
{
    /// <summary>
    /// 「跟随采集时缩成小窗口」的几何与持久化。
    ///
    /// 要挡的事故（实测过一次）：进 Play 触发域重载 → 内存里的「原尺寸」被清空 →
    /// 重新缩/放时把「当前已经是小窗口的尺寸」当成了原尺寸存下来 →
    /// 采集结束把面板「恢复」成一条横线，而且再也拉不回来。
    /// </summary>
    static class CompactWindowGeometryTests
    {
        public static void Register(List<Action> tests)
        {
            tests.Add(CompactSizeItselfIsNeverSavedAsRestoreTarget);
            tests.Add(OnlyRealPanelRectsAreUsableAsRestoreTarget);
            tests.Add(CompactRectPersistsStrictly);
        }

        static void CompactSizeItselfIsNeverSavedAsRestoreTarget()
        {
            True(CompactWindowGeometry.LooksLikeCompact(CompactWindowGeometry.Width, CompactWindowGeometry.Height),
                "小窗口自己的尺寸必须被识别为「像小窗口」");
            True(!CompactWindowGeometry.IsUsablePanelRect(CompactWindowGeometry.Width, CompactWindowGeometry.Height),
                "小窗口尺寸绝不能当「原尺寸」存下来（否则恢复出来还是一条横线）");
            True(!CompactWindowGeometry.LooksLikeCompact(900f, 600f), "正常面板尺寸不能被当成小窗口");
            True(!CompactWindowGeometry.LooksLikeCompact(1460f, 85f),
                "「宽而扁」不算小窗口 —— 它既不能当原尺寸，也不该被当成小窗口去用");
        }

        static void OnlyRealPanelRectsAreUsableAsRestoreTarget()
        {
            True(CompactWindowGeometry.IsUsablePanelRect(900f, 600f), "标准面板尺寸可用");
            True(CompactWindowGeometry.IsUsablePanelRect(CompactWindowGeometry.PanelWidth, CompactWindowGeometry.PanelHeight),
                "下限尺寸本身可用（含 1px 的 DPI 取整容差）");
            True(!CompactWindowGeometry.IsUsablePanelRect(400f, 300f), "宽度明显不够不算可用（两栏会挤爆）");
            True(!CompactWindowGeometry.IsUsablePanelRect(900f, 200f), "高度明显不够不算可用（卡片会挤在一起）");
            True(!CompactWindowGeometry.IsUsablePanelRect(1460f, 85f), "宽而扁不可用（实测事故尺寸）");
            True(!CompactWindowGeometry.IsUsablePanelRect(0f, 0f), "0x0 不可用");
        }

        static void CompactRectPersistsStrictly()
        {
            float x, y, w, h;
            True(CompactWindowGeometry.TryParse(CompactWindowGeometry.Format(120f, 40f, 900f, 600f), out x, out y, out w, out h),
                "自己写出来的串必须能解析回来");
            Equal(120.0, (double)x, "x");
            Equal(600.0, (double)h, "h");

            True(!CompactWindowGeometry.TryParse("", out x, out y, out w, out h), "空串不能解析");
            True(!CompactWindowGeometry.TryParse("1;2;3", out x, out y, out w, out h), "字段数不对不能解析");
            True(!CompactWindowGeometry.TryParse("0;0;0;0", out x, out y, out w, out h), "0x0 不能当尺寸");
            True(!CompactWindowGeometry.TryParse("0;0;NaN;600", out x, out y, out w, out h), "NaN 不能当尺寸");
            True(!CompactWindowGeometry.TryParse("0;0;900;Infinity", out x, out y, out w, out h), "Infinity 不能当尺寸");
        }

        static void True(bool value, string message)
        {
            if (!value) throw new Exception(message);
        }

        static void Equal<T>(T expected, T actual, string label)
        {
            if (!EqualityComparer<T>.Default.Equals(expected, actual))
                throw new Exception(label + ": expected " + expected + ", got " + actual);
        }
    }
}
