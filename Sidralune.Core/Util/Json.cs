using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Text;

namespace Sidralune.Core.Util
{
    /// <summary>
    /// Small dependency-free JSON parser/serializer. Objects become Dictionary&lt;string, object&gt;,
    /// arrays List&lt;object&gt;, numbers double (or long when integral), plus bool/null/string.
    /// </summary>
    public static class Json
    {
        public static object Parse(string text)
        {
            int i = 0;
            var v = ParseValue(text, ref i);
            SkipWs(text, ref i);
            return v;
        }

        public static Dictionary<string, object> ParseObject(string text) => Parse(text) as Dictionary<string, object>;

        private static void SkipWs(string s, ref int i)
        {
            while (i < s.Length && char.IsWhiteSpace(s[i])) i++;
        }

        private static object ParseValue(string s, ref int i)
        {
            SkipWs(s, ref i);
            if (i >= s.Length) throw new FormatException("Unexpected end of JSON");
            char c = s[i];
            if (c == '{') return ParseObj(s, ref i);
            if (c == '[') return ParseArr(s, ref i);
            if (c == '"') return ParseStr(s, ref i);
            if (c == 't' && string.CompareOrdinal(s, i, "true", 0, 4) == 0) { i += 4; return true; }
            if (c == 'f' && string.CompareOrdinal(s, i, "false", 0, 5) == 0) { i += 5; return false; }
            if (c == 'n' && string.CompareOrdinal(s, i, "null", 0, 4) == 0) { i += 4; return null; }
            if (c == 'N' && string.CompareOrdinal(s, i, "NaN", 0, 3) == 0) { i += 3; return double.NaN; }
            return ParseNum(s, ref i);
        }

        private static Dictionary<string, object> ParseObj(string s, ref int i)
        {
            var d = new Dictionary<string, object>(StringComparer.Ordinal);
            i++;
            SkipWs(s, ref i);
            if (i < s.Length && s[i] == '}') { i++; return d; }
            while (i < s.Length)
            {
                SkipWs(s, ref i);
                string key = ParseStr(s, ref i);
                SkipWs(s, ref i);
                if (s[i] != ':') throw new FormatException("Expected ':' at " + i);
                i++;
                d[key] = ParseValue(s, ref i);
                SkipWs(s, ref i);
                if (s[i] == ',') { i++; continue; }
                if (s[i] == '}') { i++; return d; }
                throw new FormatException("Expected ',' or '}' at " + i);
            }
            return d;
        }

        private static List<object> ParseArr(string s, ref int i)
        {
            var l = new List<object>();
            i++;
            SkipWs(s, ref i);
            if (i < s.Length && s[i] == ']') { i++; return l; }
            while (i < s.Length)
            {
                l.Add(ParseValue(s, ref i));
                SkipWs(s, ref i);
                if (s[i] == ',') { i++; continue; }
                if (s[i] == ']') { i++; return l; }
                throw new FormatException("Expected ',' or ']' at " + i);
            }
            return l;
        }

        private static string ParseStr(string s, ref int i)
        {
            if (s[i] != '"') throw new FormatException("Expected string at " + i);
            i++;
            var sb = new StringBuilder();
            while (i < s.Length)
            {
                char c = s[i++];
                if (c == '"') return sb.ToString();
                if (c == '\\')
                {
                    char e = s[i++];
                    switch (e)
                    {
                        case 'n': sb.Append('\n'); break;
                        case 'r': sb.Append('\r'); break;
                        case 't': sb.Append('\t'); break;
                        case 'b': sb.Append('\b'); break;
                        case 'f': sb.Append('\f'); break;
                        case 'u': sb.Append((char)Convert.ToInt32(s.Substring(i, 4), 16)); i += 4; break;
                        default: sb.Append(e); break;
                    }
                }
                else sb.Append(c);
            }
            throw new FormatException("Unterminated string");
        }

