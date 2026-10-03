using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Text;

namespace PerfAgent.Utils
{
    /// <summary>
    /// 跨版本反射适配层。
    ///
    /// 设计原则（见开发计划 P0）：
    ///  - 任何 Unity 未公开 / 跨版本易变的成员，一律走这里，保证包本身在任何版本都能编译通过。
    ///  - 找不到就返回 null / false，由调用方优雅降级，绝不抛异常打断采集。
    ///  - Tools/PerfAgent/API 探针 会列出本机实际存在的成员，便于按版本精修。
    /// </summary>
    public static class Reflect
    {
        public const BindingFlags StaticAll = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;
        public const BindingFlags InstanceAll = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;

        static readonly Dictionary<string, Type> TypeCache = new Dictionary<string, Type>();

        // ---------------- 类型查找 ----------------

        public static Type FindType(string fullName)
        {
            if (string.IsNullOrEmpty(fullName)) return null;
            Type cached;
            if (TypeCache.TryGetValue(fullName, out cached)) return cached;

            Type found = null;
            var assemblies = AppDomain.CurrentDomain.GetAssemblies();
            for (int i = 0; i < assemblies.Length; i++)
            {
                try { found = assemblies[i].GetType(fullName, false); }
                catch { found = null; }
                if (found != null) break;
            }
            TypeCache[fullName] = found;
            return found;
        }

        public static Type NestedType(Type t, string name)
        {
            if (t == null || string.IsNullOrEmpty(name)) return null;
            Type n = null;
            try { n = t.GetNestedType(name, StaticAll | InstanceAll); }
            catch { n = null; }
            if (n != null) return n;

            // 某些版本嵌套类型在别的程序集里，回退全扫描
            return FindType(t.FullName + "+" + name);
        }

        // ---------------- 成员查找 ----------------

        static MemberInfo FindMember(Type t, string name, bool isStatic, bool props, bool fields)
        {
            if (t == null || string.IsNullOrEmpty(name)) return null;
            var flags = isStatic ? StaticAll : InstanceAll;

            for (Type cur = t; cur != null; cur = cur.BaseType)
            {
                try
                {
                    if (props)
                    {
                        var p = cur.GetProperty(name, flags);
                        if (p != null) return p;
                    }
                    if (fields)
                    {
                        var f = cur.GetField(name, flags);
                        if (f != null) return f;
                    }
                }
                catch { /* 忽略单个成员异常 */ }
            }
            return null;
        }

        public static PropertyInfo Prop(Type t, string name, bool isStatic = false)
        {
            return FindMember(t, name, isStatic, true, false) as PropertyInfo;
        }

        public static FieldInfo Fld(Type t, string name, bool isStatic = false)
        {
            return FindMember(t, name, isStatic, false, true) as FieldInfo;
        }

        public static MethodInfo Method(Type t, string name, int argCount = -1, bool isStatic = false)
        {
            if (t == null || string.IsNullOrEmpty(name)) return null;
            var flags = isStatic ? StaticAll : InstanceAll;
            MethodInfo fallback = null;
            var methods = t.GetMethods(flags);
            for (int i = 0; i < methods.Length; i++)
            {
                var m = methods[i];
                if (m.Name != name) continue;
                if (argCount < 0) return m;
                if (m.GetParameters().Length == argCount) return m;
                if (fallback == null) fallback = m;
            }
            return fallback;
        }

        // ---------------- 读取 ----------------

        public static object Get(object target, string name)
        {
            if (target == null) return null;
            var t = target.GetType();
            var p = Prop(t, name, false);
            if (p != null)
            {
                try { return p.GetValue(target, null); } catch { }
            }
            var f = Fld(t, name, false);
            if (f != null)
            {
                try { return f.GetValue(target); } catch { }
            }
            return null;
        }

        public static object GetStatic(Type t, string name)
        {
            if (t == null) return null;
            var p = Prop(t, name, true);
            if (p != null)
            {
                try { return p.GetValue(null, null); } catch { }
            }
            var f = Fld(t, name, true);
            if (f != null)
            {
                try { return f.GetValue(null); } catch { }
            }
            return null;
        }

        public static bool SetStatic(Type t, string name, object value)
        {
            if (t == null) return false;
            var p = Prop(t, name, true);
            if (p != null && p.CanWrite)
            {
                try { p.SetValue(null, value, null); return true; } catch { }
            }
            var f = Fld(t, name, true);
            if (f != null)
            {
                try { f.SetValue(null, value); return true; } catch { }
            }
            return false;
        }

        // ---------------- 调用 ----------------

        static MethodInfo PickMethod(Type t, string name, object[] args, bool isStatic)
        {
            if (t == null) return null;
            var flags = isStatic ? StaticAll : InstanceAll;
            MethodInfo fallback = null;
            var methods = t.GetMethods(flags);

            for (int i = 0; i < methods.Length; i++)
            {
                var m = methods[i];
                if (m.Name != name) continue;
                var ps = m.GetParameters();
                if (args == null || args.Length == 0)
                {
                    if (ps.Length == 0) return m;
                    continue;
                }
                if (ps.Length != args.Length) continue;

                bool ok = true;
                for (int j = 0; j < ps.Length; j++)
                {
                    if (args[j] == null) continue;
                    if (!ps[j].ParameterType.IsInstanceOfType(args[j])) { ok = false; break; }
                }
                if (ok) return m;
                if (fallback == null) fallback = m;
            }
            return fallback;
        }

        static object[] CoerceArgs(MethodInfo m, object[] args)
        {
            if (m == null || args == null) return args;
            var ps = m.GetParameters();
            if (ps.Length != args.Length) return args;

