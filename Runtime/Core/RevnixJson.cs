using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Revnix
{
    /// <summary>
    /// Minimal dependency-free JSON for the SDK's small payloads. Parses into
    /// Dictionary&lt;string, object&gt; / List&lt;object&gt; / string / long / double /
    /// bool / null (integral numbers land as long — ledger cursors are 64-bit).
    /// UnityEngine.JsonUtility can't represent optional fields or dictionaries,
    /// and forcing a Newtonsoft dependency on every consumer is not worth two
    /// hundred lines.
    /// </summary>
    public static class RevnixJson
    {
        /// <summary>Parse JSON text. Throws FormatException on malformed input.</summary>
        public static object Parse(string text)
        {
            if (text == null) throw new FormatException("null JSON input");
            var pos = 0;
            var value = ParseValue(text, ref pos);
            SkipWhitespace(text, ref pos);
            if (pos != text.Length) throw new FormatException("trailing characters at " + pos);
            return value;
        }

        public static Dictionary<string, object> ParseObject(string text)
        {
            return Parse(text) as Dictionary<string, object>
                ?? throw new FormatException("expected a JSON object");
        }

        public static string Serialize(object value)
        {
            var sb = new StringBuilder();
            Write(sb, value);
            return sb.ToString();
        }

        // ── Reading helpers (models use these; missing key = default) ────────

        public static string GetString(Dictionary<string, object> map, string key, string fallback = null)
            => map != null && map.TryGetValue(key, out var v) && v is string s ? s : fallback;

        public static bool GetBool(Dictionary<string, object> map, string key, bool fallback = false)
            => map != null && map.TryGetValue(key, out var v) && v is bool b ? b : fallback;

        public static bool? GetNullableBool(Dictionary<string, object> map, string key)
            => map != null && map.TryGetValue(key, out var v) && v is bool b ? b : (bool?)null;

        public static long GetLong(Dictionary<string, object> map, string key, long fallback = 0)
        {
            if (map == null || !map.TryGetValue(key, out var v)) return fallback;
            if (v is long l) return l;
            if (v is double d) return (long)d;
            return fallback;
        }

        public static long? GetNullableLong(Dictionary<string, object> map, string key)
        {
            if (map == null || !map.TryGetValue(key, out var v)) return null;
            if (v is long l) return l;
            if (v is double d) return (long)d;
            return null;
        }

        public static double? GetNullableDouble(Dictionary<string, object> map, string key)
        {
            if (map == null || !map.TryGetValue(key, out var v)) return null;
            if (v is double d) return d;
            if (v is long l) return l;
            return null;
        }

        public static List<object> GetList(Dictionary<string, object> map, string key)
            => map != null && map.TryGetValue(key, out var v) && v is List<object> list
                ? list
                : new List<object>();

        public static Dictionary<string, object> GetObject(Dictionary<string, object> map, string key)
            => map != null && map.TryGetValue(key, out var v) ? v as Dictionary<string, object> : null;

        // ── Parser ───────────────────────────────────────────────────────────

        private static object ParseValue(string s, ref int pos)
        {
            SkipWhitespace(s, ref pos);
            if (pos >= s.Length) throw new FormatException("unexpected end of JSON");
            var c = s[pos];
            switch (c)
            {
                case '{': return ParseObjectBody(s, ref pos);
                case '[': return ParseArrayBody(s, ref pos);
                case '"': return ParseString(s, ref pos);
                case 't': Expect(s, ref pos, "true"); return true;
                case 'f': Expect(s, ref pos, "false"); return false;
                case 'n': Expect(s, ref pos, "null"); return null;
                default: return ParseNumber(s, ref pos);
            }
        }

        private static Dictionary<string, object> ParseObjectBody(string s, ref int pos)
        {
            var result = new Dictionary<string, object>();
            pos++; // '{'
            SkipWhitespace(s, ref pos);
            if (pos < s.Length && s[pos] == '}') { pos++; return result; }
            while (true)
            {
                SkipWhitespace(s, ref pos);
                if (pos >= s.Length || s[pos] != '"') throw new FormatException("expected object key at " + pos);
                var key = ParseString(s, ref pos);
                SkipWhitespace(s, ref pos);
                if (pos >= s.Length || s[pos] != ':') throw new FormatException("expected ':' at " + pos);
                pos++;
                result[key] = ParseValue(s, ref pos);
                SkipWhitespace(s, ref pos);
                if (pos >= s.Length) throw new FormatException("unterminated object");
                if (s[pos] == ',') { pos++; continue; }
                if (s[pos] == '}') { pos++; return result; }
                throw new FormatException("expected ',' or '}' at " + pos);
            }
        }

        private static List<object> ParseArrayBody(string s, ref int pos)
        {
            var result = new List<object>();
            pos++; // '['
            SkipWhitespace(s, ref pos);
            if (pos < s.Length && s[pos] == ']') { pos++; return result; }
            while (true)
            {
                result.Add(ParseValue(s, ref pos));
                SkipWhitespace(s, ref pos);
                if (pos >= s.Length) throw new FormatException("unterminated array");
                if (s[pos] == ',') { pos++; continue; }
                if (s[pos] == ']') { pos++; return result; }
                throw new FormatException("expected ',' or ']' at " + pos);
            }
        }

        private static string ParseString(string s, ref int pos)
        {
            pos++; // opening quote
            var sb = new StringBuilder();
            while (true)
            {
                if (pos >= s.Length) throw new FormatException("unterminated string");
                var c = s[pos++];
                if (c == '"') return sb.ToString();
                if (c != '\\') { sb.Append(c); continue; }
                if (pos >= s.Length) throw new FormatException("unterminated escape");
                var esc = s[pos++];
                switch (esc)
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
                        if (pos + 4 > s.Length) throw new FormatException("bad unicode escape");
                        sb.Append((char)ushort.Parse(s.Substring(pos, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture));
                        pos += 4;
                        break;
                    default: throw new FormatException("bad escape '\\" + esc + "'");
                }
            }
        }

        private static object ParseNumber(string s, ref int pos)
        {
            var start = pos;
            if (pos < s.Length && s[pos] == '-') pos++;
            while (pos < s.Length && (char.IsDigit(s[pos]) || s[pos] == '.' || s[pos] == 'e' || s[pos] == 'E' || s[pos] == '+' || s[pos] == '-'))
                pos++;
            var slice = s.Substring(start, pos - start);
            if (slice.IndexOf('.') < 0 && slice.IndexOf('e') < 0 && slice.IndexOf('E') < 0
                && long.TryParse(slice, NumberStyles.Integer, CultureInfo.InvariantCulture, out var l))
                return l;
            if (double.TryParse(slice, NumberStyles.Float, CultureInfo.InvariantCulture, out var d))
                return d;
            throw new FormatException("bad number '" + slice + "' at " + start);
        }

        private static void Expect(string s, ref int pos, string literal)
        {
            if (pos + literal.Length > s.Length || s.Substring(pos, literal.Length) != literal)
                throw new FormatException("bad literal at " + pos);
            pos += literal.Length;
        }

        private static void SkipWhitespace(string s, ref int pos)
        {
            while (pos < s.Length && (s[pos] == ' ' || s[pos] == '\t' || s[pos] == '\n' || s[pos] == '\r'))
                pos++;
        }

        // ── Writer ───────────────────────────────────────────────────────────

        private static void Write(StringBuilder sb, object value)
        {
            switch (value)
            {
                case null: sb.Append("null"); break;
                case bool b: sb.Append(b ? "true" : "false"); break;
                case string s: WriteString(sb, s); break;
                case long l: sb.Append(l.ToString(CultureInfo.InvariantCulture)); break;
                case int i: sb.Append(i.ToString(CultureInfo.InvariantCulture)); break;
                case double d: sb.Append(d.ToString("R", CultureInfo.InvariantCulture)); break;
                case float f: sb.Append(((double)f).ToString("R", CultureInfo.InvariantCulture)); break;
                case Dictionary<string, object> map:
                {
                    sb.Append('{');
                    var first = true;
                    foreach (var pair in map)
                    {
                        if (!first) sb.Append(',');
                        first = false;
                        WriteString(sb, pair.Key);
                        sb.Append(':');
                        Write(sb, pair.Value);
                    }
                    sb.Append('}');
                    break;
                }
                case List<object> list:
                {
                    sb.Append('[');
                    for (var i = 0; i < list.Count; i++)
                    {
                        if (i > 0) sb.Append(',');
                        Write(sb, list[i]);
                    }
                    sb.Append(']');
                    break;
                }
                default:
                    throw new ArgumentException("unsupported JSON value type: " + value.GetType().Name);
            }
        }

        private static void WriteString(StringBuilder sb, string s)
        {
            sb.Append('"');
            foreach (var c in s)
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
                        if (c < 0x20)
                            sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                        else
                            sb.Append(c);
                        break;
                }
            }
            sb.Append('"');
        }
    }
}
