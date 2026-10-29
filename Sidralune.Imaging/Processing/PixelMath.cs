using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading.Tasks;

namespace Sidralune.Imaging
{
    /// <summary>
    /// PixelMath expression evaluator. Expressions are compiled to a small stack program and evaluated per pixel.
    /// Variables: $T (target image), named images (e.g. Ha, OIII), $x, $y, $c (channel). Functions: min, max, abs, sqrt,
    /// exp, ln, log, pow, mtf(m,x), iif(cond,a,b), clamp(x,lo,hi), sin, cos, avg(a,b,...).
    /// Operators: + - * / ^ &lt; &gt; &lt;= &gt;= == != &amp;&amp; || and unary minus.
    /// </summary>
    public class PixelMath
    {
        private enum Op { Const, Var, Img, X, Y, C, Add, Sub, Mul, Div, Pow, Neg, Lt, Gt, Le, Ge, Eq, Ne, And, Or, Fn }
        private struct Ins { public Op Op; public double V; public int I; public int Argc; public string Name; }

        private readonly List<Ins> _code = new List<Ins>();
        private readonly List<string> _images = new List<string>();
        private string _s; private int _p;

        public IReadOnlyList<string> ReferencedImages => _images;

        public PixelMath(string expr)
        {
            _s = expr; _p = 0;
            ParseOr();
            Skip();
            if (_p < _s.Length) throw new FormatException("Unexpected '" + _s.Substring(_p) + "'");
        }

        private void Skip() { while (_p < _s.Length && char.IsWhiteSpace(_s[_p])) _p++; }
        private bool Eat(string t) { Skip(); if (string.CompareOrdinal(_s, _p, t, 0, t.Length) == 0) { _p += t.Length; return true; } return false; }
        private void Emit(Op op, double v = 0, int i = 0, int argc = 0, string name = null) => _code.Add(new Ins { Op = op, V = v, I = i, Argc = argc, Name = name });

        private void ParseOr() { ParseAnd(); while (Eat("||")) { ParseAnd(); Emit(Op.Or); } }
        private void ParseAnd() { ParseCmp(); while (Eat("&&")) { ParseCmp(); Emit(Op.And); } }
        private void ParseCmp()
        {
            ParseAdd();
            while (true)
            {
                if (Eat("<=")) { ParseAdd(); Emit(Op.Le); }
                else if (Eat(">=")) { ParseAdd(); Emit(Op.Ge); }
                else if (Eat("==")) { ParseAdd(); Emit(Op.Eq); }
                else if (Eat("!=")) { ParseAdd(); Emit(Op.Ne); }
                else if (Eat("<")) { ParseAdd(); Emit(Op.Lt); }
                else if (Eat(">")) { ParseAdd(); Emit(Op.Gt); }
                else break;
            }
        }
        private void ParseAdd() { ParseMul(); while (true) { if (Eat("+")) { ParseMul(); Emit(Op.Add); } else if (Eat("-")) { ParseMul(); Emit(Op.Sub); } else break; } }
        private void ParseMul() { ParsePow(); while (true) { if (Eat("*")) { ParsePow(); Emit(Op.Mul); } else if (Eat("/")) { ParsePow(); Emit(Op.Div); } else break; } }
        private void ParsePow() { ParseUnary(); if (Eat("^")) { ParsePow(); Emit(Op.Pow); } }
        private void ParseUnary() { if (Eat("-")) { ParseUnary(); Emit(Op.Neg); } else if (Eat("+")) ParseUnary(); else ParseAtom(); }

        private void ParseAtom()
        {
            Skip();
            if (Eat("(")) { ParseOr(); if (!Eat(")")) throw new FormatException("Missing ')'"); return; }
            int st = _p;
            if (_p < _s.Length && (char.IsDigit(_s[_p]) || _s[_p] == '.'))
            {
                while (_p < _s.Length && (char.IsDigit(_s[_p]) || _s[_p] == '.' || _s[_p] == 'e' || _s[_p] == 'E' || ((_s[_p] == '-' || _s[_p] == '+') && (_s[_p - 1] == 'e' || _s[_p - 1] == 'E')))) _p++;
                Emit(Op.Const, double.Parse(_s.Substring(st, _p - st), CultureInfo.InvariantCulture));
                return;
            }
            while (_p < _s.Length && (char.IsLetterOrDigit(_s[_p]) || _s[_p] == '_' || _s[_p] == '$')) _p++;
            string id = _s.Substring(st, _p - st);
            if (id.Length == 0) throw new FormatException("Expected value at position " + st);
            if (Eat("("))
            {
                int argc = 0;
                if (!Eat(")"))
                {
                    do { ParseOr(); argc++; } while (Eat(","));
                    if (!Eat(")")) throw new FormatException("Missing ')' after arguments of " + id);
                }
                Emit(Op.Fn, 0, 0, argc, id.ToLowerInvariant());
                return;
            }
            switch (id.ToLowerInvariant())
            {
                case "$x": Emit(Op.X); return;
                case "$y": Emit(Op.Y); return;
                case "$c": Emit(Op.C); return;
                case "pi": Emit(Op.Const, Math.PI); return;
                case "e": Emit(Op.Const, Math.E); return;
            }
            string name = id == "$T" || id == "$t" ? "$T" : id;
            int idx = _images.IndexOf(name);
            if (idx < 0) { _images.Add(name); idx = _images.Count - 1; }
            Emit(Op.Img, 0, idx);
        }