            var result = new object[args.Length];
            for (int i = 0; i < args.Length; i++)
            {
                var want = ps[i].ParameterType;
                var have = args[i];
                if (have == null)
                {
                    // 自动构造 List<T> 之类的出参容器（如 GetItemChildren(int, List<int>)）
                    result[i] = want.IsValueType ? Activator.CreateInstance(want) : TryCreateCollection(want);
                    continue;
                }
                if (want.IsInstanceOfType(have)) { result[i] = have; continue; }
                try { result[i] = Convert.ChangeType(have, want, CultureInfo.InvariantCulture); }
                catch { result[i] = have; }
            }
            return result;
        }

        static object TryCreateCollection(Type want)
        {
            try
            {
                if (want.IsAbstract || want.IsInterface) return null;
                if (want.GetConstructor(Type.EmptyTypes) != null) return Activator.CreateInstance(want);
            }
            catch { }
            return null;
        }

        public static object Invoke(object target, string name, params object[] args)
        {
            object result;
            TryInvoke(target, name, out result, args);
            return result;
        }

        public static bool TryInvoke(object target, string name, out object result, params object[] args)
        {
            result = null;
            if (target == null) return false;
            var m = PickMethod(target.GetType(), name, args, false);
            if (m == null) return false;
            try
            {
                result = m.Invoke(target, CoerceArgs(m, args));
                return true;
            }
            catch { return false; }
        }

        public static object InvokeStatic(Type t, string name, params object[] args)
        {
            if (t == null) return null;
            var m = PickMethod(t, name, args, true);
            if (m == null) return null;
            try { return m.Invoke(null, CoerceArgs(m, args)); }
            catch { return null; }
        }

        public static bool InvokeStaticVoid(Type t, string name, params object[] args)
        {
            if (t == null) return false;
            var m = PickMethod(t, name, args, true);
            if (m == null) return false;
            try { m.Invoke(null, CoerceArgs(m, args)); return true; }
            catch { return false; }
        }

        // ---------------- 类型化读取 ----------------

        /// <summary>有些版本返回单元素数组，这里做一次拆包。</summary>
        static object Unwrap1(object v)
        {
            var arr = v as Array;
            if (arr != null && arr.Length == 1) return arr.GetValue(0);
            return v;
        }

        public static bool TryGetBool(object target, string name, out bool value)
        {
            value = false;
            var v = Unwrap1(Get(target, name));
            if (v == null) return false;
            try { value = Convert.ToBoolean(v, CultureInfo.InvariantCulture); return true; }
            catch { return false; }
        }

        public static bool TryGetInt(object target, string name, out int value)
        {
            value = 0;
            var v = Unwrap1(Get(target, name));
            if (v == null) return false;
            try { value = Convert.ToInt32(v, CultureInfo.InvariantCulture); return true; }
            catch { return false; }
        }

        public static bool TryGetLong(object target, string name, out long value)
        {
            value = 0L;
            var v = Unwrap1(Get(target, name));
            if (v == null) return false;
            try { value = Convert.ToInt64(v, CultureInfo.InvariantCulture); return true; }
            catch { return false; }
        }

        public static bool TryGetDouble(object target, string name, out double value)
        {
            value = 0d;
            var v = Unwrap1(Get(target, name));
            if (v == null) return false;
            try { value = Convert.ToDouble(v, CultureInfo.InvariantCulture); return true; }
            catch { return false; }
        }

        public static bool TryGetString(object target, string name, out string value)
        {
            value = null;
            var v = Unwrap1(Get(target, name));
            if (v == null) return false;
            value = v.ToString();
            return true;
        }

        public static string GetString(object target, string name, string def = "")
        {
            string s;
            return TryGetString(target, name, out s) && s != null ? s : def;
        }

        public static int GetInt(object target, string name, int def = 0)
        {
            int v;
            return TryGetInt(target, name, out v) ? v : def;
        }

        public static double GetDouble(object target, string name, double def = 0)
        {
            double v;
            return TryGetDouble(target, name, out v) ? v : def;
        }

        // ---------------- 探针用 ----------------

        /// <summary>列出类型上可用于适配的成员，供 API 探针窗口输出。</summary>
        public static string Describe(Type t, bool includeNonPublic = false, int maxMembers = 200)
        {
            if (t == null) return "(类型不存在)";
            var flags = (includeNonPublic ? InstanceAll | StaticAll
                                          : BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static);
            var sb = new StringBuilder();

            var methods = t.GetMethods(flags);
            Array.Sort(methods, (a, b) => string.CompareOrdinal(a.Name, b.Name));
            int count = 0;
            foreach (var m in methods)
            {
                if (m.IsSpecialName) continue;
                if (count++ > maxMembers) break;
                sb.Append(m.IsStatic ? "static " : "       ");
                sb.Append(m.ReturnType.Name).Append(' ').Append(m.Name).Append('(');
                var ps = m.GetParameters();
                for (int i = 0; i < ps.Length; i++)
                {
                    if (i > 0) sb.Append(", ");
                    sb.Append(ps[i].ParameterType.Name).Append(' ').Append(ps[i].Name);
                }
                sb.Append(")\n");
            }

            foreach (var p in t.GetProperties(flags))
            {
                if (count++ > maxMembers) break;
                sb.Append(p.GetGetMethod(true) != null && p.GetGetMethod(true).IsStatic ? "static " : "       ");
                sb.Append("prop ").Append(p.PropertyType.Name).Append(' ').Append(p.Name).Append('\n');
            }

            return sb.ToString();
        }
    }
}
