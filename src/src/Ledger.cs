// Port of warden/ledger.py.
// Append-only, hash-chained audit ledger (SQLite).
//
// Each row stores hash = sha256(prev_hash + payload), so editing or deleting any
// row breaks every hash after it. verify() walks the chain to prove it's intact.
//
// Python used the sqlite3 module; the host uses the SQLite built into Windows (winsqlite3.dll) through P/Invoke.
// This is the only file allowed DllImport, and only for winsqlite3.dll (host\Build.ps1).
using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace BridgeWarden
{
    public sealed class Ledger : IDisposable
    {
        public const string GENESIS = "0000000000000000000000000000000000000000000000000000000000000000";

        IntPtr _db = IntPtr.Zero;
        readonly object _lock = new object();

        // path null = backend\data\ledger.db
        public Ledger(string path = null)
        {
            if (string.IsNullOrEmpty(path)) path = System.IO.Path.Combine(Paths.Data, "ledger.db");
            path = System.IO.Path.GetFullPath(path);
            string dir = System.IO.Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            IntPtr db;
            int rc = Sqlite.sqlite3_open_v2(Sqlite.Utf8z(path), out db, Sqlite.OPEN_READWRITE | Sqlite.OPEN_CREATE | Sqlite.OPEN_FULLMUTEX, IntPtr.Zero);
            if (rc != Sqlite.OK)
            {
                string msg = db != IntPtr.Zero ? Sqlite.ErrMsg(db) : ("sqlite error " + rc);
                if (db != IntPtr.Zero) Sqlite.sqlite3_close(db);
                throw new IOException("Cannot open ledger " + path + ": " + msg);
            }
            _db = db;
            Exec(@"CREATE TABLE IF NOT EXISTS ledger (
            seq INTEGER PRIMARY KEY, ts REAL, type TEXT, actor TEXT,
            payload TEXT, prev_hash TEXT, hash TEXT)");
        }

        public void Close()
        {
            lock (_lock)
            {
                if (_db != IntPtr.Zero) { Sqlite.sqlite3_close(_db); _db = IntPtr.Zero; }
            }
        }

        public void Dispose() { Close(); }

        public void Reset()
        {
            lock (_lock)
            {
                Exec("DELETE FROM ledger");
            }
        }

        // Caller holds _lock (or is the constructor).
        string LastHash()
        {
            string h = null;
            Query("SELECT hash FROM ledger ORDER BY seq DESC LIMIT 1", null, delegate(IntPtr st) { h = Sqlite.ColText(st, 0); });
            return h ?? GENESIS;
        }

        public static string Digest(string prev_hash, double ts, string type_, string actor, string payload)
        {
            string s = prev_hash + "|" + ts.ToString("F6", CultureInfo.InvariantCulture) + "|" + type_ + "|" + actor + "|" + payload;
            using (SHA256 sha = SHA256.Create())
            {
                byte[] d = sha.ComputeHash(Encoding.UTF8.GetBytes(s));
                StringBuilder sb = new StringBuilder(64);
                foreach (byte b in d) sb.Append(b.ToString("x2", CultureInfo.InvariantCulture));
                return sb.ToString();
            }
        }

        public Dictionary<string, object> Append(string type_, string actor, object payload)
        {
            double ts = Py.Now();
            string body = PyJson.Dumps(payload, true);
            string h;
            long seq;
            lock (_lock)
            {
                string prev = LastHash();
                h = Digest(prev, ts, type_, actor, body);
                Query("INSERT INTO ledger (ts, type, actor, payload, prev_hash, hash) VALUES (?,?,?,?,?,?)",
                      new object[] { ts, type_, actor, body, prev, h }, null);
                seq = Sqlite.sqlite3_last_insert_rowid(_db);
            }
            return Py.D("seq", seq, "ts", ts, "type", type_, "actor", actor, "payload", payload, "hash", h);
        }

        public List<object> Rows(int limit = 200, string type_ = null)
        {
            string q = "SELECT seq, ts, type, actor, payload, hash FROM ledger";
            List<object> args = new List<object>();
            if (!string.IsNullOrEmpty(type_))
            {
                q += " WHERE type = ?";
                args.Add(type_);
            }
            q += " ORDER BY seq DESC LIMIT ?";
            args.Add((long)limit);
            List<object[]> rows = new List<object[]>();
            lock (_lock)
            {
                Query(q, args.ToArray(), delegate(IntPtr st)
                {
                    rows.Add(new object[] { Sqlite.sqlite3_column_int64(st, 0), Sqlite.sqlite3_column_double(st, 1),
                        Sqlite.ColText(st, 2), Sqlite.ColText(st, 3), Sqlite.ColText(st, 4), Sqlite.ColText(st, 5) });
                });
            }
            rows.Reverse();
            List<object> result = new List<object>();
            foreach (object[] r in rows)
                result.Add(Py.D("seq", r[0], "ts", r[1], "type", r[2], "actor", r[3], "payload", Json.Parse((string)r[4]), "hash", r[5]));
            return result;
        }

        public Dictionary<string, object> Verify()
        {
            List<object[]> rows = new List<object[]>();
            lock (_lock)
            {
                Query("SELECT seq, ts, type, actor, payload, prev_hash, hash FROM ledger ORDER BY seq", null, delegate(IntPtr st)
                {
                    rows.Add(new object[] { Sqlite.sqlite3_column_int64(st, 0), Sqlite.sqlite3_column_double(st, 1),
                        Sqlite.ColText(st, 2), Sqlite.ColText(st, 3), Sqlite.ColText(st, 4), Sqlite.ColText(st, 5), Sqlite.ColText(st, 6) });
                });
            }
            string prev = GENESIS;
            foreach (object[] r in rows)
            {
                string prev_hash = (string)r[5], h = (string)r[6];
                if (prev_hash != prev || Digest(prev, (double)r[1], (string)r[2], (string)r[3], (string)r[4]) != h)
                    return Py.D("valid", false, "rows", rows.Count, "broken_at", r[0]);
                prev = h;
            }
            return Py.D("valid", true, "rows", rows.Count, "broken_at", null);
        }

        // ------------------------------------------------------------------ SQLite plumbing
        void Exec(string sql) { Query(sql, null, null); }

        // Prepare, bind (double, long/int, string, null), step every row through onRow, finalize.
        void Query(string sql, object[] args, Action<IntPtr> onRow)
        {
            if (_db == IntPtr.Zero) throw new ObjectDisposedException("Ledger");
            IntPtr st, tail;
            byte[] q = Sqlite.Utf8z(sql);
            if (Sqlite.sqlite3_prepare_v2(_db, q, q.Length, out st, out tail) != Sqlite.OK)
                throw new IOException("SQLite prepare failed: " + Sqlite.ErrMsg(_db));
            try
            {
                if (args != null)
                {
                    for (int i = 0; i < args.Length; i++)
                    {
                        object a = args[i];
                        int rc;
                        if (a == null) rc = Sqlite.sqlite3_bind_null(st, i + 1);
                        else if (a is double) rc = Sqlite.sqlite3_bind_double(st, i + 1, (double)a);
                        else if (a is long || a is int) rc = Sqlite.sqlite3_bind_int64(st, i + 1, Convert.ToInt64(a, CultureInfo.InvariantCulture));
                        else
                        {
                            byte[] b = Encoding.UTF8.GetBytes(Convert.ToString(a, CultureInfo.InvariantCulture));
                            rc = Sqlite.sqlite3_bind_text(st, i + 1, b, b.Length, Sqlite.TRANSIENT);
                        }
                        if (rc != Sqlite.OK) throw new IOException("SQLite bind failed: " + Sqlite.ErrMsg(_db));
                    }
                }
                while (true)
                {
                    int rc = Sqlite.sqlite3_step(st);
                    if (rc == Sqlite.ROW) { if (onRow != null) onRow(st); continue; }
                    if (rc == Sqlite.DONE) break;
                    throw new IOException("SQLite step failed: " + Sqlite.ErrMsg(_db));
                }
            }
            finally
            {
                Sqlite.sqlite3_finalize(st);
            }
        }

        static class Sqlite
        {
            public const int OK = 0, ROW = 100, DONE = 101;
            public const int OPEN_READWRITE = 0x2, OPEN_CREATE = 0x4, OPEN_FULLMUTEX = 0x10000;
            public static readonly IntPtr TRANSIENT = new IntPtr(-1);
            const string Dll = "winsqlite3.dll";

            [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.StdCall)]
            public static extern int sqlite3_open_v2(byte[] filename, out IntPtr db, int flags, IntPtr vfs);
            [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.StdCall)]
            public static extern int sqlite3_close(IntPtr db);
            [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.StdCall)]
            public static extern int sqlite3_prepare_v2(IntPtr db, byte[] sql, int nbyte, out IntPtr stmt, out IntPtr tail);
            [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.StdCall)]
            public static extern int sqlite3_step(IntPtr stmt);
            [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.StdCall)]
            public static extern int sqlite3_finalize(IntPtr stmt);
            [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.StdCall)]
            public static extern int sqlite3_bind_text(IntPtr stmt, int idx, byte[] text, int n, IntPtr destructor);
            [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.StdCall)]
            public static extern int sqlite3_bind_double(IntPtr stmt, int idx, double v);
            [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.StdCall)]
            public static extern int sqlite3_bind_int64(IntPtr stmt, int idx, long v);
            [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.StdCall)]
            public static extern int sqlite3_bind_null(IntPtr stmt, int idx);
            [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.StdCall)]
            public static extern long sqlite3_column_int64(IntPtr stmt, int col);
            [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.StdCall)]
            public static extern double sqlite3_column_double(IntPtr stmt, int col);
            [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.StdCall)]
            public static extern IntPtr sqlite3_column_text(IntPtr stmt, int col);
            [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.StdCall)]
            public static extern int sqlite3_column_bytes(IntPtr stmt, int col);
            [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.StdCall)]
            public static extern long sqlite3_last_insert_rowid(IntPtr db);
            [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.StdCall)]
            public static extern IntPtr sqlite3_errmsg(IntPtr db);

            public static byte[] Utf8z(string s)
            {
                byte[] b = Encoding.UTF8.GetBytes(s);
                byte[] z = new byte[b.Length + 1];
                Buffer.BlockCopy(b, 0, z, 0, b.Length);
                return z;
            }

            static string FromUtf8z(IntPtr p, int n)
            {
                if (p == IntPtr.Zero) return null;
                if (n < 0) { n = 0; while (Marshal.ReadByte(p, n) != 0) n++; }
                byte[] b = new byte[n];
                Marshal.Copy(p, b, 0, n);
                return Encoding.UTF8.GetString(b);
            }

            // NULL column -> null (Python None)
            public static string ColText(IntPtr st, int col)
            {
                IntPtr p = sqlite3_column_text(st, col);
                if (p == IntPtr.Zero) return null;
                return FromUtf8z(p, sqlite3_column_bytes(st, col));
            }

            public static string ErrMsg(IntPtr db) { return FromUtf8z(sqlite3_errmsg(db), -1); }
        }
    }

    // json.dumps(obj, sort_keys=..., indent=..., default=str) with Python's default formatting: ", " and ": "
    // separators (or "," + newline with indent), ensure_ascii escapes, floats as repr(). The ledger digests and the
    // LLM cache keys hash this text, so it must be stable; cache keys also match the Python backend's.
    public static class PyJson
    {
        static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        public static string Dumps(object o, bool sort_keys = false, int indent = -1)
        {
            StringBuilder sb = new StringBuilder();
            Write(sb, o, sort_keys, indent, 0);
            return sb.ToString();
        }

        static void Newline(StringBuilder sb, int indent, int level)
        {
            sb.Append('\n').Append(' ', indent * level);
        }

        static void Write(StringBuilder sb, object v, bool sortKeys, int indent, int level)
        {
            if (level > 64) throw new InvalidOperationException("JSON nesting too deep");
            if (v == null) { sb.Append("null"); return; }
            string s = v as string;
            if (s != null) { Quote(sb, s); return; }
            if (v is bool) { sb.Append((bool)v ? "true" : "false"); return; }
            if (v is double || v is float || v is decimal) { sb.Append(Float(Convert.ToDouble(v, Inv))); return; }
            if (v is int || v is long || v is short || v is byte || v is uint || v is ulong || v is sbyte || v is ushort)
            { sb.Append(Convert.ToString(v, Inv)); return; }
            RawJson raw = v as RawJson;
            if (raw != null) { sb.Append(raw.Text); return; }
            IJson ij = v as IJson;
            if (ij != null) { Write(sb, ij.ToJson(), sortKeys, indent, level); return; }
            IDictionary dict = v as IDictionary;
            if (dict != null)
            {
                if (dict.Count == 0) { sb.Append("{}"); return; }
                List<KeyValuePair<string, object>> items = new List<KeyValuePair<string, object>>();
                foreach (DictionaryEntry e in dict) items.Add(new KeyValuePair<string, object>(KeyStr(e.Key), e.Value));
                if (sortKeys) items.Sort(delegate(KeyValuePair<string, object> a, KeyValuePair<string, object> b) { return string.CompareOrdinal(a.Key, b.Key); });
                sb.Append('{');
                bool first = true;
                foreach (KeyValuePair<string, object> kv in items)
                {
                    if (!first) sb.Append(indent >= 0 ? "," : ", ");
                    first = false;
                    if (indent >= 0) Newline(sb, indent, level + 1);
                    Quote(sb, kv.Key);
                    sb.Append(": ");
                    Write(sb, kv.Value, sortKeys, indent, level + 1);
                }
                if (indent >= 0) Newline(sb, indent, level);
                sb.Append('}');
                return;
            }
            IEnumerable list = v as IEnumerable;
            if (list != null)
            {
                List<object> xs = new List<object>();
                foreach (object x in list) xs.Add(x);
                if (xs.Count == 0) { sb.Append("[]"); return; }
                sb.Append('[');
                bool first = true;
                foreach (object x in xs)
                {
                    if (!first) sb.Append(indent >= 0 ? "," : ", ");
                    first = false;
                    if (indent >= 0) Newline(sb, indent, level + 1);
                    Write(sb, x, sortKeys, indent, level + 1);
                }
                if (indent >= 0) Newline(sb, indent, level);
                sb.Append(']');
                return;
            }
            Quote(sb, Py.Str(v));   // default=str
        }

        static string KeyStr(object k)
        {
            string s = k as string;
            if (s != null) return s;
            if (k is bool) return (bool)k ? "true" : "false";
            if (k == null) return "null";
            if (k is double) return Float((double)k);
            return Convert.ToString(k, Inv);
        }

        // ensure_ascii=True
        static void Quote(StringBuilder sb, string s)
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
                    case '\b': sb.Append("\\b"); break;
                    case '\f': sb.Append("\\f"); break;
                    default:
                        if (c < 0x20 || c > 0x7e && c != (char)0x7f) sb.Append("\\u").Append(((int)c).ToString("x4", Inv));
                        else sb.Append(c);
                        break;
                }
            }
            sb.Append('"');
        }

        // repr(float): shortest round-trip digits, fixed notation for 1e-4 <= |x| < 1e16, else 1.5e+16 style.
        public static string Float(double d)
        {
            if (double.IsNaN(d)) return "NaN";
            if (double.IsPositiveInfinity(d)) return "Infinity";
            if (double.IsNegativeInfinity(d)) return "-Infinity";
            string r = d.ToString("R", Inv);
            bool neg = r.StartsWith("-");
            if (neg) r = r.Substring(1);
            int exp = 0;
            int ei = r.IndexOfAny(new char[] { 'E', 'e' });
            if (ei >= 0) { exp = int.Parse(r.Substring(ei + 1), NumberStyles.AllowLeadingSign, Inv); r = r.Substring(0, ei); }
            int dot = r.IndexOf('.');
            int intLen = dot < 0 ? r.Length : dot;
            string digits = r.Replace(".", "");
            int lz = 0;
            while (lz < digits.Length && digits[lz] == '0') lz++;
            digits = digits.Substring(lz).TrimEnd('0');
            string sign = neg ? "-" : "";
            if (digits.Length == 0) return sign + "0.0";
            int e10 = intLen - lz - 1 + exp;
            string body;
            if (e10 >= -4 && e10 < 16)
            {
                if (e10 >= 0)
                {
                    string ip = digits.Length > e10 + 1 ? digits.Substring(0, e10 + 1) : digits.PadRight(e10 + 1, '0');
                    string fp = digits.Length > e10 + 1 ? digits.Substring(e10 + 1) : "0";
                    body = ip + "." + fp;
                }
                else body = "0." + new string('0', -e10 - 1) + digits;
            }
            else
            {
                body = digits.Substring(0, 1) + (digits.Length > 1 ? "." + digits.Substring(1) : "")
                       + "e" + (e10 < 0 ? "-" : "+") + Math.Abs(e10).ToString("00", Inv);
            }
            return sign + body;
        }
    }
}
