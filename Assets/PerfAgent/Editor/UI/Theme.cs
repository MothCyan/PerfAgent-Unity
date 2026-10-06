using System;
using System.Text.RegularExpressions;
using UnityEngine;
using UnityEngine.UIElements;

namespace PerfAgent.UI
{
    /// <summary>
    /// 面板的视觉规范：颜色 / 间距 / 字号 + 常用控件构造。
    ///
    /// <para><b>为什么集中在一个文件</b></para>
    /// 窗口里有一百多处样式赋值，散着写必然出现「同一个按钮三种灰、每块间距都不一样」。
    /// 这里做唯一的来源：改风格只改这一个文件。
    ///
    /// <para><b>为什么不用 USS / 伪类 / transition</b></para>
    /// 只用 UI Toolkit 的**基础**样式 API（背景、圆角、边框、内外边距、字号、颜色）。
    /// `opacity` / `transition` / `:hover` 伪类在不同版本上行为不一致，而这是常驻面板 ——
    /// 样式 API 一旦在某版本缺失或行为不同，整个窗口会白屏或渲染异常，不值得冒这个险。
    /// 悬停效果用 MouseEnter / MouseLeave 回调实现（见 <see cref="Hover"/>）。
    /// </summary>
    public static class Theme
    {
        // =====================================================================
        // 色板（参照 Unity 编辑器暗色皮肤）
        // =====================================================================
        public static readonly Color WindowBg = new Color(0.128f, 0.133f, 0.148f);
        public static readonly Color CardBg = new Color(0.184f, 0.192f, 0.208f);
        public static readonly Color CardBgAlt = new Color(0.212f, 0.222f, 0.240f);
        public static readonly Color CardHover = new Color(0.248f, 0.260f, 0.284f);
        public static readonly Color SunkenBg = new Color(0.150f, 0.158f, 0.172f);
        public static readonly Color Border = new Color(0.270f, 0.282f, 0.310f);

        public static readonly Color Text = new Color(0.862f, 0.874f, 0.900f);
        public static readonly Color TextDim = new Color(0.640f, 0.660f, 0.700f);
        public static readonly Color TextFaint = new Color(0.500f, 0.520f, 0.560f);

        public static readonly Color Accent = new Color(0.350f, 0.600f, 0.940f);
        public static readonly Color AccentHover = new Color(0.430f, 0.670f, 0.980f);
        public static readonly Color AccentDim = new Color(0.216f, 0.298f, 0.440f);

        public static readonly Color Good = new Color(0.350f, 0.750f, 0.450f);
        public static readonly Color Warn = new Color(0.930f, 0.770f, 0.320f);
        public static readonly Color Bad = new Color(0.900f, 0.360f, 0.330f);

        public static readonly Color BtnBg = new Color(0.250f, 0.262f, 0.292f);
        public static readonly Color BtnHover = new Color(0.312f, 0.330f, 0.368f);

        // =====================================================================
        // 尺度
        // =====================================================================
        public const int SizeTitle = 14;
        public const int SizeBody = 12;
        public const int SizeSmall = 11;
        public const float Radius = 5f;
        public const float CardGap = 8f;

        // =====================================================================
        // 基础工具
        // =====================================================================

        /// <summary>四角圆角（分开赋值，避免链式赋值在 StyleLength 上踩坑）。</summary>
        public static void Rounded(VisualElement el, float radius)
        {
            if (el == null) return;
            el.style.borderTopLeftRadius = radius;
            el.style.borderTopRightRadius = radius;
            el.style.borderBottomLeftRadius = radius;
            el.style.borderBottomRightRadius = radius;
        }

        public static void Border1(VisualElement el, Color color)
        {
            if (el == null) return;
            el.style.borderTopWidth = 1f;
            el.style.borderBottomWidth = 1f;
            el.style.borderLeftWidth = 1f;
            el.style.borderRightWidth = 1f;
            el.style.borderTopColor = color;
            el.style.borderBottomColor = color;
            el.style.borderLeftColor = color;
            el.style.borderRightColor = color;
        }

        public static void Pad(VisualElement el, float left, float right, float top, float bottom)
        {
            if (el == null) return;
            el.style.paddingLeft = left;
            el.style.paddingRight = right;
            el.style.paddingTop = top;
            el.style.paddingBottom = bottom;
        }

        /// <summary>悬停换背景色。不用 :hover 伪类（见类注释）。</summary>
        public static void Hover(VisualElement el, Color normal, Color hover)
        {
            if (el == null) return;
            el.RegisterCallback<MouseEnterEvent>(delegate { el.style.backgroundColor = hover; });
            el.RegisterCallback<MouseLeaveEvent>(delegate { el.style.backgroundColor = normal; });
        }

