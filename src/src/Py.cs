// Helpers that let the C# port read like the Python it came from (warden\*.py). C# 5, framework assemblies only.
// The Python keeps most state in plain dicts and lists; so does the port, as Dictionary<string, object> and
// List<object>, so the JSON the UI gets is unchanged. Numbers read from JSON are double; Py.Num / Py.Int convert.
using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Threading;

namespace BridgeWarden
{
    public static class Py
    {
        static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        // ------------------------------------------------------------------ building
        // Py.D("a", 1, "b", "x") -> {"a": 1, "b": "x"} in that order.
        public static Dictionary<string, object> D(params object[] kv)
        {
            if (kv.Length % 2 != 0) throw new ArgumentException("Py.D needs key, value pairs");
            Dictionary<string, object> d = new Dictionary<string, object>();
            for (int i = 0; i < kv.Length; i += 2) d[(string)kv[i]] = kv[i + 1];
            return d;
        }

        public static List<object> L(params object[] items) { return new List<object>(items); }

        public static List<object> ToList<T>(IEnumerable<T> items)
        {
            List<object> l = new List<object>();
            foreach (T t in items) l.Add(t);
            return l;
        }

        // ------------------------------------------------------------------ reading (Python dict.get semantics)
        public static object Get(IDictionary<string, object> d, string key) { object v; return d != null && d.TryGetValue(key, out v) ? v : null; }
        public static object Get(IDictionary<string, object> d, string key, object dflt) { object v; return d != null && d.TryGetValue(key, out v) ? v : dflt; }

        public static Dictionary<string, object> Dict(object o) { return o as Dictionary<string, object>; }
        public static Dictionary<string, object> DictOrEmpty(object o) { return (o as Dictionary<string, object>) ?? new Dictionary<string, object>(); }
        public static List<object> List(object o)
        {
            List<object> l = o as List<object>;
            if (l != null || o == null || o is string) return l;
            IEnumerable e = o as IEnumerable;
            if (e == null || o is IDictionary) return null;
            l = new List<object>();
            foreach (object x in e) l.Add(x);
            return l;
        }
        public static List<object> ListOrEmpty(object o) { return List(o) ?? new List<object>(); }

        public static bool IsNumber(object o) { return o is double || o is int || o is long || o is float || o is decimal || o is short; }

        // float(x): numbers, numeric strings and bools. Anything else throws, like Python.
        public static double Num(object o)
        {
            if (o is double) return (double)o;
            if (o is bool) return (bool)o ? 1 : 0;
            if (IsNumber(o)) return Convert.ToDouble(o, Inv);
            string s = o as string;
            double d;
            if (s != null && double.TryParse(s.Trim(), NumberStyles.Float, Inv, out d)) return d;
            throw new FormatException("not a number: " + Repr(o));
        }
        public static double Num(object o, double dflt) { try { return o == null ? dflt : Num(o); } catch (FormatException) { return dflt; } }
        public static bool TryNum(object o, out double d) { try { d = Num(o); return true; } catch (FormatException) { d = 0; return false; } }

        // int(x): truncates toward zero, like Python.
        public static int Int(object o) { return (int)Math.Truncate(Num(o)); }
        public static int Int(object o, int dflt) { try { return o == null ? dflt : Int(o); } catch (FormatException) { return dflt; } }

        // str(x) for the values these dicts hold.
        public static string Str(object o)
        {
            if (o == null) return "None";
            string s = o as string;
            if (s != null) return s;
            if (o is bool) return (bool)o ? "True" : "False";
            if (o is double) return PyFloat((double)o);
            if (IsNumber(o)) return Convert.ToString(o, Inv);
            return Repr(o);
        }
        public static string StrOr(object o, string dflt) { return o == null ? dflt : Str(o); }

        // Python truthiness: None, False, 0, "", empty containers are false.
        public static bool Truthy(object o)
        {
            if (o == null) return false;
            if (o is bool) return (bool)o;
            if (IsNumber(o)) return Num(o) != 0;
            string s = o as string;
            if (s != null) return s.Length > 0;
            ICollection c = o as ICollection;
            if (c != null) return c.Count > 0;
            return true;
        }

