using System;
using System.Globalization;

namespace PerfAgent.Utils
{
    /// <summary>
    /// 「采集时收成一条细条」用到的窗口几何与持久化格式。
    ///
    /// 抽成纯逻辑（不引用 UnityEngine / UnityEditor）是为了能离线回归。这里挡的是一个会把面板**弄废**的
    /// 事故：进 Play 必然触发域重载，字段全被清空，而窗口尺寸是持久的 ——
    /// 重载后再收起时，当前尺寸可能**已经是细条**。如果直接把它当成「原来的尺寸」存下来，
    /// 采集结束后就会「恢复」成一个细条面板：内容全在，但窗口小得看不见、也拖不大。
    /// 所以判定「看起来像不像细条」这件事必须能被测。
    /// </summary>
    public static class StripGeometry
    {
        /// <summary>
        /// 细条的目标尺寸。宽高要小到不挡 Game 视图，又要能放下帧率 + 帧耗时 P50/P95/峰值 + 已记录帧数
        ///（实测反馈：520x32 时这行字会被截到「口径」就没了，所以放宽 —— 宽度是按真实文案量出来的）。
        /// </summary>
        public const float Width = 720f;
        public const float Height = 36f;

        /// <summary>
        /// 面板恢复正常（非细条）时的可用尺寸下限。
        ///
        /// 低于它，两栏会被挤到互相重叠 —— 实测就是「整个面板只剩一条监视行」（用户截图）。
        /// 而 Unity 的 <c>minSize</c> 只能限制**手动拖拽**，管不住「从布局里恢复成小窗口」与
        /// 「细条来回后 minSize 被域重载清掉」这两种情况，所以还需要主动擑一下。
        /// </summary>
        public const float PanelWidth = 900f;
        public const float PanelHeight = 600f;

        /// <summary>这个窗口尺寸是不是小到不能当「面板尺寸」用（用于收起前的尺寸保存判定）。</summary>
        public static bool NeedsGrow(float width, float height)
        {
            return width < PanelWidth - 1f || height < PanelHeight - 1f;
        }

        /// <summary>
        /// 这个尺寸看起来就是「细条」吗（即：不能拿它当「收起前的尺寸」存下来）。
        ///
        /// 留 40/20 px 的余量 —— 不同 DPI 缩放与窗口边框会让实际尺寸与请求值差几个像素，
        /// 判定卡在整数边界上会漏判，而漏判的后果就是上面说的那种「恢复成一个废窗口」。
        /// </summary>
        public static bool LooksLikeStrip(float width, float height)
        {
            return width > 1f && height > 1f && width <= Width + 40f && height <= Height + 20f;
        }

        /// <summary>把窗口尺寸存成 SessionState 里的一个字符串（跨域重载有效）。</summary>
        public static string Format(float x, float y, float width, float height)
        {
            return x.ToString("0.###", CultureInfo.InvariantCulture) + ";"
                 + y.ToString("0.###", CultureInfo.InvariantCulture) + ";"
                 + width.ToString("0.###", CultureInfo.InvariantCulture) + ";"
                 + height.ToString("0.###", CultureInfo.InvariantCulture);
        }

        /// <summary>
        /// 解析 <see cref="Format"/> 写出的字符串。
        ///
        /// 校验故意严格：格式不对、NaN / Infinity、宽或高 ≤ 1 一律返回 false ——
        /// 调用方据此退回默认尺寸，而不是把一个 0x0 当成「原来的尺寸」去恢复。
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

        static bool TryFloat(string text, out float value)
        {
            value = 0f;
            if (!float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value)) return false;
            // NaN / Infinity 会让窗口尺寸彻底失效，直接当解析失败
            return !float.IsNaN(value) && !float.IsInfinity(value);
        }
    }
}