        /// <summary>Evaluate into a new image the size of 'target'. images maps names (and "$T") to images.</summary>
        public AstroImage Evaluate(AstroImage target, IDictionary<string, AstroImage> images, int outChannels = -1)
        {
            var imgs = new AstroImage[_images.Count];
            for (int i = 0; i < _images.Count; i++)
            {
                AstroImage im;
                if (_images[i] == "$T") imgs[i] = target;
                else if (images != null && images.TryGetValue(_images[i], out im)) imgs[i] = im;
                else throw new KeyNotFoundException("Unknown image '" + _images[i] + "' in expression");
            }
            int ch = outChannels > 0 ? outChannels : target.Channels;
            var outp = new AstroImage(target.Width, target.Height, ch) { Header = target.Header.Clone() };
            var stats = new Dictionary<string, double>();
            int w = target.Width, h = target.Height;
            var code = _code.ToArray();
            for (int c = 0; c < ch; c++)
            {
                int cc = c;
                Parallel.For(0, h, Core.Util.StopRequest.Options, () => new double[64], (y, st, stack) =>
                {
                    for (int x = 0; x < w; x++)
                        outp.Data[cc][y * w + x] = (float)Run(code, stack, imgs, x, y, cc, w, stats);
                    return stack;
                }, s => { });
            }
            return outp;
        }

        private static double Run(Ins[] code, double[] st, AstroImage[] imgs, int x, int y, int c, int w, Dictionary<string, double> cache)
        {
            int sp = 0;
            foreach (var ins in code)
            {
                switch (ins.Op)
                {
                    case Op.Const: st[sp++] = ins.V; break;
                    case Op.Img: { var im = imgs[ins.I]; st[sp++] = im.Data[Math.Min(c, im.Channels - 1)][y * w + x]; break; }
                    case Op.X: st[sp++] = x; break;
                    case Op.Y: st[sp++] = y; break;
                    case Op.C: st[sp++] = c; break;
                    case Op.Neg: st[sp - 1] = -st[sp - 1]; break;
                    case Op.Fn: sp = Call(ins.Name, ins.Argc, st, sp, imgs, c, cache); break;
                    default:
                        {
                            double b = st[--sp], a = st[--sp], r;
                            switch (ins.Op)
                            {
                                case Op.Add: r = a + b; break;
                                case Op.Sub: r = a - b; break;
                                case Op.Mul: r = a * b; break;
                                case Op.Div: r = b != 0 ? a / b : 0; break;
                                case Op.Pow: r = Math.Pow(a, b); break;
                                case Op.Lt: r = a < b ? 1 : 0; break;
                                case Op.Gt: r = a > b ? 1 : 0; break;
                                case Op.Le: r = a <= b ? 1 : 0; break;
                                case Op.Ge: r = a >= b ? 1 : 0; break;
                                case Op.Eq: r = a == b ? 1 : 0; break;
                                case Op.Ne: r = a != b ? 1 : 0; break;
                                case Op.And: r = a != 0 && b != 0 ? 1 : 0; break;
                                default: r = a != 0 || b != 0 ? 1 : 0; break;
                            }
                            st[sp++] = r;
                            break;
                        }
                }
            }
            return sp > 0 ? st[sp - 1] : 0;
        }

        private static int Call(string fn, int argc, double[] st, int sp, AstroImage[] imgs, int c, Dictionary<string, double> cache)
        {
            double[] a = new double[argc];
            for (int i = argc - 1; i >= 0; i--) a[i] = st[--sp];
            double r;
            switch (fn)
            {
                case "min": r = a[0]; for (int i = 1; i < argc; i++) r = Math.Min(r, a[i]); break;
                case "max": r = a[0]; for (int i = 1; i < argc; i++) r = Math.Max(r, a[i]); break;
                case "abs": r = Math.Abs(a[0]); break;
                case "sqrt": r = Math.Sqrt(Math.Max(0, a[0])); break;
                case "exp": r = Math.Exp(a[0]); break;
                case "ln": r = a[0] > 0 ? Math.Log(a[0]) : 0; break;
                case "log": r = a[0] > 0 ? Math.Log10(a[0]) : 0; break;
                case "pow": r = Math.Pow(a[0], a[1]); break;
                case "sin": r = Math.Sin(a[0]); break;
                case "cos": r = Math.Cos(a[0]); break;
                case "mtf": r = HistogramTransform.Mtf(a[0], a[1]); break;
                case "iif": r = a[0] != 0 ? a[1] : a[2]; break;
                case "clamp": r = Math.Max(a[1], Math.Min(a[2], a[0])); break;
                case "avg": r = 0; for (int i = 0; i < argc; i++) r += a[i]; r /= Math.Max(1, argc); break;
                default: throw new NotSupportedException("Unknown function " + fn);
            }
            st[sp++] = r;
            return sp;
        }
    }
}
