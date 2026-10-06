using System;
using System.Globalization;

namespace PerfAgent.Utils
{
    /// <summary>
    /// 「跟随采集时把面板缩成小窗口」用到的几何与持久化格式（纯逻辑，离线可测）。
    ///
    /// <para><b>为什么会有这个文件（一段完整的来回）</b></para>
    /// 2026-10-07：先有「采集时收成细条」，它踩了三个坑（原尺寸丢在域重载里 → 面板卡成一条拉不回来的横线；
    /// 只给外层设 overflow → 文字压在按钮上；停靠窗口写 position 无效）。用户当时要求**整套删掉**，
    /// 随后又要求「跟随采集要变成小窗口」——于是按原意重做，但把坑堵上：
    ///   1. 原尺寸存 <c>SessionState</c>（进 Play 的域重载会清空静态字段，之前就是这么丢的）；
    ///   2. 「看起来就是小窗口」的尺寸**不能**当原尺寸存下来（否则恢复出来还是一条横线）；
    ///   3. 只有**宽高都**达到面板下限的矩形才算可用（「宽而扁」1460x85 不算）；
    ///   4. 恢复路径永远有兜底：拿不到原尺寸就用面板默认尺寸，绝不退化成 0x0。
    /// </summary>
    internal static class CompactWindowGeometry
    {
        /// <summary>
        /// 小窗口的尺寸：一行要放得下「帧率 + 帧耗时 P50/P95/峰值 + 已记录 N 帧」加两个按钮。
        ///
        /// 高度是 **72 而不是 48**（实测反馈：「那个小面板打开之后还是没有高度」）：
        /// <c>EditorWindow.position</c> 的高度**包含标题栏**（约 22px），48 减去标题栏与内边距后
        /// 正文只剩二十几像素，一行内容被裁得几乎看不见 —— 看着就是「窗口没高度」。
        /// </summary>
        public const float Width = 660f;
        public const float Height = 72f;

        /// <summary>面板的最小尺寸（与 <c>PerfAgentWindow.PanelMinSize</c> 保持一致：两栏能并排的底线）。</summary>
        public const float PanelWidth = 460f;
        public const float PanelHeight = 300f;

        /// <summary>这个尺寸看起来就是「小窗口」吗（即：不能拿它当原尺寸存下来）。</summary>
        public static bool LooksLikeCompact(float width, float height)
        {
            return width > 1f && height > 1f
                && width <= Width + 80f && height <= Height + 30f;
        }

        /// <summary>这个矩形够不够当一个「正常面板」用（宽高都要达到下限）。</summary>
        public static bool IsUsablePanelRect(float width, float height)
        {
            return width >= PanelWidth - 1f && height >= PanelHeight - 1f;
        }

        /// <summary>把窗口矩形存成 SessionState 里的一个字符串（跨域重载有效）。</summary>
        public static string Format(float x, float y, float width, float height)
        {
            return x.ToString("0.###", CultureInfo.InvariantCulture) + ";"
                 + y.ToString("0.###", CultureInfo.InvariantCulture) + ";"
                 + width.ToString("0.###", CultureInfo.InvariantCulture) + ";"
                 + height.ToString("0.###", CultureInfo.InvariantCulture);
        }

        /// <summary>
        /// 解析 <see cref="Format"/> 写出的字符串。校验故意严格：字段数不对、NaN / Infinity、
        /// 宽或高 ≤ 1 一律 false —— 调用方据此退回默认尺寸，而不是把 0x0 当成「原来的尺寸」去恢复。
        /// </summary>
        public static bool TryParse(string text, out float x, out float y, out float width, out float height)
        {
            x = y = width = height = 0f;
            if (string.IsNullOrEmpty(text)) return false;

            var parts = text.Split(';');
            if (parts.Length != 4) return false;

            if (!TryFloat(parts[0], out x) || !TryFloat(parts[1], out y)
                || !TryFloat(parts[2], out width) || !TryFloat(parts[3], out height)) return false;

            return width > 1f && height > 1f;
        }

        static bool TryFloat(string s, out float v)
        {
            return float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out v)
                   && !float.IsNaN(v) && !float.IsInfinity(v);
        }
    }
}
