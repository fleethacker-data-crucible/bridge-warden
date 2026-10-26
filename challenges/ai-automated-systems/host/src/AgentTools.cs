// Calculator tools the LLM agents may call while deciding (port of warden\agent_tools.py). C# 5, framework assemblies only.
//
// These are pure math over facts already on the COP. They never recommend, rank or choose anything: the model decides
// WHICH calculation to run, reads the numbers, and makes the decision. Every call and result is recorded in the
// decision's provenance.
using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace BridgeWarden
{
    // A Python exception (ValueError, TypeError, ZeroDivisionError ...): only its str() text matters to run_tool.
    public sealed class PyError : Exception
    {
        public PyError(string message) : base(message) { }
    }

    public static class AgentTools
    {
        public const string NOT_ALLOWED = "only numbers, + - * / ** %, and math functions are allowed";

        // ------------------------------------------------------------------ safe arithmetic (Python: ast + _safe_eval)
        // Supports exactly what the Python allows: int/float literals (True/False count as ints), pi, + - * / ** %,
        // unary + -, parentheses, and calls to the _FUNCS names. Anything else that Python would PARSE becomes a
        // node that raises NOT_ALLOWED when evaluated (so syntax errors still win, like ast.parse first).
        static readonly string[] FUNCS = { "sqrt", "sin", "cos", "tan", "atan2", "radians", "degrees", "exp", "log",
                                           "hypot", "floor", "ceil", "abs", "min", "max", "round" };

        // A Python number: int or float. Ints are kept in a double (exact up to 2**53, inf when too large).
        struct Num
        {
            public double v;
            public bool isInt;
            public Num(double v, bool isInt) { this.v = v; this.isInt = isInt; }
        }

        abstract class Node { public abstract Num Eval(); }

        sealed class Const : Node
        {
            readonly Num _n;
            public Const(Num n) { _n = n; }
            public override Num Eval() { return _n; }
        }

        // Valid Python, but not on the allow list (names, attributes, strings, lists, comparisons, // ...).
        sealed class Reject : Node
        {
            public override Num Eval() { throw new PyError(NOT_ALLOWED); }
        }

        sealed class Bin : Node
        {
            readonly string _op;
            readonly Node _l, _r;
            public Bin(string op, Node l, Node r) { _op = op; _l = l; _r = r; }
            public override Num Eval() { Num a = _l.Eval(); Num b = _r.Eval(); return BinOp(_op, a, b); }
        }

        sealed class Unary : Node
        {
            readonly string _op;
            readonly Node _x;
            public Unary(string op, Node x) { _op = op; _x = x; }
            public override Num Eval() { Num a = _x.Eval(); return new Num(_op == "-" ? -a.v : a.v, a.isInt); }
        }

        sealed class Call : Node
        {
            readonly string _fn;
            readonly List<Node> _args;
            public Call(string fn, List<Node> args) { _fn = fn; _args = args; }
            public override Num Eval()
            {
                List<Num> a = new List<Num>();
                foreach (Node n in _args) a.Add(n.Eval());
                return CallFn(_fn, a);
            }
        }

        static Num BinOp(string op, Num a, Num b)
        {
            bool ints = a.isInt && b.isInt;
            switch (op)
            {
                case "+": return new Num(a.v + b.v, ints);
                case "-": return new Num(a.v - b.v, ints);
                case "*": return new Num(a.v * b.v, ints);
                case "/":
                    if (b.v == 0) throw new PyError(ints ? "division by zero" : "float division by zero");
                    return new Num(a.v / b.v, false);
                case "%":
                    if (b.v == 0) throw new PyError(ints ? "integer modulo by zero" : "float modulo");
                    return new Num(WorldMath.PyMod(a.v, b.v), ints);
                case "**":
                    if (ints && b.v >= 0) return new Num(Math.Pow(a.v, b.v), true);
                    if (a.v == 0 && b.v < 0)
                        throw new PyError("0.0 cannot be raised to a negative power");
                    if (a.v < 0 && b.v != Math.Floor(b.v))
                        throw new PyError("float() argument must be a string or a real number, not 'complex'");
                    double p = Math.Pow(a.v, b.v);
                    if (double.IsInfinity(p) && !double.IsInfinity(a.v) && !double.IsInfinity(b.v))
                        throw new PyError("(34, 'Result too large')");
                    return new Num(p, false);
            }
            throw new PyError(NOT_ALLOWED);
        }

        static string TypeName(Num n) { return n.isInt ? "int" : "float"; }

        static void One(string fn, List<Num> a, string qual)
        {
            if (a.Count != 1) throw new PyError(string.Format("{0}() takes exactly one argument ({1} given)", qual, a.Count));
        }

        static Num F(double v) { return new Num(v, false); }

        static Num CallFn(string fn, List<Num> a)
        {
            switch (fn)
            {
                case "sqrt":
                    One(fn, a, "math.sqrt");
                    if (a[0].v < 0) throw new PyError("math domain error");
                    return F(Math.Sqrt(a[0].v));
                case "sin": One(fn, a, "math.sin"); return F(Domain(Math.Sin(a[0].v), a[0].v));
                case "cos": One(fn, a, "math.cos"); return F(Domain(Math.Cos(a[0].v), a[0].v));
                case "tan": One(fn, a, "math.tan"); return F(Domain(Math.Tan(a[0].v), a[0].v));
                case "radians": One(fn, a, "math.radians"); return F(a[0].v * Math.PI / 180.0);
                case "degrees": One(fn, a, "math.degrees"); return F(a[0].v * 180.0 / Math.PI);
                case "exp":
                {
                    One(fn, a, "math.exp");
                    double e = Math.Exp(a[0].v);
                    if (double.IsInfinity(e) && !double.IsInfinity(a[0].v)) throw new PyError("math range error");
                    return F(e);
                }
                case "atan2":
                    if (a.Count != 2) throw new PyError(string.Format("atan2 expected 2 arguments, got {0}", a.Count));
                    return F(Math.Atan2(a[0].v, a[1].v));
                case "log":
                {
                    if (a.Count < 1) throw new PyError("log expected at least 1 argument, got 0");
                    if (a.Count > 2) throw new PyError(string.Format("log expected at most 2 arguments, got {0}", a.Count));
                    if (a[0].v <= 0) throw new PyError("math domain error");
                    double num = Math.Log(a[0].v);
                    if (a.Count == 1) return F(num);
                    if (a[1].v <= 0) throw new PyError("math domain error");
                    double den = Math.Log(a[1].v);
                    if (den == 0) throw new PyError("float division by zero");
                    return F(num / den);
                }
                case "hypot":
                {
                    double s = 0;
                    foreach (Num n in a) s += n.v * n.v;
                    return F(Math.Sqrt(s));
                }
                case "floor": One(fn, a, "math.floor"); return new Num(Math.Floor(a[0].v), true);
                case "ceil": One(fn, a, "math.ceil"); return new Num(Math.Ceiling(a[0].v), true);
                case "abs": One(fn, a, "abs"); return new Num(Math.Abs(a[0].v), a[0].isInt);
                case "min":
                case "max":
                {
                    if (a.Count == 0) throw new PyError(fn + " expected at least 1 argument, got 0");
                    if (a.Count == 1) throw new PyError("'" + TypeName(a[0]) + "' object is not iterable");
                    Num best = a[0];
                    for (int i = 1; i < a.Count; i++)
                        if (fn == "min" ? a[i].v < best.v : a[i].v > best.v) best = a[i];
                    return best;
                }
                case "round":
                {
                    if (a.Count == 0) throw new PyError("round() missing required argument 'number' (pos 1)");
                    if (a.Count > 2) throw new PyError(string.Format("round() takes at most 2 arguments ({0} given)", a.Count));
                    if (a.Count == 1) return new Num(Py.Round(a[0].v), true);
                    if (!a[1].isInt) throw new PyError("'float' object cannot be interpreted as an integer");
                    int nd = (int)a[1].v;
                    if (a[0].isInt) return nd >= 0 ? a[0] : new Num(Math.Round(a[0].v / Math.Pow(10, -nd), MidpointRounding.ToEven) * Math.Pow(10, -nd), true);
                    if (nd > 15) return a[0];
                    return F(nd >= 0 ? Py.Round(a[0].v, nd) : Math.Round(a[0].v / Math.Pow(10, -nd), MidpointRounding.ToEven) * Math.Pow(10, -nd));
                }
            }
            throw new PyError(NOT_ALLOWED);
        }

        static double Domain(double r, double x)
        {
            if (double.IsInfinity(x)) throw new PyError("math domain error");
            return r;
        }

        // Tokenizer + recursive-descent parser for Python expression syntax (the parts a model would write).
        sealed class Parser
        {
            readonly string _s;
            int _i;
            readonly List<string> _tok = new List<string>();   // token text; kinds by first char
            readonly List<char> _kind = new List<char>();      // n number, a name, s string, o operator
            int _p;

            static readonly string[] Ops3 = { "**=", "//=", ">>=", "<<=" };
            static readonly string[] Ops2 = { "**", "//", "<<", ">>", "<=", ">=", "==", "!=", ":=", "->" };
            static readonly HashSet<string> Keywords = new HashSet<string> { "and", "as", "assert", "async", "await", "break",
                "class", "continue", "def", "del", "elif", "else", "except", "finally", "for", "from", "global", "if",
                "import", "in", "is", "lambda", "nonlocal", "not", "or", "pass", "raise", "return", "try", "while", "with", "yield" };

            public Parser(string s) { _s = s; }

            static PyError Syntax(string what) { return new PyError(what + " (<unknown>, line 1)"); }

            public Node ParseAll()
            {
                Lex();
                if (_tok.Count == 0) throw new PyError("invalid syntax (<unknown>, line 0)");
                Node n = Expr();
                if (_p != _tok.Count) throw Syntax("invalid syntax");
                return n;
            }

            void Lex()
            {
                int depth = 0;
                // Leading newlines are fine; leading spaces are an indent.
                while (_i < _s.Length && (_s[_i] == '\n' || _s[_i] == '\r')) _i++;
                if (_i < _s.Length && (_s[_i] == ' ' || _s[_i] == '\t' || _s[_i] == '\f')) throw Syntax("unexpected indent");
                Stack<char> open = new Stack<char>();
                while (_i < _s.Length)
                {
                    char c = _s[_i];
                    if (c == ' ' || c == '\t' || c == '\f') { _i++; continue; }
                    if (c == '\r' || c == '\n')
                    {
                        if (depth > 0) { _i++; continue; }
                        int j = _i;
                        while (j < _s.Length && char.IsWhiteSpace(_s[j])) j++;
                        if (j == _s.Length) { _i = j; continue; }
                        throw Syntax("invalid syntax");
                    }
                    if (c == '\\' && _i + 1 < _s.Length && (_s[_i + 1] == '\n' || _s[_i + 1] == '\r')) { _i += 2; continue; }
                    if (c == '#') { while (_i < _s.Length && _s[_i] != '\n') _i++; continue; }
                    if (char.IsDigit(c) || (c == '.' && _i + 1 < _s.Length && char.IsDigit(_s[_i + 1]))) { LexNumber(); continue; }
                    if (char.IsLetter(c) || c == '_')
                    {
                        int j = _i;
                        while (j < _s.Length && (char.IsLetterOrDigit(_s[j]) || _s[j] == '_')) j++;
                        string w = _s.Substring(_i, j - _i);
                        if (j < _s.Length && (_s[j] == '\'' || _s[j] == '"') && w.Length <= 3 && w.ToLowerInvariant().Trim('r', 'b', 'u', 'f').Length == 0)
                        { _i = j; LexString(); continue; }       // r"..", b'..', f".." prefixes
                        Add(w, 'a');
                        _i = j;
                        continue;
                    }
                    if (c == '\'' || c == '"') { LexString(); continue; }
                    string op = null;
                    foreach (string o in Ops3) if (string.CompareOrdinal(_s, _i, o, 0, 3) == 0) { op = o; break; }
                    if (op == null) foreach (string o in Ops2) if (string.CompareOrdinal(_s, _i, o, 0, 2) == 0) { op = o; break; }
                    if (op == null && "+-*/%@&|^~<>()[]{},:.;=!".IndexOf(c) >= 0) op = c.ToString();
                    if (op == null) throw Syntax("invalid character '" + c + "' (U+" + ((int)c).ToString("X4", CultureInfo.InvariantCulture) + ")");
                    if (op == "(" || op == "[" || op == "{") { depth++; open.Push(op[0]); }
                    if (op == ")" || op == "]" || op == "}")
                    {
                        char want = op == ")" ? '(' : op == "]" ? '[' : '{';
                        if (open.Count == 0) throw Syntax("unmatched '" + op + "'");
                        char got = open.Pop();
                        if (got != want) throw Syntax("closing parenthesis '" + op + "' does not match opening parenthesis '" + got + "'");
                        depth--;
                    }
                    Add(op, 'o');
                    _i += op.Length;
                }
                if (open.Count > 0) throw Syntax("'" + open.Peek() + "' was never closed");
            }

            void Add(string t, char k) { _tok.Add(t); _kind.Add(k); }

            void LexNumber()
            {
                int j = _i;
                if (_s[j] == '0' && j + 1 < _s.Length && "xXoObB".IndexOf(_s[j + 1]) >= 0)
                {
                    j += 2;
                    while (j < _s.Length && (Uri.IsHexDigit(_s[j]) || _s[j] == '_')) j++;
                }
                else
                {
                    while (j < _s.Length && (char.IsDigit(_s[j]) || _s[j] == '_')) j++;
                    if (j < _s.Length && _s[j] == '.') { j++; while (j < _s.Length && (char.IsDigit(_s[j]) || _s[j] == '_')) j++; }
                    if (j < _s.Length && (_s[j] == 'e' || _s[j] == 'E'))
                    {
                        int k = j + 1;
                        if (k < _s.Length && (_s[k] == '+' || _s[k] == '-')) k++;
                        if (k < _s.Length && char.IsDigit(_s[k])) { j = k; while (j < _s.Length && (char.IsDigit(_s[j]) || _s[j] == '_')) j++; }
                    }
                    if (j < _s.Length && (_s[j] == 'j' || _s[j] == 'J')) j++;
                }
                Add(_s.Substring(_i, j - _i), 'n');
                _i = j;
            }

            void LexString()
            {
                char q = _s[_i];
                bool triple = _i + 2 < _s.Length && _s[_i + 1] == q && _s[_i + 2] == q;
                int j = _i + (triple ? 3 : 1);
                while (true)
                {
                    if (j >= _s.Length) throw Syntax("unterminated string literal (detected at line 1)");
                    if (_s[j] == '\\') { j += 2; continue; }
                    if (!triple && (_s[j] == '\n')) throw Syntax("unterminated string literal (detected at line 1)");
                    if (triple && j + 2 < _s.Length && _s[j] == q && _s[j + 1] == q && _s[j + 2] == q) { j += 3; break; }
                    if (!triple && _s[j] == q) { j++; break; }
                    j++;
                }
                Add(_s.Substring(_i, j - _i), 's');
                _i = j;
            }

            string Peek() { return _p < _tok.Count ? _tok[_p] : null; }
            char PeekKind() { return _p < _tok.Count ? _kind[_p] : '\0'; }
            bool Is(string t) { return _p < _tok.Count && _kind[_p] != 's' && _tok[_p] == t; }
            void Expect(string t) { if (!Is(t)) throw Syntax("invalid syntax"); _p++; }

            // expr: lambda | ternary
            Node Expr()
            {
                if (Is("lambda"))
                {
                    _p++;
                    while (_p < _tok.Count && !Is(":")) _p++;
                    Expect(":");
                    Expr();
                    return new Reject();
                }
                Node n = Or();
                if (Is("if")) { _p++; Or(); Expect("else"); Expr(); return new Reject(); }
                return n;
            }

            Node Or() { Node n = And(); while (Is("or")) { _p++; And(); n = new Reject(); } return n; }
            Node And() { Node n = Not(); while (Is("and")) { _p++; Not(); n = new Reject(); } return n; }
            Node Not() { if (Is("not")) { _p++; Not(); return new Reject(); } return Comparison(); }

            Node Comparison()
            {
                Node n = BitOr();
                while (true)
                {
                    string t = Peek();
                    if (PeekKind() == 'o' && (t == "<" || t == ">" || t == "<=" || t == ">=" || t == "==" || t == "!=")) { _p++; }
                    else if (Is("in")) { _p++; }
                    else if (Is("not") && _p + 1 < _tok.Count && _tok[_p + 1] == "in") { _p += 2; }
                    else if (Is("is")) { _p++; if (Is("not")) _p++; }
                    else return n;
                    BitOr();
                    n = new Reject();
                }
            }

            Node BitOr() { Node n = BitXor(); while (Is("|")) { _p++; BitXor(); n = new Reject(); } return n; }
            Node BitXor() { Node n = BitAnd(); while (Is("^")) { _p++; BitAnd(); n = new Reject(); } return n; }
            Node BitAnd() { Node n = Shift(); while (Is("&")) { _p++; Shift(); n = new Reject(); } return n; }
            Node Shift() { Node n = Arith(); while (Is("<<") || Is(">>")) { _p++; Arith(); n = new Reject(); } return n; }

            Node Arith()
            {
                Node n = Term();
                while (Is("+") || Is("-")) { string op = Peek(); _p++; n = new Bin(op, n, Term()); }
                return n;
            }

            Node Term()
            {
                Node n = Factor();
                while (Is("*") || Is("/") || Is("%") || Is("//") || Is("@"))
                {
                    string op = Peek();
                    _p++;
                    Node r = Factor();
                    n = (op == "//" || op == "@") ? (Node)new Reject() : new Bin(op, n, r);
                }
                return n;
            }

            Node Factor()
            {
                if (Is("+") || Is("-")) { string op = Peek(); _p++; return new Unary(op, Factor()); }
                if (Is("~")) { _p++; Factor(); return new Reject(); }
                return Power();
            }

            Node Power()
            {
                Node n = Primary();
                if (Is("**")) { _p++; return new Bin("**", n, Factor()); }
                return n;
            }

            // atom trailers: calls, attributes, subscripts
            Node Primary()
            {
                string name = (PeekKind() == 'a') ? Peek() : null;
                Node n = Atom();
                bool first = true;
                while (true)
                {
                    if (Is("("))
                    {
                        _p++;
                        List<Node> args = new List<Node>();
                        while (!Is(")"))
                        {
                            if (Is("*") || Is("**")) { _p++; Expr(); args.Add(new Reject()); }
                            else if (PeekKind() == 'a' && _p + 1 < _tok.Count && _tok[_p + 1] == "=") { _p += 2; Expr(); }   // keywords: ignored, like the Python
                            else args.Add(Expr());
                            if (!Is(",")) break;
                            _p++;
                        }
                        Expect(")");
                        bool allowed = first && name != null && Array.IndexOf(FUNCS, name) >= 0;
                        n = allowed ? (Node)new Call(name, args) : new Reject();
                    }
                    else if (Is(".")) { _p++; if (PeekKind() != 'a') throw Syntax("invalid syntax"); _p++; n = new Reject(); }
                    else if (Is("[")) { _p++; Skip("]"); n = new Reject(); }
                    else return n;
                    first = false;
                }
            }

            // Skip a bracketed run whose content we never evaluate (the lexer already checked the brackets match).
            void Skip(string close)
            {
                int depth = 0;
                while (_p < _tok.Count)
                {
                    string t = _tok[_p];
                    bool op = _kind[_p] == 'o';
                    _p++;
                    if (op && (t == "(" || t == "[" || t == "{")) depth++;
                    else if (op && (t == ")" || t == "]" || t == "}"))
                    {
                        if (depth == 0) return;
                        depth--;
                    }
                }
                throw Syntax("invalid syntax");
            }

            Node Atom()
            {
                if (_p >= _tok.Count) throw Syntax("invalid syntax");
                string t = _tok[_p];
                char k = _kind[_p];
                if (k == 'n') { _p++; return Number(t); }
                if (k == 's') { _p++; while (PeekKind() == 's') _p++; return new Reject(); }
                if (k == 'a')
                {
                    if (t == "True" || t == "False") { _p++; return new Const(new Num(t == "True" ? 1 : 0, true)); }
                    if (Keywords.Contains(t)) throw Syntax("invalid syntax");
                    _p++;
                    if (t == "pi") return new Const(new Num(Math.PI, false));
                    return new NameNode();      // any other bare name; allowed calls are handled in Primary
                }
                if (t == "(")
                {
                    _p++;
                    if (Is(")")) { _p++; return new Reject(); }       // () is a tuple
                    Node n = Expr();
                    if (Is(",")) { Skip(")"); return new Reject(); }   // a tuple
                    Expect(")");
                    return n;
                }
                if (t == "[" || t == "{") { _p++; Skip(t == "[" ? "]" : "}"); return new Reject(); }
                if (t == "...") { _p++; return new Reject(); }
                throw Syntax("invalid syntax");
            }

            Node Number(string t)
            {
                string s = t.Replace("_", "");
                if (s.EndsWith("j") || s.EndsWith("J")) return new Reject();   // complex literal
                if (s.Length > 1 && s[0] == '0' && "xXoObB".IndexOf(s[1]) >= 0)
                {
                    int b = "xX".IndexOf(s[1]) >= 0 ? 16 : "oO".IndexOf(s[1]) >= 0 ? 8 : 2;
                    double v = 0;
                    foreach (char c in s.Substring(2))
                    {
                        int dv = Uri.IsHexDigit(c) ? Uri.FromHex(c) : 99;
                        if (dv >= b) throw Syntax("invalid digit '" + c + "' in literal");
                        v = v * b + dv;
                    }
                    if (s.Length == 2) throw Syntax("invalid syntax");
                    return new Const(new Num(v, true));
                }
                bool isFloat = s.IndexOfAny(new char[] { '.', 'e', 'E' }) >= 0;
                if (!isFloat && s.Length > 1 && s[0] == '0' && s.Trim('0').Length > 0)
                    throw Syntax("leading zeros in decimal integer literals are not permitted; use an 0o prefix for octal integers");
                double d;
                if (!double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out d)) throw Syntax("invalid decimal literal");
                return new Const(new Num(d, !isFloat));
            }
        }

        // A bare name other than pi.
        sealed class NameNode : Node
        {
            public override Num Eval() { throw new PyError(NOT_ALLOWED); }
        }

        // round(float(_safe_eval(ast.parse(expr, mode="eval"))), 6)
        public static double SafeEval(string expr)
        {
            Num n = new Parser(expr).ParseAll().Eval();
            if (n.isInt && double.IsInfinity(n.v)) throw new PyError("int too large to convert to float");
            return double.IsInfinity(n.v) || double.IsNaN(n.v) ? n.v : Py.Round(n.v, 6);
        }

        // ------------------------------------------------------------------ helpers (Python float() / int() errors)
        static string PyType(object o)
        {
            if (o == null) return "NoneType";
            if (o is string) return "str";
            if (o is bool) return "bool";
            if (o is double) return "float";
            if (Py.IsNumber(o)) return "int";
            if (o is IDictionary) return "dict";
            return "list";
        }

        static double Float(object o)
        {
            if (o is string)
            {
                double d;
                if (Py.TryNum(o, out d)) return d;
                throw new PyError("could not convert string to float: " + Py.Repr(o));
            }
            if (o == null || !(o is bool || Py.IsNumber(o)))
                throw new PyError("float() argument must be a string or a real number, not '" + PyType(o) + "'");
            return Py.Num(o);
        }

        static int Int(object o)
        {
            if (o is string)
            {
                int i;
                if (int.TryParse(((string)o).Trim(), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out i)) return i;
                throw new PyError("invalid literal for int() with base 10: " + Py.Repr(o));
            }
            if (o == null || !(o is bool || Py.IsNumber(o)))
                throw new PyError("int() argument must be a string, a bytes-like object or a real number, not '" + PyType(o) + "'");
            return Py.Int(o);
        }

        // round(x) -> int
        static long RoundI(double x) { return (long)Py.Round(x); }

        // Iterate a value the way a Python for-loop would (a string gives its characters).
        static List<object> Iter(object o)
        {
            string s = o as string;
            if (s != null) return s.Select(c => (object)c.ToString()).ToList();
            List<object> l = Py.List(o);
            if (l != null) return l;
            IDictionary d = o as IDictionary;
            if (d != null) { List<object> keys = new List<object>(); foreach (object k in d.Keys) keys.Add(k); return keys; }
            throw new PyError("'" + PyType(o) + "' object is not iterable");
        }

        static Contact Track(World world, object tid)
        {
            Contact c;
            if (!world.contacts.TryGetValue(Py.Str(tid).Replace(" ", ""), out c) || c == null || c.hidden)
                throw new PyError(string.Format("no track {0} on the COP", Py.Str(tid)));
            return c;
        }

        static double MinValue(Dictionary<string, object> d) { return d.Values.Select(v => Py.Num(v)).Min(); }

        // ------------------------------------------------------------------ the tools
        public static Dictionary<string, object> ToolCalc(World world, Dictionary<string, object> args)
        {
            string expr = Py.Cut(Py.Str(Py.Get(args, "expression", "")), 300);
            return Py.D("expression", expr, "result", SafeEval(expr));
        }

        // Closest point of approach to a track for own ship on its current, or a trial, heading/speed.
        public static Dictionary<string, object> ToolCpa(World world, Dictionary<string, object> args)
        {
            Contact c = Track(world, Py.Get(args, "track"));
            OwnShip s = world.ship;
            double h = Float(Py.Get(args, "own_heading", s.heading));
            double v = Float(Py.Get(args, "own_speed", s.speed));
            double[] r = WorldMath.CpaTcpa(new double[] { s.x, s.y }, WorldMath.Velocity(h, v), new double[] { c.x, c.y }, WorldMath.Velocity(c.course, c.speed));
            return Py.D("track", c.id, "own_heading", h, "own_speed", v, "cpa_nm", Py.Round(r[0], 3), "tcpa_min", Py.Round(r[1], 2));
        }

        // Minutes until a track (closing at its current geometry) reaches a given range from own ship.
        public static Dictionary<string, object> ToolTimeToRange(World world, Dictionary<string, object> args)
        {
            Contact c = Track(world, Py.Get(args, "track"));
            OwnShip s = world.ship;
            double target = Float(Py.Get(args, "range_nm"));
            double[] cv = WorldMath.Velocity(c.course, c.speed), sv = WorldMath.Velocity(s.heading, s.speed);
            double[] rel_v = { cv[0] - sv[0], cv[1] - sv[1] };
            double rx = c.x - s.x, ry = c.y - s.y;
            double rng = WorldMath.Hypot(rx, ry);
            double closing = rng != 0 ? -(rx * rel_v[0] + ry * rel_v[1]) / rng : 0.0;      // kn, positive = closing
            object minutes = closing > 0 && rng > target ? (object)Py.Round((rng - target) / closing * 60, 2) : null;
            return Py.D("track", c.id, "range_now_nm", Py.Round(rng, 2), "closing_speed_kn", Py.Round(closing, 1),
                        "target_range_nm", target, "minutes", minutes);
        }

        // P(one inbound gets through) = product of (1 - pk) over the listed systems, using the published pk table.
        public static Dictionary<string, object> ToolHitProbability(World world, Dictionary<string, object> args)
        {
            List<object> systems = Iter(Py.Get(args, "systems", new List<object>())).Select(x => (object)Py.Str(x)).ToList();
            List<object> unknown = systems.Where(x => !ShipSystems.DEFENSES.ContainsKey((string)x)).ToList();
            if (unknown.Count > 0)
            {
                List<string> known = ShipSystems.DEFENSES.Keys.ToList();
                known.Sort(string.CompareOrdinal);
                throw new PyError(string.Format("unknown systems {0}; known: {1}", Py.Repr(unknown), Py.Repr(known)));
            }
            double p = 1.0;
            foreach (object d in systems) p *= 1 - ShipSystems.DEFENSES[(string)d];
            int n = Int(Py.Get(args, "inbound_count", 1));
            Dictionary<string, object> pk = new Dictionary<string, object>();
            foreach (object d in systems) pk[(string)d] = ShipSystems.DEFENSES[(string)d];
            return Py.D("systems", systems, "pk_table", pk, "p_one_gets_through", Py.Round(p, 4),
                        "inbound_count", n, "p_at_least_one_hit", Py.Round(1 - Math.Pow(1 - p, n), 4));
        }

        // What would be energized, and generator load, if these breakers were closed/opened. A what-if on a copy.
        public static Dictionary<string, object> ToolPowerLineup(World world, Dictionary<string, object> args)
        {
            // copy.deepcopy(world.sys): Flow() reads only nodes and breakers, so a fresh plant with copies of those.
            ShipSystems sim = new ShipSystems();
            sim.nodes = CopyTable(world.sys.nodes);
            sim.breakers = CopyTable(world.sys.breakers);
            object close = Py.Get(args, "close", new List<object>()), open = Py.Get(args, "open", new List<object>());
            foreach (object b in Iter(close))
            {
                string id = b as string;
                if (id == null || !sim.breakers.ContainsKey(id)) throw new PyError("unknown breaker " + Py.Str(b));
                sim.breakers[id]["closed"] = true;
            }
            foreach (object b in Iter(open))
            {
                string id = b as string;
                if (id == null || !sim.breakers.ContainsKey(id)) throw new PyError("unknown breaker " + Py.Str(b));
                sim.breakers[id]["closed"] = false;
            }
            sim.Flow();
            List<object> unpowered = new List<object>();
            foreach (KeyValuePair<string, Dictionary<string, object>> kv in sim.nodes)
                if (!Py.Truthy(kv.Value["powered"]) && !Py.Truthy(kv.Value["failed"])) unpowered.Add(kv.Key);
            Dictionary<string, object> load = new Dictionary<string, object>();
            foreach (KeyValuePair<string, int> kv in sim.gen_load) load[kv.Key] = kv.Value;
            return Py.D("close", close, "open", open, "unpowered", unpowered, "generator_load_pct", load);
        }

        static Dictionary<string, Dictionary<string, object>> CopyTable(Dictionary<string, Dictionary<string, object>> t)
        {
            Dictionary<string, Dictionary<string, object>> c = new Dictionary<string, Dictionary<string, object>>();
            foreach (KeyValuePair<string, Dictionary<string, object>> kv in t) c[kv.Key] = (Dictionary<string, object>)Py.DeepCopy(kv.Value);
            return c;
        }

        // Frames and seconds for a repair party to reach a compartment.
        public static Dictionary<string, object> ToolDcTransit(World world, Dictionary<string, object> args)
        {
            Dictionary<string, object> t = null, comp = null;
            string team = Py.Get(args, "team") as string, cid = Py.Get(args, "compartment") as string;
            if (team != null) world.sys.teams.TryGetValue(team, out t);
            if (cid != null) ShipSystems.COMP.TryGetValue(cid, out comp);
            if (t == null || comp == null)
                throw new PyError("unknown team or compartment");
            List<object> fr = Py.List(comp["fr"]);
            double mid = fr.Sum(x => Py.Num(x)) / 2;
            double frames = Math.Abs(Py.Num(t["pos"]) - mid);
            return Py.D("team", t["id"], "from_frame", RoundI(Py.Num(t["pos"])), "to_frame", mid, "frames", RoundI(frames),
                        "seconds", RoundI(frames / ShipSystems.TEAM_FRAMES_PER_S), "team_status", t["status"]);
        }

        // Clearance (nm) from each heavy rain cell's edge along a trial heading over the next 30 min. Negative = into it.
        public static Dictionary<string, object> ToolWeatherOnHeading(World world, Dictionary<string, object> args)
        {
            double h = Float(Py.Get(args, "heading"));
            object sp = Py.Get(args, "speed");
            Dictionary<string, object> clear = world.StormClearance(WorldMath.PyMod(h, 360), sp == null ? (double?)null : Float(sp));
            return Py.D("heading", WorldMath.PyMod(h, 360), "clearance_nm_by_cell", clear,
                        "min_clearance_nm", clear.Count > 0 ? (object)MinValue(clear) : null,
                        "runs_into_cell", clear.Values.Any(v => Py.Num(v) < 0));
        }

        // For a trial heading/speed: closing speed of a torpedo, time to reach us, and whether it runs out of fuel first.
        public static Dictionary<string, object> ToolTorpedoEvasion(World world, Dictionary<string, object> args)
        {
            string want = Py.Str(Py.Get(args, "torpedo"));
            Dictionary<string, object> t = world.sys.torpedoes.FirstOrDefault(x => Py.Str(x["id"]) == want);
            if (t == null)
                throw new PyError(string.Format("no torpedo {0} in the water", Py.Str(Py.Get(args, "torpedo"))));
            double h = WorldMath.PyMod(Float(Py.Get(args, "own_heading", world.ship.heading)), 360);
            double v = Float(Py.Get(args, "own_speed", world.ship.speed));
            double closing = ShipSystems.TORP_KN + v * Math.Cos(WorldMath.Radians(h - Py.Num(t["bearing"])));
            double? t_reach = closing > 0 ? Py.Num(t["range"]) / closing * 60 : (double?)null;
            double t_fuel = Py.Num(t["run_left"]) / ShipSystems.TORP_KN * 60;
            return Py.D("torpedo", t["id"], "own_heading", h, "own_speed", v, "torpedo_bearing", t["bearing"],
                        "closing_speed_kn", Py.Round(closing, 1), "minutes_to_reach_us", t_reach.HasValue ? (object)Py.Round(t_reach.Value, 2) : null,
                        "minutes_of_fuel_left", Py.Round(t_fuel, 2), "outruns_torpedo", !t_reach.HasValue || t_fuel < t_reach.Value);
        }

        // Both headings that put a threat bearing `offset` degrees off the bow, the turn each needs from cmd_heading,
        // and the heavy-weather clearance on each.
        public static Dictionary<string, object> ToolEvasionHeadings(World world, Dictionary<string, object> args)
        {
            double brg = WorldMath.PyMod(Float(Py.Get(args, "threat_bearing")), 360);
            double off = Float(Py.Get(args, "offset_deg", 45.0));
            double cmd = world.ship.cmd_heading;
            List<object> outl = new List<object>();
            foreach (double h in new double[] { WorldMath.PyMod(brg + off, 360), WorldMath.PyMod(brg - off, 360) })
            {
                Dictionary<string, object> clear = world.env.cells.Count > 0 ? world.StormClearance(h) : new Dictionary<string, object>();
                outl.Add(Py.D("heading", RoundI(h), "turn_from_cmd_deg", RoundI(WorldMath.AngleDiff(h, cmd)),
                              "min_weather_clearance_nm", clear.Count > 0 ? (object)MinValue(clear) : null));
            }
            return Py.D("threat_bearing", brg, "cmd_heading", RoundI(cmd), "options", outl);
        }

        // name -> (function, example args, description)
        public static readonly Dictionary<string, Tuple<Func<World, Dictionary<string, object>, Dictionary<string, object>>, string, string>> TOOLS = BuildTools();

        static Dictionary<string, Tuple<Func<World, Dictionary<string, object>, Dictionary<string, object>>, string, string>> BuildTools()
        {
            Dictionary<string, Tuple<Func<World, Dictionary<string, object>, Dictionary<string, object>>, string, string>> t =
                new Dictionary<string, Tuple<Func<World, Dictionary<string, object>, Dictionary<string, object>>, string, string>>();
            t["calc"] = Tuple.Create<Func<World, Dictionary<string, object>, Dictionary<string, object>>, string, string>(ToolCalc,
                "{\"expression\": \"1-(1-0.45)*(1-0.35)\"}", "arithmetic and math functions");
            t["cpa"] = Tuple.Create<Func<World, Dictionary<string, object>, Dictionary<string, object>>, string, string>(ToolCpa,
                "{\"track\": \"TN4002\", \"own_heading\": 120, \"own_speed\": 8}",
                "CPA (nm) and TCPA (min) to a track; heading/speed optional for a trial maneuver");
            t["time_to_range"] = Tuple.Create<Func<World, Dictionary<string, object>, Dictionary<string, object>>, string, string>(ToolTimeToRange,
                "{\"track\": \"TN4006\", \"range_nm\": 10}", "closing speed and minutes until a track reaches a range");
            t["hit_probability"] = Tuple.Create<Func<World, Dictionary<string, object>, Dictionary<string, object>>, string, string>(ToolHitProbability,
                "{\"systems\": [\"CIWS\", \"ADS\"], \"inbound_count\": 2}", "probability inbound get through a set of defenses (pk table)");
            t["power_lineup"] = Tuple.Create<Func<World, Dictionary<string, object>, Dictionary<string, object>>, string, string>(ToolPowerLineup,
                "{\"close\": [\"B-13\"], \"open\": []}", "what-if: equipment unpowered and generator load for a breaker lineup");
            t["weather_on_heading"] = Tuple.Create<Func<World, Dictionary<string, object>, Dictionary<string, object>>, string, string>(ToolWeatherOnHeading,
                "{\"heading\": 55}", "clearance from each heavy rain cell along a trial heading (negative = runs into it)");
            t["torpedo_evasion"] = Tuple.Create<Func<World, Dictionary<string, object>, Dictionary<string, object>>, string, string>(ToolTorpedoEvasion,
                "{\"torpedo\": \"TN4003-T1\", \"own_heading\": 270, \"own_speed\": 31}",
                "torpedo closing speed and whether it runs out of fuel before reaching us on a trial heading/speed");
            t["evasion_headings"] = Tuple.Create<Func<World, Dictionary<string, object>, Dictionary<string, object>>, string, string>(ToolEvasionHeadings,
                "{\"threat_bearing\": 130, \"offset_deg\": 45}",
                "the two headings putting a threat offset_deg off the bow, the turn each needs, weather clearance");
            t["dc_transit"] = Tuple.Create<Func<World, Dictionary<string, object>, Dictionary<string, object>>, string, string>(ToolDcTransit,
                "{\"team\": \"R3\", \"compartment\": \"MMR2\"}", "frames and seconds for a repair party to reach a compartment");
            return t;
        }

        // Which calculators each role may use.
        public static readonly Dictionary<string, List<string>> ROLE_TOOLS = new Dictionary<string, List<string>>
        {
            { "collision", new List<string> { "cpa", "weather_on_heading", "calc" } }, { "navigation", new List<string> { "weather_on_heading", "calc" } },
            { "weather", new List<string> { "weather_on_heading", "calc" } }, { "energy", new List<string> { "calc" } },
            { "engine", new List<string> { "calc" } }, { "ew", new List<string> { "time_to_range", "calc" } },
            { "defense", new List<string> { "evasion_headings", "hit_probability", "calc", "time_to_range" } },
            { "asw", new List<string> { "torpedo_evasion", "calc" } },
            { "power", new List<string> { "power_lineup", "calc" } }, { "damage_control", new List<string> { "dc_transit", "calc" } },
        };

        public static Dictionary<string, object> RunTool(World world, string name, object args)
        {
            if (name == null || !TOOLS.ContainsKey(name))
                return Py.D("error", "unknown tool " + Py.Str(name));
            try
            {
                return TOOLS[name].Item1(world, (args as Dictionary<string, object>) ?? new Dictionary<string, object>());
            }
            catch (Exception e)
            {
                return Py.D("error", Py.Cut(e.Message, 200));
            }
        }
    }
}
