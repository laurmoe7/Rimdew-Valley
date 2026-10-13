using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace RimDewBridge
{
    /// <summary>Tiny JSON reader/writer. Objects are Dictionary&lt;string, object&gt;, arrays are List&lt;object&gt;,
    /// numbers are double.</summary>
    public static class MiniJson
    {
        public static object Parse(string s)
        {
            int i = 0;
            return ParseValue(s, ref i);
        }

        private static void Ws(string s, ref int i) { while (i < s.Length && char.IsWhiteSpace(s[i])) i++; }

        private static object ParseValue(string s, ref int i)
        {
            Ws(s, ref i);
            char c = s[i];
            if (c == '{')
            {
                var d = new Dictionary<string, object>();
                i++; Ws(s, ref i);
                if (s[i] == '}') { i++; return d; }
                while (true)
                {
                    Ws(s, ref i);
                    string k = ParseString(s, ref i);
                    Ws(s, ref i); i++; // ':'
                    d[k] = ParseValue(s, ref i);
                    Ws(s, ref i);
                    if (s[i++] == '}') return d;
                }
            }
            if (c == '[')
            {
                var l = new List<object>();
                i++; Ws(s, ref i);
                if (s[i] == ']') { i++; return l; }
                while (true)
                {
                    l.Add(ParseValue(s, ref i));
                    Ws(s, ref i);
                    if (s[i++] == ']') return l;
                }
            }
            if (c == '"') return ParseString(s, ref i);
            if (string.CompareOrdinal(s, i, "true", 0, 4) == 0) { i += 4; return true; }
            if (string.CompareOrdinal(s, i, "false", 0, 5) == 0) { i += 5; return false; }
            if (string.CompareOrdinal(s, i, "null", 0, 4) == 0) { i += 4; return null; }
            int st = i;
            while (i < s.Length && "+-0123456789.eE".IndexOf(s[i]) >= 0) i++;
            return double.Parse(s.Substring(st, i - st), CultureInfo.InvariantCulture);
        }

        private static string ParseString(string s, ref int i)
        {
            var sb = new StringBuilder();
            i++; // opening quote
            while (s[i] != '"')
            {
                char c = s[i++];
                if (c != '\\') { sb.Append(c); continue; }
                char e = s[i++];
                switch (e)
                {
                    case 'n': sb.Append('\n'); break;
                    case 't': sb.Append('\t'); break;
                    case 'r': sb.Append('\r'); break;
                    case 'b': sb.Append('\b'); break;
                    case 'f': sb.Append('\f'); break;
                    case 'u': sb.Append((char)Convert.ToInt32(s.Substring(i, 4), 16)); i += 4; break;
                    default: sb.Append(e); break;
                }
            }
            i++;
            return sb.ToString();
        }

        public static string Serialize(object o)
        {
            var sb = new StringBuilder();
            Write(sb, o);
            return sb.ToString();
        }

        private static void Write(StringBuilder sb, object o)
        {
            if (o == null) sb.Append("null");
            else if (o is string) WriteString(sb, (string)o);
            else if (o is bool) sb.Append((bool)o ? "true" : "false");
            else if (o is IDictionary)
            {
                sb.Append('{');
                bool first = true;
                foreach (DictionaryEntry kv in (IDictionary)o)
                {
                    if (!first) sb.Append(',');
                    first = false;
                    WriteString(sb, (string)kv.Key);
                    sb.Append(':');
                    Write(sb, kv.Value);
                }
                sb.Append('}');
            }
            else if (o is IEnumerable)
            {
                sb.Append('[');
                bool first = true;
                foreach (var v in (IEnumerable)o)
                {
                    if (!first) sb.Append(',');
                    first = false;
                    Write(sb, v);
                }
                sb.Append(']');
            }
            else sb.Append(Convert.ToString(o, CultureInfo.InvariantCulture));
        }

        private static void WriteString(StringBuilder sb, string s)
        {
            sb.Append('"');
            foreach (char c in s)
            {
                if (c == '"' || c == '\\') sb.Append('\\').Append(c);
                else if (c == '\n') sb.Append("\\n");
                else if (c < 32) sb.Append("\\u").Append(((int)c).ToString("x4"));
                else sb.Append(c);
            }
            sb.Append('"');
        }
    }

    /// <summary>Typed accessors for parsed messages.</summary>
    public static class J
    {
        public static string Str(this Dictionary<string, object> d, string k, string def = "")
        { object v; return d.TryGetValue(k, out v) && v != null ? Convert.ToString(v, CultureInfo.InvariantCulture) : def; }

        public static double Num(this Dictionary<string, object> d, string k, double def = 0)
        { object v; return d.TryGetValue(k, out v) && v is double ? (double)v : def; }

        public static bool Bool(this Dictionary<string, object> d, string k, bool def = false)
        { object v; return d.TryGetValue(k, out v) && v is bool ? (bool)v : def; }

        public static List<object> List(this Dictionary<string, object> d, string k)
        { object v; return d.TryGetValue(k, out v) && v is List<object> ? (List<object>)v : new List<object>(); }

        public static Dictionary<string, object> Obj(this object o) { return o as Dictionary<string, object> ?? new Dictionary<string, object>(); }
    }
}
