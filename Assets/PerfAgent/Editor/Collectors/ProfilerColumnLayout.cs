using System;
using System.Collections.Generic;
using System.Globalization;

namespace PerfAgent.Collectors
{
    /// <summary>
    /// Profiler 层级视图的列语义识别。
    ///
    /// <para><b>为什么不写死列号</b></para>
    /// 不同 Unity 版本的列顺序与格式都不一样，而这个视图**没有**任何「列名 / 列数」API
    /// （<c>FrameDataView</c> / <c>HierarchyFrameDataView</c> 上既没有 <c>columnCount</c>，
    /// 也没有 <c>GetColumnName</c>），只能从单元格内容反推。
    ///
    /// <para><b>实测列语义（Unity 2022.3.62f2c1，主线程 Hierarchy 视图，8 列）</b></para>
    /// <code>
    /// [0]=EditorLoop  名称
    /// [1]=0.0%        百分比（Total%）
    /// [2]=0.0%        百分比（Self%）
    /// [3]=1           调用次数
    /// [4]=0 B         GC Alloc（带单位）
    /// [5]=0.06        耗时（裸小数，**没有 ms 后缀**）
    /// [6]=0.06        耗时（裸小数）
    /// [7]=N/A         其它
    /// </code>
    ///
    /// 之前的实现只认「带 ms 后缀」或带单位的字符串，于是 [5][6] 全部解析成 0，
    /// 排行的每一行都是 <c>selfMs = 0</c> —— 数据看起来「有 60 条」，实际上一条可用的都没有。
    /// 这里改成：先按内容把每一列归类，再按类别解析数值。
    /// </summary>
    public class ProfilerColumnLayout
    {
        /// <summary>调用次数列（整数列）。-1 = 未识别到。</summary>
        public int callsColumn = -1;

        /// <summary>GC Alloc 列（带单位的字节列）。-1 = 未识别到。</summary>
        public int gcAllocColumn = -1;

        /// <summary>耗时列（裸小数，或带 ms 后缀）。顺序按列号升序。</summary>
        public readonly List<int> msColumns = new List<int>();

        /// <summary>百分比列（Total% / Self%）。顺序按列号升序。</summary>
        public readonly List<int> percentColumns = new List<int>();

        /// <summary>识别到的列数。</summary>
        public int columnCount;

        /// <summary>是否至少认出一种有用的列。</summary>
        public bool IsUsable
        {
            get { return msColumns.Count > 0 || gcAllocColumn >= 0 || callsColumn >= 0; }
        }

        /// <summary>供提示/探针显示的一句话描述。</summary>
        public string Describe()
        {
            var sb = new System.Text.StringBuilder();
            sb.Append("列数 ").Append(columnCount);
            sb.Append("，耗时列 [").Append(Join(msColumns)).Append(']');
            sb.Append("，GC 分配列 ").Append(gcAllocColumn < 0 ? "未识别" : gcAllocColumn.ToString(CultureInfo.InvariantCulture));
            sb.Append("，调用次数列 ").Append(callsColumn < 0 ? "未识别" : callsColumn.ToString(CultureInfo.InvariantCulture));
            sb.Append("，百分比列 [").Append(Join(percentColumns)).Append(']');
            return sb.ToString();
        }

        static string Join(List<int> list)
        {
            if (list.Count == 0) return "-";
            var sb = new System.Text.StringBuilder();
            for (int i = 0; i < list.Count; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append(list[i].ToString(CultureInfo.InvariantCulture));
            }
            return sb.ToString();
        }

        /// <summary>
        /// 从若干行的单元格文本里识别列语义。
        /// 至少要看到几行数据才有意义 —— 单行反推会把「1」这种调用次数误判成耗时。
        /// </summary>
        public static ProfilerColumnLayout Detect(List<string[]> rows)
        {
            var layout = new ProfilerColumnLayout();
            if (rows == null || rows.Count == 0) return layout;

            int cols = 0;
            for (int i = 0; i < rows.Count; i++)
                if (rows[i] != null && rows[i].Length > cols) cols = rows[i].Length;
            layout.columnCount = cols;
            if (cols == 0) return layout;

            var seen = new int[cols];
            var percent = new int[cols];
            var msWithUnit = new int[cols];
            var bytes = new int[cols];
            var integer = new int[cols];
            var decimalOnly = new int[cols];

            int limit = Math.Min(rows.Count, 40);
            for (int r = 0; r < limit; r++)
            {
                var row = rows[r];
                if (row == null) continue;
                for (int c = 0; c < cols && c < row.Length; c++)
                {
                    string cell = (row[c] ?? "").Trim();
                    if (cell.Length == 0) continue;
                    seen[c]++;
                    if (IsPercent(cell)) percent[c]++;
                    else if (HasMsUnit(cell)) msWithUnit[c]++;
                    else if (HasByteUnit(cell)) bytes[c]++;
                    else if (IsBareInteger(cell)) integer[c]++;
                    else if (IsBareDecimal(cell)) decimalOnly[c]++;
                }
            }

            for (int c = 0; c < cols; c++)
            {
                if (seen[c] == 0) continue;                                  // 整列都空，认不出
                if (percent[c] > 0) { layout.percentColumns.Add(c); continue; }
                if (msWithUnit[c] > 0) { layout.msColumns.Add(c); continue; }
                if (bytes[c] > 0)
                {
                    if (layout.gcAllocColumn < 0) layout.gcAllocColumn = c;
                    continue;
                }
                if (integer[c] > 0 && decimalOnly[c] == 0)
                {
                    // 纯整数：调用次数（2022 的 [3]）。名称列不会是纯整数列。
                    if (layout.callsColumn < 0) layout.callsColumn = c;
                    continue;
                }
                if (decimalOnly[c] > 0) layout.msColumns.Add(c);             // 裸小数：耗时（2022 的 [5][6]）
            }

            return layout;
        }

