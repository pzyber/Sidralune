using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace Sidralune.Core.Util
{
    /// <summary>
    /// The language of the user interface. Text is written in English in the code and marked with T("…"); the
    /// translation is looked up in a gettext .po file in the Languages folder, and missing translations show in English.
    /// Only text changes: numbers and dates are always formatted with the invariant culture.
    /// </summary>
    public static class Lang
    {
        /// <summary>The pseudo-language: every text accented, bracketed and made longer, to find untranslated text and tight layouts.</summary>
        public const string PseudoCode = "qps";

        private static PoCatalog _catalog;

        /// <summary>The folder with the .po files, next to the program.</summary>
        public static string Folder = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Languages");

        /// <summary>The language in use: "en", a .po file's code (e.g. "sv", "nb") or PseudoCode.</summary>
        public static string Code { get; private set; } = "en";

        /// <summary>
        /// Uses a language: a code such as "sv" or "nb-NO", "" for the Windows display language, PseudoCode, or "en".
        /// A language without a .po file falls back to English. Returns the code in use.
        /// </summary>
        public static string Use(string code)
        {
            _catalog = null;
            Code = "en";
            if (string.IsNullOrEmpty(code)) code = SystemLanguage();
            var file = code == PseudoCode ? null : Resolve(code);
            if (code == PseudoCode) Code = PseudoCode;
            else if (file != null)
                try
                {
                    _catalog = PoCatalog.Load(file);
                    Code = Path.GetFileNameWithoutExtension(file);
                }
                catch (Exception ex) { Log.Warn("Language", "Could not read " + file + ": " + ex.Message); }
            DateNames = NamesOf(Code);
            return Code;
        }

        /// <summary>The invariant culture with the day and month names of the language in use, for dates shown as words ("ddd", "MMMM").</summary>
        public static CultureInfo DateNames { get; private set; } = CultureInfo.InvariantCulture;

        private static CultureInfo NamesOf(string code)
        {
            if (code == "en") return CultureInfo.InvariantCulture;
            try
            {
                var names = code == PseudoCode ? PseudoNames() : new CultureInfo(code).DateTimeFormat;
                var c = (CultureInfo)CultureInfo.InvariantCulture.Clone();
                var f = c.DateTimeFormat;
                f.DayNames = names.DayNames; f.AbbreviatedDayNames = names.AbbreviatedDayNames; f.ShortestDayNames = names.ShortestDayNames;
                f.MonthNames = names.MonthNames; f.AbbreviatedMonthNames = names.AbbreviatedMonthNames;
                f.MonthGenitiveNames = names.MonthGenitiveNames; f.AbbreviatedMonthGenitiveNames = names.AbbreviatedMonthGenitiveNames;
                return CultureInfo.ReadOnly(c);
            }
            catch (CultureNotFoundException) { return CultureInfo.InvariantCulture; }
        }

        // the invariant day and month names in the pseudo-language, so dates written in words show up too
        private static DateTimeFormatInfo PseudoNames()
        {
            var f = (DateTimeFormatInfo)CultureInfo.InvariantCulture.DateTimeFormat.Clone();
            Func<string[], string[]> p = a => a.Select(x => x.Length == 0 ? x : Pseudo(x)).ToArray();
            f.DayNames = p(f.DayNames); f.AbbreviatedDayNames = p(f.AbbreviatedDayNames); f.ShortestDayNames = p(f.ShortestDayNames);
            f.MonthNames = p(f.MonthNames); f.AbbreviatedMonthNames = p(f.AbbreviatedMonthNames);
            f.MonthGenitiveNames = p(f.MonthGenitiveNames); f.AbbreviatedMonthGenitiveNames = p(f.AbbreviatedMonthGenitiveNames);
            return f;
        }

        /// <summary>The Windows display language, as a code ("sv-SE"); Norwegian "no" counts as Bokmål.</summary>
        public static string SystemLanguage() => CultureInfo.CurrentUICulture.Name;

        // the .po file for a code: the exact one (pt-BR) or the language alone (pt); English needs none
        private static string Resolve(string code)
        {
            var names = new List<string> { code };
            var neutral = code.Split('-')[0].ToLowerInvariant();
            if (neutral == "no") neutral = "nb";
            names.Add(neutral);
            if (neutral == "en") return null;
            return names.Select(n => Path.Combine(Folder, n + ".po")).FirstOrDefault(File.Exists);
        }

        /// <summary>The languages with a .po file, as (code, name in that language), English first.</summary>
        public static List<KeyValuePair<string, string>> Available()
        {
            var list = new List<KeyValuePair<string, string>> { new KeyValuePair<string, string>("en", "English") };
            if (Directory.Exists(Folder))
                foreach (var f in Directory.GetFiles(Folder, "*.po").OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
                {
                    var code = Path.GetFileNameWithoutExtension(f);
                    list.Add(new KeyValuePair<string, string>(code, NativeName(code)));
                }
            return list;
        }

        /// <summary>A language's name in that language, capitalised ("Svenska", "Norsk bokmål").</summary>
        public static string NativeName(string code)
        {
            try
            {
                var name = new CultureInfo(code).NativeName;
                return name.Length > 0 ? char.ToUpper(name[0], CultureInfo.InvariantCulture) + name.Substring(1) : code;
            }
            catch (CultureNotFoundException) { return code; }
        }

        /// <summary>The text in the language in use.</summary>
        public static string T(string text) => Translate(null, text, null, 0, false);

        /// <summary>The format in the language in use, filled in: invariant numbers, the language's day and month names.</summary>
        public static string T(string format, params object[] args) => string.Format(DateNames, Translate(null, format, null, 0, false), args);

        /// <summary>The text for a context, for English words that need different translations ("Open" a file, an "open" cluster).</summary>
        public static string Tc(string context, string text) => Translate(context, text, null, 0, false);

        /// <summary>The singular or plural form for n, filled in with args, or with n when there are none.</summary>
        public static string Tn(string singular, string plural, long n, params object[] args) =>
            string.Format(DateNames, Translate(null, singular, plural, n, true), args.Length == 0 ? new object[] { n } : args);

        /// <summary>A member or enum name split into words: "UntilAltitudeBelow" is "Until Altitude Below".</summary>
        public static string Words(string name)
        {
            var sb = new StringBuilder();
            for (int i = 0; i < name.Length; i++)
            {
                if (i > 0 && char.IsUpper(name[i]) && !char.IsUpper(name[i - 1])) sb.Append(' ');
                sb.Append(name[i]);
            }
            return sb.ToString();
        }

        /// <summary>An option name or enum value as shown in a property grid: its words, translated with the context "option".</summary>
        public static string Option(string name) => Tc("option", Words(name));

        [ThreadStatic] private static bool _english;

        /// <summary>
        /// Runs f with every text in English, for texts that are also kept as data (the type stored with a planner entry):
        /// shared labels such as DeepSkyObject.TypeLabel then give the English that is saved and read back.
        /// </summary>
        public static TResult InEnglish<TResult>(Func<TResult> f)
        {
            bool was = _english;
            _english = true;
            try { return f(); }
            finally { _english = was; }
        }

        private static string Translate(string context, string id, string plural, long n, bool counted)
        {
            if (id == null) return null;
            if (_english) return counted && n != 1 ? plural : id;
            if (Code == PseudoCode) return Pseudo(counted && n != 1 ? plural : id);
            var c = _catalog;
            var found = c?.Find(context, id, counted ? n : (long?)null);
            if (found != null) return found;
            return counted && n != 1 ? plural : id;
        }

        private const string Plain = "aceinorsuyACEINORSUYdgklt", Accented = "åçéîñöŕšûýÅÇÉÎÑÖŘŠÛÝđĝķĺţ";

        /// <summary>Text in the pseudo-language: accented letters, a third longer, in brackets; {0} placeholders and line breaks kept.</summary>
        public static string Pseudo(string text)
        {
            var sb = new StringBuilder("[");
            int depth = 0, letters = 0;
            foreach (var ch in text)
            {
                if (ch == '{') depth++;
                if (depth == 0 && char.IsLetter(ch)) letters++;
                int k = depth == 0 ? Plain.IndexOf(ch) : -1;
                sb.Append(k >= 0 ? Accented[k] : ch);
                if (ch == '}' && depth > 0) depth--;
            }
            return sb.Append(' ').Append('~', Math.Max(1, letters / 3)).Append(']').ToString();
        }

        /// <summary>
        /// Marks text for translation without translating it, for text kept as data and translated where it is shown
        /// with T(variable): the name of a dataset is logged in English and shown in the interface language.
        /// </summary>
        public static string N(string text) => text;

        /// <summary>The texts marked for translation in C# source: T("…"), N("…"), Tc("context", "…") and Tn("…", "…", n).</summary>
        public static List<PoEntry> Extract(string source)
        {
            const string lit = "\"(?:[^\"\\\\\\r\\n]|\\\\.)*\"";
            string text = "(?<{0}>" + lit + ")(?:\\s*\\+\\s*(?<{0}>" + lit + "))*";
            var call = new Regex("(?<![\\w])(?:(?<f>[TN])\\(\\s*" + string.Format(text, "a") +
                "|(?<f>Tc)\\(\\s*" + string.Format(text, "c") + "\\s*,\\s*" + string.Format(text, "a") +
                "|(?<f>Tn)\\(\\s*" + string.Format(text, "a") + "\\s*,\\s*" + string.Format(text, "p") + ")");
            Func<Group, string> join = g => g.Success ? string.Concat(g.Captures.Cast<Capture>().Select(x => Unescape(x.Value.Substring(1, x.Value.Length - 2)))) : null;
            var list = new List<PoEntry>();
            // comments, which may show the calls in their text, are left out
            source = string.Join("\n", source.Split('\n').Select(l => l.TrimStart().StartsWith("//") ? "" : l));
            foreach (Match m in call.Matches(source))
                list.Add(new PoEntry { Context = join(m.Groups["c"]), Id = join(m.Groups["a"]), Plural = join(m.Groups["p"]) });
            return list;
        }

        /// <summary>The texts of many source files (name, text), each once, in order of first use, with the files that use it.</summary>
        public static List<PoEntry> Collect(IEnumerable<KeyValuePair<string, string>> files)
        {
            var list = new List<PoEntry>();
            var byKey = new Dictionary<string, PoEntry>();
            foreach (var f in files)
                foreach (var e in Extract(f.Value))
                {
                    PoEntry seen;
                    if (!byKey.TryGetValue(e.Key, out seen)) { byKey[e.Key] = seen = e; list.Add(e); }
                    if (seen.Plural == null) seen.Plural = e.Plural;
                    if (!seen.References.Contains(f.Key)) seen.References.Add(f.Key);
                }
            return list;
        }

        /// <summary>The numbers of the {n} placeholders in a format, in order ("{1:0.0} of {0}" gives 0, 1).</summary>
        public static List<int> Placeholders(string format) =>
            Regex.Matches(format.Replace("{{", "").Replace("}}", ""), "\\{(\\d+)[^}]*\\}").Cast<Match>().Select(m => int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture)).Distinct().OrderBy(n => n).ToList();

        private static string Unescape(string s)
        {
            if (s.IndexOf('\\') < 0) return s;
            var sb = new StringBuilder();
            for (int i = 0; i < s.Length; i++)
            {
                if (s[i] != '\\' || i + 1 >= s.Length) { sb.Append(s[i]); continue; }
                char e = s[++i];
                switch (e)
                {
                    case 'n': sb.Append('\n'); break;
                    case 'r': sb.Append('\r'); break;
                    case 't': sb.Append('\t'); break;
                    case '0': sb.Append('\0'); break;
                    case 'u': sb.Append((char)Convert.ToInt32(s.Substring(i + 1, 4), 16)); i += 4; break;
                    default: sb.Append(e); break;
                }
            }
            return sb.ToString();
        }
    }

    /// <summary>One text of a .po file: its context, English text (and plural), and translations.</summary>
    public class PoEntry
    {
        public string Context, Id, Plural;
        public string[] Strings = new string[0];
        public bool Fuzzy;
        /// <summary>Where the text is used (source files), written as #: comments for translators.</summary>
        public List<string> References = new List<string>();
        public string Key => (Context != null ? Context + "\u0004" : "") + Id;
    }

    /// <summary>A gettext catalogue: the translations of one .po file.</summary>
    public class PoCatalog
    {
        public readonly List<PoEntry> Entries = new List<PoEntry>();
        public readonly Dictionary<string, string> Header = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, PoEntry> _byKey = new Dictionary<string, PoEntry>();
        private string _pluralRule = "n != 1";

        /// <summary>The form to use for n (0 for the singular in most languages), from the Plural-Forms header.</summary>
        public int PluralIndex(long n)
        {
            try { return (int)new PluralRule(_pluralRule, n).Evaluate(); }
            catch (FormatException) { return n != 1 ? 1 : 0; }
        }

        /// <summary>The translation, or null when there is none (untranslated or fuzzy). n picks the plural form.</summary>
        public string Find(string context, string id, long? n = null)
        {
            PoEntry e;
            if (!_byKey.TryGetValue((context != null ? context + "\u0004" : "") + id, out e) || e.Fuzzy) return null;
            int k = n.HasValue && e.Plural != null ? PluralIndex(n.Value) : 0;
            return k < e.Strings.Length && !string.IsNullOrEmpty(e.Strings[k]) ? e.Strings[k] : null;
        }

        public static PoCatalog Load(string path) => Parse(File.ReadAllText(path, Encoding.UTF8));

        public static PoCatalog Parse(string text)
        {
            var cat = new PoCatalog();
            PoEntry e = null;
            string field = null;
            int index = 0;
            bool fuzzy = false;
            var strings = new SortedDictionary<int, string>();
            Action finish = () =>
            {
                if (e != null && e.Id != null)
                {
                    e.Strings = Enumerable.Range(0, strings.Count == 0 ? 0 : strings.Keys.Max() + 1).Select(i => strings.ContainsKey(i) ? strings[i] : "").ToArray();
                    e.Fuzzy = fuzzy;
                    if (e.Id == "" && e.Context == null) cat.ReadHeader(e.Strings.FirstOrDefault() ?? "");
                    else { cat.Entries.Add(e); cat._byKey[e.Key] = e; }
                }
                e = null; field = null; fuzzy = false; strings = new SortedDictionary<int, string>();
            };
            Action<string> append = s =>
            {
                if (field == "ctx") e.Context += s;
                else if (field == "id") e.Id += s;
                else if (field == "plural") e.Plural += s;
                else if (field == "str") strings[index] = (strings.ContainsKey(index) ? strings[index] : "") + s;
            };
            var refs = new List<string>();
            foreach (var raw in text.Split('\n'))
            {
                var line = raw.Trim();
                if (line.Length == 0) { finish(); continue; }
                if (line.StartsWith("#"))
                {
                    if (e != null && e.Id != null && strings.Count > 0) finish();
                    if (line.StartsWith("#,") && line.Contains("fuzzy")) fuzzy = true;
                    if (line.StartsWith("#:")) refs.AddRange(line.Substring(2).Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries));
                    continue;
                }
                if (line.StartsWith("\"")) { if (e != null) append(Unquote(line)); continue; }
                int sp = line.IndexOf(' ');
                if (sp < 0) throw new FormatException("Unexpected line in .po file: " + line);
                string key = line.Substring(0, sp), value = Unquote(line.Substring(sp + 1).Trim());
                if ((key == "msgctxt" || key == "msgid") && e != null && strings.Count > 0) finish();
                if (e == null) { e = new PoEntry { References = refs }; refs = new List<string>(); }
                if (key == "msgctxt") { field = "ctx"; e.Context = value; }
                else if (key == "msgid") { field = "id"; e.Id = value; }
                else if (key == "msgid_plural") { field = "plural"; e.Plural = value; }
                else if (key == "msgstr") { field = "str"; index = 0; strings[0] = value; }
                else if (key.StartsWith("msgstr[") && key.EndsWith("]")) { field = "str"; index = int.Parse(key.Substring(7, key.Length - 8), CultureInfo.InvariantCulture); strings[index] = value; }
                else throw new FormatException("Unknown keyword in .po file: " + key);
            }
            finish();
            return cat;
        }

        private void ReadHeader(string text)
        {
            foreach (var line in text.Split('\n'))
            {
                int c = line.IndexOf(':');
                if (c > 0) Header[line.Substring(0, c).Trim()] = line.Substring(c + 1).Trim();
            }
            string forms;
            if (Header.TryGetValue("Plural-Forms", out forms))
            {
                int p = forms.IndexOf("plural=", StringComparison.Ordinal);
                if (p >= 0) _pluralRule = forms.Substring(p + 7).TrimEnd(';', ' ');
            }
        }

        private static string Unquote(string s)
        {
            if (s.Length < 2 || s[0] != '"' || s[s.Length - 1] != '"') throw new FormatException("Expected a quoted string in .po file: " + s);
            var sb = new StringBuilder();
            for (int i = 1; i < s.Length - 1; i++)
            {
                if (s[i] != '\\' || i + 1 >= s.Length - 1) { sb.Append(s[i]); continue; }
                char c = s[++i];
                sb.Append(c == 'n' ? '\n' : c == 't' ? '\t' : c == 'r' ? '\r' : c);
            }
            return sb.ToString();
        }

        /// <summary>The catalogue as .po text: the header, then every entry with its translations.</summary>
        public static string Write(IEnumerable<KeyValuePair<string, string>> header, IEnumerable<PoEntry> entries, int pluralForms = 2)
        {
            var sb = new StringBuilder();
            sb.Append("msgid \"\"\nmsgstr \"\"\n");
            foreach (var h in header) sb.Append(Quote(h.Key + ": " + h.Value + "\n")).Append('\n');
            foreach (var e in entries)
            {
                sb.Append('\n');
                if (e.References.Count > 0) sb.Append("#: ").Append(string.Join(" ", e.References)).Append('\n');
                if (e.Fuzzy) sb.Append("#, fuzzy\n");
                if (e.Context != null) sb.Append("msgctxt ").Append(Quote(e.Context)).Append('\n');
                sb.Append("msgid ").Append(Quote(e.Id)).Append('\n');
                if (e.Plural != null)
                {
                    sb.Append("msgid_plural ").Append(Quote(e.Plural)).Append('\n');
                    for (int i = 0; i < pluralForms; i++) sb.Append("msgstr[").Append(i).Append("] ").Append(Quote(i < e.Strings.Length ? e.Strings[i] : "")).Append('\n');
                }
                else sb.Append("msgstr ").Append(Quote(e.Strings.Length > 0 ? e.Strings[0] : "")).Append('\n');
            }
            return sb.ToString();
        }

        private static string Quote(string s) => "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\t", "\\t").Replace("\r", "\\r").Replace("\n", "\\n") + "\"";

        // a C-like plural expression of n, as in Plural-Forms: n != 1, n > 1, n%10==1 && n%100!=11 ? 0 : 1 …
        private class PluralRule
        {
            private readonly string _s;
            private readonly long _n;
            private int _i;

            public PluralRule(string s, long n) { _s = s; _n = n; }

            public long Evaluate()
            {
                long v = Ternary();
                Space();
                if (_i != _s.Length) throw new FormatException("Unexpected text in plural rule: " + _s.Substring(_i));
                return v;
            }

            private void Space() { while (_i < _s.Length && char.IsWhiteSpace(_s[_i])) _i++; }
            private bool Take(string op) { Space(); if (string.CompareOrdinal(_s, _i, op, 0, op.Length) != 0) return false; _i += op.Length; return true; }
            private void Expect(string op) { if (!Take(op)) throw new FormatException("Expected " + op + " in plural rule"); }

            private long Ternary()
            {
                long c = Or();
                if (!Take("?")) return c;
                long a = Ternary();
                Expect(":");
                long b = Ternary();
                return c != 0 ? a : b;
            }

            private long Or() { long v = And(); while (Take("||")) { long r = And(); v = v != 0 || r != 0 ? 1 : 0; } return v; }
            private long And() { long v = Equality(); while (Take("&&")) { long r = Equality(); v = v != 0 && r != 0 ? 1 : 0; } return v; }

            private long Equality()
            {
                long v = Relation();
                while (true)
                {
                    if (Take("==")) v = v == Relation() ? 1 : 0;
                    else if (Take("!=")) v = v != Relation() ? 1 : 0;
                    else return v;
                }
            }

            private long Relation()
            {
                long v = Sum();
                while (true)
                {
                    if (Take("<=")) v = v <= Sum() ? 1 : 0;
                    else if (Take(">=")) v = v >= Sum() ? 1 : 0;
                    else if (Take("<")) v = v < Sum() ? 1 : 0;
                    else if (Take(">")) v = v > Sum() ? 1 : 0;
                    else return v;
                }
            }

            private long Sum()
            {
                long v = Product();
                while (true)
                {
                    if (Take("+")) v += Product();
                    else if (Take("-")) v -= Product();
                    else return v;
                }
            }

            private long Product()
            {
                long v = Unary();
                while (true)
                {
                    if (Take("*")) v *= Unary();
                    else if (Take("/")) { long d = Unary(); v = d == 0 ? 0 : v / d; }
                    else if (Take("%")) { long d = Unary(); v = d == 0 ? 0 : v % d; }
                    else return v;
                }
            }

            private long Unary()
            {
                if (Take("!")) return Unary() == 0 ? 1 : 0;
                if (Take("(")) { long v = Ternary(); Expect(")"); return v; }
                if (Take("n")) return _n;
                Space();
                int start = _i;
                while (_i < _s.Length && char.IsDigit(_s[_i])) _i++;
                if (_i == start) throw new FormatException("Expected a number in plural rule: " + _s);
                return long.Parse(_s.Substring(start, _i - start), CultureInfo.InvariantCulture);
            }
        }
    }
}
