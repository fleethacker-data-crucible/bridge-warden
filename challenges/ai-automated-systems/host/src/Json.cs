// JSON for the C# host. C# 5, framework assemblies only.
// Parse gives the same shapes Python's json module does: Dictionary<string, object> (key order kept),
// List<object>, string, double, bool, null. Write accepts those, any IDictionary / IEnumerable, numbers, enums,
// and IJson objects (the model classes), so the API sends the same JSON the Python server did.
using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace BridgeWarden
{
    // A class that knows its own JSON shape (Models.cs). ToJson returns a dictionary / list / scalar for Json.Write.
    public interface IJson { object ToJson(); }

    public static class Json
    {
        // ------------------------------------------------------------------ write
        public static string Write(object value)
        {
            StringBuilder sb = new StringBuilder();
            WriteValue(sb, value, 0);
            return sb.ToString();
        }

        static void WriteValue(StringBuilder sb, object v, int depth)
        {
            if (depth > 64) throw new InvalidOperationException("JSON nesting too deep");
            if (v == null) { sb.Append("null"); return; }
            string s = v as string;
            if (s != null) { sb.Append(Quote(s)); return; }
            if (v is bool) { sb.Append((bool)v ? "true" : "false"); return; }
            if (v is double || v is float || v is decimal) { sb.Append(Number(Convert.ToDouble(v, CultureInfo.InvariantCulture))); return; }
            if (v is int || v is long || v is short || v is byte || v is uint || v is ulong) { sb.Append(Convert.ToString(v, CultureInfo.InvariantCulture)); return; }
            if (v is Enum) { sb.Append(Quote(v.ToString())); return; }
            if (v is char) { sb.Append(Quote(v.ToString())); return; }
            RawJson raw = v as RawJson;
            if (raw != null) { sb.Append(raw.Text); return; }
            IJson ij = v as IJson;
            if (ij != null) { WriteValue(sb, ij.ToJson(), depth + 1); return; }
            IDictionary dict = v as IDictionary;
            if (dict != null)
            {
                sb.Append('{');
                bool first = true;
                foreach (DictionaryEntry e in dict)
                {
                    if (!first) sb.Append(',');
                    first = false;
                    sb.Append(Quote(Convert.ToString(e.Key, CultureInfo.InvariantCulture))).Append(':');
                    WriteValue(sb, e.Value, depth + 1);
                }
                sb.Append('}');
                return;
            }
            IEnumerable list = v as IEnumerable;
            if (list != null)
            {
                sb.Append('[');
                bool first = true;
                foreach (object o in list)
                {
                    if (!first) sb.Append(',');
                    first = false;
                    WriteValue(sb, o, depth + 1);
                }
                sb.Append(']');
                return;
            }
            sb.Append(Quote(Convert.ToString(v, CultureInfo.InvariantCulture)));
        }

        // Python prints whole floats as 14.0; JavaScript reads both the same. NaN / Infinity are not JSON: null.
        public static string Number(double d)
        {
            if (double.IsNaN(d) || double.IsInfinity(d)) return "null";
            string r = d.ToString("R", CultureInfo.InvariantCulture);
            if (r.IndexOf('.') < 0 && r.IndexOf('E') < 0) r += ".0";
            return r;
        }

        // A JSON string literal, quotes included. Escapes control characters and </ so it is safe inside HTML too.
        public static string Quote(string s)
        {
            if (s == null) return "null";
            StringBuilder sb = new StringBuilder(s.Length + 2);
            sb.Append('"');
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    case '/': sb.Append(i > 0 && s[i - 1] == '<' ? "\\/" : "/"); break;
                    default:
                        if (c < 0x20 || c == (char)0x2028 || c == (char)0x2029) sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                        else sb.Append(c);
                        break;
                }
            }
            sb.Append('"');
            return sb.ToString();
        }

        // Seconds since 1970 (UTC) as a JSON number, the format the UI's timestamps use.
        public static string Epoch(DateTime utc)
        {
            return ((utc - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalSeconds).ToString("0.000", CultureInfo.InvariantCulture);
        }

        // ------------------------------------------------------------------ parse
        public static object Parse(string text)
        {
            if (text == null) throw new FormatException("JSON text is null");
            Parser p = new Parser(text);
            p.Ws();
            object v = p.Value(0);
            p.Ws();
            if (p.Pos != text.Length) throw p.Error("trailing characters");
            return v;
        }

        // Parse, expecting an object (request bodies, config files).
        public static Dictionary<string, object> ParseObject(string text)
        {
            Dictionary<string, object> d = Parse(text) as Dictionary<string, object>;
            if (d == null) throw new FormatException("JSON object expected");
            return d;
        }

        sealed class Parser
        {
            readonly string _s;
            public int Pos;
            public Parser(string s) { _s = s; }

            public FormatException Error(string what) { return new FormatException("Bad JSON at " + Pos + ": " + what); }

            public void Ws() { while (Pos < _s.Length && (_s[Pos] == ' ' || _s[Pos] == '\t' || _s[Pos] == '\n' || _s[Pos] == '\r' || _s[Pos] == (char)0xFEFF)) Pos++; }

            public object Value(int depth)
            {
                if (depth > 64) throw Error("nesting too deep");
                if (Pos >= _s.Length) throw Error("unexpected end");
                char c = _s[Pos];
                if (c == '{') return Obj(depth);
                if (c == '[') return Arr(depth);
                if (c == '"') return Str();
                if (c == 't') { Lit("true"); return true; }
                if (c == 'f') { Lit("false"); return false; }
                if (c == 'n') { Lit("null"); return null; }
                if (c == '-' || (c >= '0' && c <= '9')) return Num();
                throw Error("unexpected '" + c + "'");
            }

            void Lit(string w)
            {
                if (string.CompareOrdinal(_s, Pos, w, 0, w.Length) != 0) throw Error("expected " + w);
                Pos += w.Length;
            }

            Dictionary<string, object> Obj(int depth)
            {
                Dictionary<string, object> d = new Dictionary<string, object>();
                Pos++; Ws();
                if (Pos < _s.Length && _s[Pos] == '}') { Pos++; return d; }
                while (true)
                {
                    Ws();
                    if (Pos >= _s.Length || _s[Pos] != '"') throw Error("expected a key");
                    string k = Str();
                    Ws();
                    if (Pos >= _s.Length || _s[Pos] != ':') throw Error("expected ':'");
                    Pos++; Ws();
                    d[k] = Value(depth + 1);
                    Ws();
                    if (Pos >= _s.Length) throw Error("unexpected end");
                    if (_s[Pos] == ',') { Pos++; continue; }
                    if (_s[Pos] == '}') { Pos++; return d; }
                    throw Error("expected ',' or '}'");
                }
            }

            List<object> Arr(int depth)
            {
                List<object> l = new List<object>();
                Pos++; Ws();
                if (Pos < _s.Length && _s[Pos] == ']') { Pos++; return l; }
                while (true)
                {
                    Ws();
                    l.Add(Value(depth + 1));
                    Ws();
                    if (Pos >= _s.Length) throw Error("unexpected end");
                    if (_s[Pos] == ',') { Pos++; continue; }
                    if (_s[Pos] == ']') { Pos++; return l; }
                    throw Error("expected ',' or ']'");
                }
            }

            string Str()
            {
                Pos++;
                StringBuilder sb = new StringBuilder();
                while (true)
                {
                    if (Pos >= _s.Length) throw Error("unterminated string");
                    char c = _s[Pos++];
                    if (c == '"') return sb.ToString();
                    if (c != '\\') { sb.Append(c); continue; }
                    if (Pos >= _s.Length) throw Error("bad escape");
                    char e = _s[Pos++];
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
                            if (Pos + 4 > _s.Length) throw Error("bad \\u escape");
                            sb.Append((char)int.Parse(_s.Substring(Pos, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture));
                            Pos += 4;
                            break;
                        default: throw Error("bad escape");
                    }
                }
            }

            object Num()
            {
                int start = Pos;
                if (_s[Pos] == '-') Pos++;
                while (Pos < _s.Length && "0123456789.eE+-".IndexOf(_s[Pos]) >= 0) Pos++;
                double d;
                if (!double.TryParse(_s.Substring(start, Pos - start), NumberStyles.Float, CultureInfo.InvariantCulture, out d)) throw Error("bad number");
                return d;
            }
        }
    }

    // Already-serialized JSON to splice in as is (a cached state snapshot, a model's raw reply object).
    public sealed class RawJson
    {
        public readonly string Text;
        public RawJson(string text) { Text = text; }
    }
}