        /// <summary>重新着色一个 Pill（文字变色时底色得跟着变，否则出现绿字红底）。</summary>
        public static void TintPill(Label pill, Color color)
        {
            if (pill == null) return;
            pill.style.color = color;
            pill.style.backgroundColor = new Color(color.r * 0.26f, color.g * 0.26f, color.b * 0.26f, 1f);
        }

        /// <summary>
        /// Markdown → UI Toolkit 富文本。
        /// 面板里用 `**粗体**` 写的内容原来是**原样显示星号**的（很难看），这里做最小转换；
        /// 先转义再替换，避免内容里的 `&lt;` 被当成标签。
        /// </summary>
        public static string RichText(string markdown)
        {
            if (string.IsNullOrEmpty(markdown)) return "";

            string s = markdown.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");
            s = Regex.Replace(s, @"\*\*(.+?)\*\*", "<b>$1</b>");
            s = Regex.Replace(s, @"`([^`]+)`", "<color=#9CDCFE>$1</color>");
            return s;
        }

        // =====================================================================
        // 容器
        // =====================================================================

        /// <summary>一张卡片：可选标题 + 可选右侧灰字说明。</summary>
        public static VisualElement Card(string title = null, string hint = null)
        {
            var card = new VisualElement();
            Rounded(card, Radius);
            card.style.backgroundColor = CardBg;
            Border1(card, Border);
            Pad(card, 8, 8, 6, 8);
            card.style.marginBottom = CardGap;

            if (!string.IsNullOrEmpty(title))
            {
                var row = new VisualElement();
                row.style.flexDirection = FlexDirection.Row;
                row.style.alignItems = Align.Center;
                row.style.marginBottom = 5;

                var t = new Label(title);
                t.style.fontSize = SizeBody;
                t.style.unityFontStyleAndWeight = FontStyle.Bold;
                t.style.color = Text;
                row.Add(t);

                if (!string.IsNullOrEmpty(hint))
                {
                    var h = new Label(hint);
                    h.style.fontSize = SizeSmall;
                    h.style.color = TextFaint;
                    h.style.marginLeft = 6;
                    h.style.flexShrink = 1;
                    h.style.whiteSpace = WhiteSpace.Normal;
                    row.Add(h);
                }
                card.Add(row);
            }
            return card;
        }

        /// <summary>页面级标题 + 副标题。</summary>
        public static VisualElement Header(string title, string subtitle)
        {
            var row = new VisualElement();
            row.style.flexDirection = FlexDirection.Row;
            row.style.alignItems = Align.Center;
            row.style.marginBottom = 8;

            var box = new VisualElement();
            box.style.flexGrow = 1;
            box.style.flexShrink = 1;

            var t = new Label(title);
            t.style.fontSize = SizeTitle;
            t.style.unityFontStyleAndWeight = FontStyle.Bold;
            t.style.color = Text;
            box.Add(t);

            if (!string.IsNullOrEmpty(subtitle))
            {
                var s = new Label(subtitle);
                s.style.fontSize = SizeSmall;
                s.style.color = TextFaint;
                s.style.marginTop = 1;
                s.style.whiteSpace = WhiteSpace.Normal;
                box.Add(s);
            }

            row.Add(box);
            return row;
        }

        public static VisualElement Divider(bool vertical = false)
        {
            var d = new VisualElement();
            d.style.backgroundColor = Border;
            if (vertical)
            {
                d.style.width = 1;
                // 固定高度而不是 alignSelf=Stretch：容器会换行（flexWrap）时 Stretch 会把它拉成奇形怪状
                d.style.height = 18;
                d.style.alignSelf = Align.Center;
                d.style.marginLeft = 6;
                d.style.marginRight = 6;
            }
            else
            {
                d.style.height = 1;
                d.style.marginTop = 6;
                d.style.marginBottom = 6;
            }
            return d;
        }

        /// <summary>贴着状态栏显示的「标签」。</summary>
        public static Label Pill(string text, Color color)
        {
            var l = new Label(text);
            l.style.fontSize = SizeSmall;
            l.style.color = color;
            l.style.backgroundColor = new Color(color.r * 0.26f, color.g * 0.26f, color.b * 0.26f, 1f);
            Pad(l, 7, 7, 3, 3);
            Rounded(l, 8f);
            l.style.flexShrink = 1;
            l.style.whiteSpace = WhiteSpace.Normal;
            return l;
        }

        // =====================================================================
        // 按钮
        // =====================================================================

        public static Button MakeButton(string text, Action onClick, Color bg, Color fg, Color hover)
        {
            var b = new Button(onClick);
            b.text = text;
            b.style.fontSize = SizeBody;
            b.style.color = fg;
            b.style.backgroundColor = bg;
            Pad(b, 10, 10, 4, 4);
            b.style.marginRight = 4;
            b.style.marginBottom = 2;
            Rounded(b, 4f);
            Hover(b, bg, hover);
            return b;
        }

        /// <summary>主操作（整屏只有一个）：强调色。</summary>
        public static Button Primary(string text, Action onClick)
        {
            return MakeButton(text, onClick, Accent, Color.white, AccentHover);
        }