        // repr() close enough for messages and prompts: dicts and lists as Python prints them.
        public static string Repr(object o)
        {
            if (o == null) return "None";
            string s = o as string;
            if (s != null) return "'" + s.Replace("\\", "\\\\").Replace("'", "\\'") + "'";
            if (o is bool || IsNumber(o)) return Str(o);
            IDictionary d = o as IDictionary;
            if (d != null)
            {
                StringBuilder sb = new StringBuilder("{");
                bool first = true;
                foreach (DictionaryEntry e in d) { if (!first) sb.Append(", "); first = false; sb.Append(Repr(e.Key)).Append(": ").Append(Repr(e.Value)); }
                return sb.Append('}').ToString();
            }
            IEnumerable l = o as IEnumerable;
            if (l != null)
            {
                StringBuilder sb = new StringBuilder("[");
                bool first = true;
                foreach (object x in l) { if (!first) sb.Append(", "); first = false; sb.Append(Repr(x)); }
                return sb.Append(']').ToString();
            }
            return o.ToString();
        }

        // ------------------------------------------------------------------ numbers and text
        // round(x, n): banker's rounding, like Python 3.
        public static double Round(double x, int digits) { return Math.Round(x, digits, MidpointRounding.ToEven); }
        public static double Round(double x) { return Math.Round(x, 0, MidpointRounding.ToEven); }

        // f"{x:.Nf}"
        public static string F(double x, int decimals) { return x.ToString("F" + decimals, Inv); }

        // str(float): 14.0, 0.25, 1e-05 style kept simple: shortest round-trip, with ".0" on whole numbers.
        public static string PyFloat(double d)
        {
            if (double.IsNaN(d)) return "nan";
            if (double.IsInfinity(d)) return d > 0 ? "inf" : "-inf";
            string r = d.ToString("R", Inv);
            if (r.IndexOf('.') < 0 && r.IndexOf('E') < 0) r += ".0";
            return r;
        }

        public static string Join(string sep, IEnumerable items)
        {
            StringBuilder sb = new StringBuilder();
            bool first = true;
            foreach (object o in items) { if (!first) sb.Append(sep); first = false; sb.Append(Str(o)); }
            return sb.ToString();
        }

        public static string Cut(string s, int max) { return s == null ? "" : (s.Length <= max ? s : s.Substring(0, max)); }

        // ------------------------------------------------------------------ copies
        public static object DeepCopy(object o)
        {
            IDictionary<string, object> d = o as IDictionary<string, object>;
            if (d != null)
            {
                Dictionary<string, object> c = new Dictionary<string, object>();
                foreach (KeyValuePair<string, object> kv in d) c[kv.Key] = DeepCopy(kv.Value);
                return c;
            }
            List<object> l = o as List<object>;
            if (l != null)
            {
                List<object> c = new List<object>(l.Count);
                foreach (object x in l) c.Add(DeepCopy(x));
                return c;
            }
            return o;
        }

        // ------------------------------------------------------------------ time and ids
        static readonly DateTime EpochStart = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        // time.time(). FakeTime > 0 freezes the clock at that value (tests: the pytest FakeClock monkeypatch).
        public static double FakeTime = 0.0;
        public static double Now() { double f = FakeTime; return f > 0 ? f : (DateTime.UtcNow - EpochStart).TotalSeconds; }

        // uuid.uuid4().hex[:n]
        public static string Hex(int n) { return Guid.NewGuid().ToString("N").Substring(0, n); }

        // time.perf_counter() in seconds
        static readonly System.Diagnostics.Stopwatch Clock = System.Diagnostics.Stopwatch.StartNew();
        public static double Perf() { return Clock.Elapsed.TotalSeconds; }

        // One shared random source per thread for code that doesn't need a seed.
        static readonly ThreadLocal<Random> Rng = new ThreadLocal<Random>(delegate { return new Random(Guid.NewGuid().GetHashCode()); });
        public static Random Random { get { return Rng.Value; } }
    }
}