        // =====================================================================
        // 单元格解析
        // =====================================================================

        /// <summary>解析一个数值单元格。认不出返回 NaN（绝不返回 0 冒充数值）。</summary>
        public static double ParseNumber(string cell)
        {
            string s = Normalize(cell);
            if (s.Length == 0) return double.NaN;

            bool negative = false;
            int i = 0;
            if (s[0] == '-' || s[0] == '+') { negative = s[0] == '-'; i = 1; }
            if (i >= s.Length) return double.NaN;

            var digits = new System.Text.StringBuilder();
            bool dot = false;
            for (; i < s.Length; i++)
            {
                char ch = s[i];
                if (ch >= '0' && ch <= '9') { digits.Append(ch); continue; }
                if (ch == ',' || ch == ' ') continue;                        // 千分位 / 单位前空格
                if (ch == '.')
                {
                    if (dot) return double.NaN;
                    dot = true;
                    digits.Append(ch);
                    continue;
                }
                break;                                                      // 遇到单位就停（"12.3ms" / "1.2 KB" / "45%"）
            }

            string text = digits.ToString();
            if (text.Length == 0 || text == ".") return double.NaN;

            double value;
            if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value)) return double.NaN;
            return negative ? -value : value;
        }

        /// <summary>解析字节单元格（"0 B" / "1.2 KB" / "3.4 MB" / "1,024 B"）。认不出返回 0。</summary>
        public static long ParseBytes(string cell)
        {
            string s = Normalize(cell);
            if (s.Length == 0) return 0L;

            double value = ParseNumber(s);
            if (double.IsNaN(value)) return 0L;

            string up = s.ToUpperInvariant();
            double scale = 1.0;
            if (up.EndsWith("KB")) scale = 1024.0;
            else if (up.EndsWith("MB")) scale = 1024.0 * 1024.0;
            else if (up.EndsWith("GB")) scale = 1024.0 * 1024.0 * 1024.0;

            double bytes = value * scale;
            if (bytes <= 0) return 0L;
            if (bytes > long.MaxValue) return long.MaxValue;
            return (long)Math.Round(bytes);
        }

        /// <summary>解析调用次数单元格；认不出返回 0。</summary>
        public static long ParseCount(string cell)
        {
            double value = ParseNumber(cell);
            if (double.IsNaN(value) || value <= 0) return 0L;
            if (value > long.MaxValue) return long.MaxValue;
            return (long)Math.Round(value);
        }

        static string Normalize(string cell)
        {
            if (string.IsNullOrEmpty(cell)) return "";
            string s = cell.Trim();
            if (s == "-" || s == "N/A" || s == "n/a" || s == "--") return "";
            return s;
        }

        static bool IsPercent(string s) { return s.Length > 1 && s[s.Length - 1] == '%'; }

        static bool HasMsUnit(string s)
        {
            return s.Length > 2 && s.EndsWith("ms", StringComparison.OrdinalIgnoreCase);
        }

        static bool HasByteUnit(string s)
        {
            string up = s.ToUpperInvariant();
            return up.EndsWith(" B") || up.EndsWith("KB") || up.EndsWith("MB") || up.EndsWith("GB")
                   || up.EndsWith("BYTES") || up.EndsWith(" KIB") || up.EndsWith(" MIB");
        }

        static bool IsBareInteger(string s)
        {
            bool any = false;
            for (int i = 0; i < s.Length; i++)
            {
                char ch = s[i];
                if (ch == ',' || ch == ' ') continue;
                if (ch < '0' || ch > '9') return false;
                any = true;
            }
            return any;
        }

        static bool IsBareDecimal(string s)
        {
            bool dot = false, any = false;
            for (int i = 0; i < s.Length; i++)
            {
                char ch = s[i];
                if (ch == ',' || ch == ' ') continue;
                if (ch == '.') { if (dot) return false; dot = true; continue; }
                if (ch < '0' || ch > '9') return false;
                any = true;
            }
            return dot && any;
        }
    }
}