        private static object ParseNum(string s, ref int i)
        {
            int start = i;
            while (i < s.Length && "+-0123456789.eE".IndexOf(s[i]) >= 0) i++;
            string t = s.Substring(start, i - start);
            long l;
            if (t.IndexOfAny(new[] { '.', 'e', 'E' }) < 0 && long.TryParse(t, NumberStyles.Integer, CultureInfo.InvariantCulture, out l)) return l;
            double d;
            if (double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out d)) return d;
            throw new FormatException("Bad number '" + t + "' at " + start);
        }

        // ------------------------------------------------------------------ serialization

        public static string Serialize(object o, bool indent = false)
        {
            var sb = new StringBuilder();
            Write(sb, o, indent, 0);
            return sb.ToString();
        }

        private static void Write(StringBuilder sb, object o, bool indent, int level)
        {
            if (o == null) { sb.Append("null"); return; }
            if (o is string) { WriteStr(sb, (string)o); return; }
            if (o is bool) { sb.Append((bool)o ? "true" : "false"); return; }
            if (o is double || o is float)
            {
                double d = Convert.ToDouble(o);
                if (double.IsNaN(d) || double.IsInfinity(d)) sb.Append("null");
                else sb.Append(d.ToString("R", CultureInfo.InvariantCulture));
                return;
            }
            if (o is int || o is long || o is short || o is byte || o is uint || o is ulong || o is decimal)
            {
                sb.Append(Convert.ToString(o, CultureInfo.InvariantCulture)); return;
            }
            if (o is DateTime) { WriteStr(sb, ((DateTime)o).ToString("o", CultureInfo.InvariantCulture)); return; }
            if (o is Enum) { WriteStr(sb, o.ToString()); return; }
            var dict = o as IDictionary;
            if (dict != null)
            {
                sb.Append('{');
                bool first = true;
                foreach (DictionaryEntry kv in dict)
                {
                    if (!first) sb.Append(',');
                    first = false;
                    NewLine(sb, indent, level + 1);
                    WriteStr(sb, Convert.ToString(kv.Key, CultureInfo.InvariantCulture));
                    sb.Append(indent ? ": " : ":");
                    Write(sb, kv.Value, indent, level + 1);
                }
                if (!first) NewLine(sb, indent, level);
                sb.Append('}');
                return;
            }
            var en = o as IEnumerable;
            if (en != null)
            {
                sb.Append('[');
                bool first = true;
                foreach (var item in en)
                {
                    if (!first) sb.Append(',');
                    first = false;
                    NewLine(sb, indent, level + 1);
                    Write(sb, item, indent, level + 1);
                }
                if (!first) NewLine(sb, indent, level);
                sb.Append(']');
                return;
            }
            // reflect public properties / fields
            sb.Append('{');
            bool f2 = true;
            foreach (var p in o.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (!p.CanRead || p.GetIndexParameters().Length > 0 || !p.CanWrite) continue;
                if (!f2) sb.Append(',');
                f2 = false;
                NewLine(sb, indent, level + 1);
                WriteStr(sb, p.Name);
                sb.Append(indent ? ": " : ":");
                Write(sb, p.GetValue(o, null), indent, level + 1);
            }
            foreach (var fi in o.GetType().GetFields(BindingFlags.Public | BindingFlags.Instance))
            {
                if (!f2) sb.Append(',');
                f2 = false;
                NewLine(sb, indent, level + 1);
                WriteStr(sb, fi.Name);
                sb.Append(indent ? ": " : ":");
                Write(sb, fi.GetValue(o), indent, level + 1);
            }
            if (!f2) NewLine(sb, indent, level);
            sb.Append('}');
        }

        private static void NewLine(StringBuilder sb, bool indent, int level)
        {
            if (!indent) return;
            sb.Append('\n');
            sb.Append(' ', level * 2);
        }

