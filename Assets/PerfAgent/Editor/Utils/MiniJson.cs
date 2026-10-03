using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace PerfAgent.Utils
{
    /// <summary>
    /// 极简 JSON 解析 / 序列化，零第三方依赖。
    /// 仅用于 LLM 线协议（工具 schema、工具调用参数、响应解析）。
    /// 解析结果类型：Dictionary&lt;string,object&gt; / List&lt;object&gt; / string / double / bool / null
    /// </summary>
    public static class MiniJson
    {
        // ---------------- Parse ----------------

        public static object Parse(string json)
        {
            if (string.IsNullOrEmpty(json)) return null;
            int i = 0;
            var value = ParseValue(json, ref i);
            return value;
        }

        /// <summary>解析失败返回 null，不抛异常。</summary>
        public static object ParseSafe(string json)
        {
            try { return Parse(json); }
            catch { return null; }
        }

        public static Dictionary<string, object> ParseObjectSafe(string json)
        {
            return AsDict(ParseSafe(json));
        }

        static void SkipWs(string s, ref int i)
        {
            while (i < s.Length)
            {
                char c = s[i];
                if (c == ' ' || c == '\t' || c == '\n' || c == '\r') i++;
                else break;
            }
        }

        static void Expect(string s, ref int i, string word)
        {
            if (i + word.Length > s.Length || string.CompareOrdinal(s, i, word, 0, word.Length) != 0)
                throw new FormatException("JSON 解析失败，位置 " + i + "，期望 " + word);
            i += word.Length;
        }

        static object ParseValue(string s, ref int i)
        {
            SkipWs(s, ref i);
            if (i >= s.Length) return null;

            char c = s[i];
            switch (c)
            {
                case '{': return ParseObject(s, ref i);
                case '[': return ParseArray(s, ref i);
                case '"': return ParseString(s, ref i);
                case 't': Expect(s, ref i, "true"); return true;
                case 'f': Expect(s, ref i, "false"); return false;
                case 'n': Expect(s, ref i, "null"); return null;
                default: return ParseNumber(s, ref i);
            }
        }

        static Dictionary<string, object> ParseObject(string s, ref int i)
        {
            var dict = new Dictionary<string, object>();
            i++; // {
            while (true)
            {
                SkipWs(s, ref i);
                if (i >= s.Length) throw new FormatException("JSON 对象未闭合");
                if (s[i] == '}') { i++; return dict; }
                if (s[i] == ',') { i++; continue; }

                string key = ParseString(s, ref i);
                SkipWs(s, ref i);
                if (i >= s.Length || s[i] != ':') throw new FormatException("JSON 对象缺少 ':'");
                i++;
                dict[key] = ParseValue(s, ref i);
            }
        }

        static List<object> ParseArray(string s, ref int i)
        {
            var list = new List<object>();
            i++; // [
            while (true)
            {
                SkipWs(s, ref i);
                if (i >= s.Length) throw new FormatException("JSON 数组未闭合");
                if (s[i] == ']') { i++; return list; }
                if (s[i] == ',') { i++; continue; }
                list.Add(ParseValue(s, ref i));
            }
        }

        static string ParseString(string s, ref int i)
        {
            SkipWs(s, ref i);
            if (i >= s.Length || s[i] != '"') throw new FormatException("JSON 字符串缺少起始引号");
            i++;
            var sb = new StringBuilder();
            while (i < s.Length)
            {
                char c = s[i++];
                if (c == '"') return sb.ToString();
                if (c != '\\') { sb.Append(c); continue; }

                if (i >= s.Length) break;
                char e = s[i++];
                switch (e)
                {
                    case '"': sb.Append('"'); break;
                    case '\\': sb.Append('\\'); break;
                    case '/': sb.Append('/'); break;
                    case 'b': sb.Append('\b'); break;
                    case 'f': sb.Append('\f'); break;
                    case 'n': sb.Append('\n'); break;
                    case 'r': sb.Append('\r'); break;
                    case 't': sb.Append('\t'); break;
                    case 'u':
                        if (i + 4 <= s.Length)
                        {
                            int code;
                            if (int.TryParse(s.Substring(i, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out code))
                            {
                                sb.Append((char)code);
                                i += 4;
                            }
                        }
                        break;
                    default: sb.Append(e); break;
                }
            }
            throw new FormatException("JSON 字符串未闭合");
        }

        static object ParseNumber(string s, ref int i)
        {
            int start = i;
            while (i < s.Length)
            {
                char c = s[i];
                if ((c >= '0' && c <= '9') || c == '-' || c == '+' || c == '.' || c == 'e' || c == 'E') i++;
                else break;
            }
            string token = s.Substring(start, i - start);
            double d;
            if (double.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out d))
                return d;
            throw new FormatException("JSON 非法数字: " + token);
        }

        // ---------------- Serialize ----------------

        public static string Serialize(object value)
        {
            var sb = new StringBuilder(256);
            Write(sb, value);
            return sb.ToString();
        }

        static void Write(StringBuilder sb, object v)
        {
            if (v == null) { sb.Append("null"); return; }

            if (v is string s) { WriteString(sb, s); return; }
            if (v is bool b) { sb.Append(b ? "true" : "false"); return; }
            if (v is Enum en) { WriteString(sb, en.ToString()); return; }

            if (v is IDictionary<string, object> typedDict)
            {
                sb.Append('{');
                bool first = true;
                foreach (var kv in typedDict)
                {
                    if (!first) sb.Append(',');
                    first = false;
                    WriteString(sb, kv.Key);
                    sb.Append(':');
                    Write(sb, kv.Value);
                }
                sb.Append('}');
                return;
            }

            if (v is IDictionary dict)
            {
                sb.Append('{');
                bool first = true;
                foreach (DictionaryEntry kv in dict)
                {
                    if (!first) sb.Append(',');
                    first = false;
                    WriteString(sb, Convert.ToString(kv.Key, CultureInfo.InvariantCulture));
                    sb.Append(':');
                    Write(sb, kv.Value);
                }
                sb.Append('}');
                return;
            }

            if (v is IEnumerable list && !(v is string))
            {
                sb.Append('[');
                bool first = true;
                foreach (var item in list)
                {
                    if (!first) sb.Append(',');
                    first = false;
                    Write(sb, item);
                }
                sb.Append(']');
                return;
            }

            switch (v)
            {
                case float f: WriteNumber(sb, f); return;
                case double d: WriteNumber(sb, d); return;
                case decimal m: WriteNumber(sb, (double)m); return;
                case byte by: sb.Append(by.ToString(CultureInfo.InvariantCulture)); return;
                case sbyte sb2: sb.Append(sb2.ToString(CultureInfo.InvariantCulture)); return;
                case short sh: sb.Append(sh.ToString(CultureInfo.InvariantCulture)); return;
                case ushort us: sb.Append(us.ToString(CultureInfo.InvariantCulture)); return;
                case int i32: sb.Append(i32.ToString(CultureInfo.InvariantCulture)); return;
                case uint u32: sb.Append(u32.ToString(CultureInfo.InvariantCulture)); return;
                case long i64: sb.Append(i64.ToString(CultureInfo.InvariantCulture)); return;
                case ulong u64: sb.Append(u64.ToString(CultureInfo.InvariantCulture)); return;
            }

            WriteString(sb, v.ToString());
        }

        static void WriteNumber(StringBuilder sb, double d)
        {
            if (double.IsNaN(d) || double.IsInfinity(d)) { sb.Append("null"); return; }
            if (Math.Abs(d - Math.Round(d)) < 1e-9 && Math.Abs(d) < 1e15)
                sb.Append(((long)Math.Round(d)).ToString(CultureInfo.InvariantCulture));
            else
                sb.Append(d.ToString("R", CultureInfo.InvariantCulture));
        }

        static void WriteString(StringBuilder sb, string s)
        {
            sb.Append('"');
            foreach (char c in s)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\b': sb.Append("\\b"); break;
                    case '\f': sb.Append("\\f"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < 32) sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                        else sb.Append(c);
                        break;
                }
            }
            sb.Append('"');
        }

        // ---------------- 便捷访问器 ----------------

        public static Dictionary<string, object> AsDict(object o)
        {
            return o as Dictionary<string, object>;
        }

        public static List<object> AsList(object o)
        {
            return o as List<object>;
        }

        public static object Get(IDictionary<string, object> d, string key)
        {
            if (d == null || string.IsNullOrEmpty(key)) return null;
            object v;
            return d.TryGetValue(key, out v) ? v : null;
        }

        public static string Str(IDictionary<string, object> d, string key, string def = null)
        {
            var v = Get(d, key);
            if (v == null) return def;
            return v as string ?? Convert.ToString(v, CultureInfo.InvariantCulture);
        }

        public static double Num(IDictionary<string, object> d, string key, double def = 0)
        {
            var v = Get(d, key);
            if (v == null) return def;
            if (v is double dv) return dv;
            if (v is bool) return def;
            double r;
            return double.TryParse(Convert.ToString(v, CultureInfo.InvariantCulture), NumberStyles.Float, CultureInfo.InvariantCulture, out r) ? r : def;
        }

        public static int Int(IDictionary<string, object> d, string key, int def = 0)
        {
            return (int)Num(d, key, def);
        }

        public static bool Bool(IDictionary<string, object> d, string key, bool def = false)
        {
            var v = Get(d, key);
            if (v == null) return def;
            if (v is bool bv) return bv;
            if (v is double dv) return Math.Abs(dv) > double.Epsilon;
            bool r;
            return bool.TryParse(Convert.ToString(v, CultureInfo.InvariantCulture), out r) ? r : def;
        }
    }
}