        /// <summary>普通操作：中性底。</summary>
        public static Button Secondary(string text, Action onClick)
        {
            return MakeButton(text, onClick, BtnBg, Text, BtnHover);
        }

        /// <summary>次要操作（配置、取消之类）：无底色，悬停才亮。</summary>
        public static Button Ghost(string text, Action onClick)
        {
            var b = MakeButton(text, onClick, new Color(0f, 0f, 0f, 0f), TextDim, AccentDim);
            b.style.unityFontStyleAndWeight = FontStyle.Normal;
            return b;
        }

        /// <summary>标签页按钮：选中态用强调色底 + 粗体。</summary>
        public static Button Tab(string text, bool active, Action onClick)
        {
            var b = active
                ? MakeButton(text, onClick, AccentDim, Color.white, AccentDim)
                : MakeButton(text, onClick, CardBgAlt, TextDim, CardHover);
            b.style.unityFontStyleAndWeight = active ? FontStyle.Bold : FontStyle.Normal;
            Pad(b, 9, 9, 3, 3);
            return b;
        }

        // =====================================================================
        // 空状态
        // =====================================================================

        /// <summary>
        /// 空状态块：图标位（用字符代替，避免引资源）+ 一句人话 + 一个可点的下一步。
        /// 比孤零零一句「未发现超出预算的问题」有用得多 —— 用户第一次打开面板时，
        /// 最需要知道的是「下一步该点哪儿」。
        /// </summary>
        public static VisualElement EmptyState(string glyph, string title, string hint,
                                               string actionText = null, Action onAction = null,
                                               Color? glyphColor = null)
        {
            var box = new VisualElement();
            box.style.alignItems = Align.Center;
            box.style.justifyContent = Justify.Center;
            Pad(box, 16, 16, 34, 34);

            var g = new Label(glyph);
            g.style.fontSize = 28;
            g.style.color = glyphColor.HasValue ? glyphColor.Value : TextFaint;
            box.Add(g);

            var t = new Label(RichText(title));
            t.style.fontSize = SizeBody;
            t.style.color = Text;
            t.style.marginTop = 8;
            box.Add(t);

            if (!string.IsNullOrEmpty(hint))
            {
                var h = new Label(RichText(hint));
                h.style.fontSize = SizeSmall;
                h.style.color = TextFaint;
                h.style.marginTop = 5;
                h.style.whiteSpace = WhiteSpace.Normal;
                h.style.unityTextAlign = TextAnchor.MiddleCenter;
                box.Add(h);
            }

            if (!string.IsNullOrEmpty(actionText) && onAction != null)
            {
                var b = Primary(actionText, onAction);
                b.style.marginTop = 14;
                b.style.marginRight = 0;
                box.Add(b);
            }
            return box;
        }

        /// <summary>小号灰字说明（放在卡片底部）。</summary>
        public static Label Hint(string text)
        {
            var l = new Label(text);
            l.style.fontSize = SizeSmall;
            l.style.color = TextFaint;
            l.style.whiteSpace = WhiteSpace.Normal;
            l.style.marginTop = 3;
            return l;
        }

        /// <summary>富文本的严重度标记（嵌在标题里）。</summary>
        public static string SeverityTag(string label, Color color)
        {
            string hex = "#" + Mathf.RoundToInt(color.r * 255f).ToString("X2")
                             + Mathf.RoundToInt(color.g * 255f).ToString("X2")
                             + Mathf.RoundToInt(color.b * 255f).ToString("X2");
            return "<b><color=" + hex + ">[" + label + "]</color></b>";
        }

        /// <summary>
        /// 一条问题行的统一外观：左侧色条 + 标题 + 灰字正文。
        /// 资源 / 场景 / 代码三个列表共用它 —— 以前是「整行文字都用严重度颜色」，
        /// 一屏红黄字反而看不出哪条更严重。
        /// </summary>
        public static VisualElement IssueRow(string headlineMarkdown, Color severityColor, string body)
        {
            var card = new VisualElement();
            card.style.marginBottom = 5;
            Pad(card, 8, 8, 5, 5);
            card.style.backgroundColor = CardBgAlt;
            Rounded(card, 4f);
            card.style.borderLeftWidth = 3;
            card.style.borderLeftColor = severityColor;

            var head = new Label(RichText(headlineMarkdown));
            head.style.whiteSpace = WhiteSpace.Normal;
            head.style.fontSize = SizeBody;
            head.style.color = Text;
            card.Add(head);

            if (!string.IsNullOrEmpty(body))
            {
                var b = new Label(RichText(body));
                b.style.whiteSpace = WhiteSpace.Normal;
                b.style.fontSize = SizeSmall;
                b.style.color = TextDim;
                b.style.marginTop = 2;
                card.Add(b);
            }
            return card;
        }
    }
}