        private static void WriteStr(StringBuilder sb, string s)
        {
            sb.Append('"');
            foreach (char c in s)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < 0x20) sb.AppendFormat("\\u{0:x4}", (int)c);
                        else sb.Append(c);
                        break;
                }
            }
            sb.Append('"');
        }

        // ------------------------------------------------------------------ typed population

        /// <summary>Populate a POCO's public settable properties/fields from a parsed JSON object.</summary>
        public static T Populate<T>(Dictionary<string, object> d) where T : new()
        {
            return (T)PopulateType(typeof(T), d);
        }

        public static object PopulateType(Type t, object value)
        {
            if (value == null) return t.IsValueType ? Activator.CreateInstance(t) : null;
            if (t == typeof(object)) return value;
            if (t == typeof(string)) return Convert.ToString(value, CultureInfo.InvariantCulture);
            if (t == typeof(double)) return value == null ? double.NaN : Convert.ToDouble(value, CultureInfo.InvariantCulture);
            if (t == typeof(float)) return Convert.ToSingle(value, CultureInfo.InvariantCulture);
            if (t == typeof(int)) return Convert.ToInt32(value, CultureInfo.InvariantCulture);
            if (t == typeof(long)) return Convert.ToInt64(value, CultureInfo.InvariantCulture);
            if (t == typeof(bool)) return Convert.ToBoolean(value, CultureInfo.InvariantCulture);
            if (t == typeof(DateTime)) return DateTime.Parse(Convert.ToString(value, CultureInfo.InvariantCulture), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
            if (t.IsEnum) return Enum.Parse(t, Convert.ToString(value), true);
            if (t.IsArray)
            {
                var l = value as List<object>;
                var et = t.GetElementType();
                var arr = Array.CreateInstance(et, l != null ? l.Count : 0);
                for (int i = 0; l != null && i < l.Count; i++) arr.SetValue(PopulateType(et, l[i]), i);
                return arr;
            }
            if (t.IsGenericType && t.GetGenericTypeDefinition() == typeof(List<>))
            {
                var et = t.GetGenericArguments()[0];
                var list = (IList)Activator.CreateInstance(t);
                var l = value as List<object>;
                if (l != null) foreach (var item in l) list.Add(PopulateType(et, item));
                return list;
            }
            if (t.IsGenericType && t.GetGenericTypeDefinition() == typeof(Dictionary<,>))
            {
                var vt = t.GetGenericArguments()[1];
                var dict = (IDictionary)Activator.CreateInstance(t);
                var src = value as Dictionary<string, object>;
                if (src != null) foreach (var kv in src) dict[kv.Key] = PopulateType(vt, kv.Value);
                return dict;
            }
            var d = value as Dictionary<string, object>;
            if (d == null) return null;
            var obj = Activator.CreateInstance(t);
            foreach (var p in t.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                object v;
                if (!p.CanWrite || !d.TryGetValue(p.Name, out v)) continue;
                try { p.SetValue(obj, PopulateType(p.PropertyType, v), null); } catch { }
            }
            foreach (var f in t.GetFields(BindingFlags.Public | BindingFlags.Instance))
            {
                object v;
                if (!d.TryGetValue(f.Name, out v)) continue;
                try { f.SetValue(obj, PopulateType(f.FieldType, v)); } catch { }
            }
            return obj;
        }

        // helpers for navigating parsed trees
        public static object Get(object node, string path)
        {
            foreach (var part in path.Split('.'))
            {
                var d = node as Dictionary<string, object>;
                if (d != null) { if (!d.TryGetValue(part, out node)) return null; continue; }
                var l = node as List<object>;
                int idx;
                if (l != null && int.TryParse(part, out idx) && idx < l.Count) { node = l[idx]; continue; }
                return null;
            }
            return node;
        }

        public static double GetDouble(object node, string path, double fallback = double.NaN)
        {
            var v = Get(node, path);
            if (v == null) return fallback;
            try { return Convert.ToDouble(v, CultureInfo.InvariantCulture); } catch { return fallback; }
        }

        public static string GetString(object node, string path) => Convert.ToString(Get(node, path), CultureInfo.InvariantCulture);
    }
}
