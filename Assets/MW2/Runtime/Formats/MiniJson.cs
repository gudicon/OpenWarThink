using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace MW2.Formats
{
    /// <summary>
    /// A small JSON reader and writer for files written by people (mod manifests): objects become
    /// Dictionary&lt;string, object&gt; (keys compared without case), arrays List&lt;object&gt;, numbers double, plus
    /// string, bool and null. Port addition with no counterpart in the original program; it is free of Unity so the
    /// mod catalog can be checked outside the engine. Errors carry the line and column.
    /// </summary>
    public static class MiniJson
    {
        public sealed class Error : Exception
        {
            public Error(string message) : base(message) { }
        }

        public static object Parse(string text)
        {
            var p = new Reader(text ?? "");
            p.Space();
            var value = p.Value();
            p.Space();
            if (!p.End) throw p.Fail("unexpected text after the value");
            return value;
        }

        sealed class Reader
        {
            readonly string _s;
            int _i;
            public Reader(string s)
            {
                _s = s;
                if (_s.Length > 0 && _s[0] == '﻿') _i = 1;
            }
            public bool End => _i >= _s.Length;

            public Error Fail(string what)
            {
                int line = 1, column = 1;
                for (int k = 0; k < _i && k < _s.Length; k++)
                {
                    if (_s[k] == '\n') { line++; column = 1; }
                    else column++;
                }
                return new Error($"{what} (line {line}, column {column})");
            }

            public void Space()
            {
                while (!End)
                {
                    char c = _s[_i];
                    if (c == ' ' || c == '\t' || c == '\r' || c == '\n') _i++;
                    // Comments are not JSON, but people write them in hand-edited files.
                    else if (c == '/' && _i + 1 < _s.Length && _s[_i + 1] == '/')
                        while (!End && _s[_i] != '\n') _i++;
                    else break;
                }
            }

            public object Value()
            {
                if (End) throw Fail("value expected");
                char c = _s[_i];
                if (c == '{') return Object();
                if (c == '[') return Array();
                if (c == '"') return String();
                if (c == '-' || (c >= '0' && c <= '9')) return Number();
                if (Word("true")) return true;
                if (Word("false")) return false;
                if (Word("null")) return null;
                throw Fail($"unexpected '{c}'");
            }

            bool Word(string w)
            {
                if (string.CompareOrdinal(_s, _i, w, 0, w.Length) != 0) return false;
                _i += w.Length;
                return true;
            }

            Dictionary<string, object> Object()
            {
                var o = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
                _i++;
                Space();
                if (!End && _s[_i] == '}') { _i++; return o; }
                while (true)
                {
                    Space();
                    if (End || _s[_i] != '"') throw Fail("name in quotes expected");
                    string key = String();
                    Space();
                    if (End || _s[_i] != ':') throw Fail("':' expected");
                    _i++;
                    Space();
                    o[key] = Value();
                    Space();
                    if (End) throw Fail("'}' expected");
                    if (_s[_i] == ',') { _i++; continue; }
                    if (_s[_i] == '}') { _i++; return o; }
                    throw Fail("',' or '}' expected");
                }
            }

            List<object> Array()
            {
                var a = new List<object>();
                _i++;
                Space();
                if (!End && _s[_i] == ']') { _i++; return a; }
                while (true)
                {
                    Space();
                    a.Add(Value());
                    Space();
                    if (End) throw Fail("']' expected");
                    if (_s[_i] == ',') { _i++; continue; }
                    if (_s[_i] == ']') { _i++; return a; }
                    throw Fail("',' or ']' expected");
                }
            }

            string String()
            {
                var b = new StringBuilder();
                _i++;
                while (true)
                {
                    if (End) throw Fail("closing '\"' expected");
                    char c = _s[_i++];
                    if (c == '"') return b.ToString();
                    if (c != '\\') { b.Append(c); continue; }
                    if (End) throw Fail("escape expected");
                    char e = _s[_i++];
                    switch (e)
                    {
                        case '"': b.Append('"'); break;
                        case '\\': b.Append('\\'); break;
                        case '/': b.Append('/'); break;
                        case 'b': b.Append('\b'); break;
                        case 'f': b.Append('\f'); break;
                        case 'n': b.Append('\n'); break;
                        case 'r': b.Append('\r'); break;
                        case 't': b.Append('\t'); break;
                        case 'u':
                            if (_i + 4 > _s.Length || !int.TryParse(_s.Substring(_i, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int u))
                                throw Fail("four hex digits expected after \\u");
                            b.Append((char)u);
                            _i += 4;
                            break;
                        default: _i--; throw Fail($"unknown escape '\\{e}'");
                    }
                }
            }

            double Number()
            {
                int start = _i;
                if (_s[_i] == '-') _i++;
                while (!End && "0123456789.eE+-".IndexOf(_s[_i]) >= 0) _i++;
                if (!double.TryParse(_s.Substring(start, _i - start), NumberStyles.Float, CultureInfo.InvariantCulture, out double d))
                {
                    _i = start;
                    throw Fail("bad number");
                }
                return d;
            }
        }

        // ---- reading helpers ----

        public static string Text(Dictionary<string, object> o, string key) =>
            o != null && o.TryGetValue(key, out var v) && v is string s ? s : null;

        public static double? Number(Dictionary<string, object> o, string key) =>
            o != null && o.TryGetValue(key, out var v) && v is double d ? d : (double?)null;

        public static bool? Flag(Dictionary<string, object> o, string key) =>
            o != null && o.TryGetValue(key, out var v) && v is bool b ? b : (bool?)null;

        public static List<object> List(Dictionary<string, object> o, string key) =>
            o != null && o.TryGetValue(key, out var v) ? v as List<object> : null;

        // ---- writing ----

        /// <summary>Writes strings, bools, numbers, null, IDictionary&lt;string, object&gt; and IEnumerable&lt;object&gt;, indented.</summary>
        public static string Write(object value)
        {
            var b = new StringBuilder();
            Write(b, value, 0);
            return b.ToString();
        }

        static void Write(StringBuilder b, object v, int depth)
        {
            switch (v)
            {
                case null: b.Append("null"); return;
                case string s: Quote(b, s); return;
                case bool f: b.Append(f ? "true" : "false"); return;
                case int n: b.Append(n.ToString(CultureInfo.InvariantCulture)); return;
                case double d: b.Append(d.ToString("R", CultureInfo.InvariantCulture)); return;
                case IDictionary<string, object> o:
                {
                    if (o.Count == 0) { b.Append("{}"); return; }
                    b.Append("{\n");
                    int k = 0;
                    foreach (var pair in o)
                    {
                        b.Append(' ', (depth + 1) * 2);
                        Quote(b, pair.Key);
                        b.Append(": ");
                        Write(b, pair.Value, depth + 1);
                        b.Append(++k < o.Count ? ",\n" : "\n");
                    }
                    b.Append(' ', depth * 2).Append('}');
                    return;
                }
                case System.Collections.IEnumerable list:
                {
                    var items = new List<object>();
                    foreach (var item in list) items.Add(item);
                    if (items.Count == 0) { b.Append("[]"); return; }
                    b.Append("[\n");
                    for (int k = 0; k < items.Count; k++)
                    {
                        b.Append(' ', (depth + 1) * 2);
                        Write(b, items[k], depth + 1);
                        b.Append(k + 1 < items.Count ? ",\n" : "\n");
                    }
                    b.Append(' ', depth * 2).Append(']');
                    return;
                }
                default: Quote(b, Convert.ToString(v, CultureInfo.InvariantCulture)); return;
            }
        }

        static void Quote(StringBuilder b, string s)
        {
            b.Append('"');
            foreach (char c in s)
            {
                switch (c)
                {
                    case '"': b.Append("\\\""); break;
                    case '\\': b.Append("\\\\"); break;
                    case '\n': b.Append("\\n"); break;
                    case '\r': b.Append("\\r"); break;
                    case '\t': b.Append("\\t"); break;
                    default:
                        if (c < 0x20) b.Append("\\u").Append(((int)c).ToString("x4"));
                        else b.Append(c);
                        break;
                }
            }
            b.Append('"');
        }
    }
}
