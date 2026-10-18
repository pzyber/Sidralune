using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace Sidralune.Core.Util
{
    /// <summary>RFC-4180 style CSV reader/writer with configurable separator and header lookup.</summary>
    public class CsvReader : IDisposable
    {
        private readonly TextReader _r;
        public char Separator;
        public string[] Header { get; private set; }
        private Dictionary<string, int> _index;

        public CsvReader(TextReader r, char sep = ',', bool hasHeader = true)
        {
            _r = r; Separator = sep;
            if (hasHeader)
            {
                Header = ReadRow();
                _index = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                if (Header != null)
                    for (int i = 0; i < Header.Length; i++)
                    {
                        var h = Header[i].Trim().Trim('"');
                        Header[i] = h;
                        if (!_index.ContainsKey(h)) _index[h] = i;
                    }
            }
        }

        public static CsvReader Open(string path, char sep = ',', bool hasHeader = true) =>
            new CsvReader(new StreamReader(path, Encoding.UTF8, true, 1 << 16), sep, hasHeader);

        public int Column(string name)
        {
            int i;
            return _index != null && _index.TryGetValue(name, out i) ? i : -1;
        }

        public int Column(params string[] names)
        {
            foreach (var n in names) { int c = Column(n); if (c >= 0) return c; }
            return -1;
        }

        /// <summary>Reads one logical row; returns null at end of stream.</summary>
        public string[] ReadRow()
        {
            var fields = new List<string>();
            var sb = new StringBuilder();
            bool inQuotes = false, any = false;
            while (true)
            {
                int c = _r.Read();
                if (c < 0)
                {
                    if (!any && sb.Length == 0 && fields.Count == 0) return null;
                    fields.Add(sb.ToString());
                    return fields.ToArray();
                }
                any = true;
                char ch = (char)c;
                if (inQuotes)
                {
                    if (ch == '"')
                    {
                        if (_r.Peek() == '"') { sb.Append('"'); _r.Read(); }
                        else inQuotes = false;
                    }
                    else sb.Append(ch);
                }
                else if (ch == '"' && sb.Length == 0) inQuotes = true;
                else if (ch == Separator) { fields.Add(sb.ToString()); sb.Clear(); }
                else if (ch == '\r') { }
                else if (ch == '\n') { fields.Add(sb.ToString()); return fields.ToArray(); }
                else sb.Append(ch);
            }
        }

        public void Dispose() => _r.Dispose();

        public static double D(string[] row, int col, double fallback = double.NaN)
        {
            if (col < 0 || col >= row.Length) return fallback;
            double v;
            var s = row[col].Trim();
            return s.Length > 0 && double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out v) ? v : fallback;
        }

        public static int I(string[] row, int col, int fallback = 0)
        {
            if (col < 0 || col >= row.Length) return fallback;
            int v;
            return int.TryParse(row[col].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out v) ? v : fallback;
        }

        public static string S(string[] row, int col) => col >= 0 && col < row.Length ? row[col].Trim() : "";
    }

    public static class CsvWriter
    {
        public static string Escape(string s, char sep = ',')
        {
            if (s == null) return "";
            if (s.IndexOf(sep) >= 0 || s.IndexOf('"') >= 0 || s.IndexOf('\n') >= 0)
                return "\"" + s.Replace("\"", "\"\"") + "\"";
            return s;
        }

        public static void Write(string path, IList<string> header, IEnumerable<IList<object>> rows, char sep = ',')
        {
            using (var w = new StreamWriter(path, false, new UTF8Encoding(false)))
            {
                w.WriteLine(string.Join(sep.ToString(), Map(header, sep)));
                foreach (var r in rows)
                {
                    var cells = new List<string>();
                    foreach (var o in r) cells.Add(Escape(Format(o), sep));
                    w.WriteLine(string.Join(sep.ToString(), cells));
                }
            }
        }

        private static IEnumerable<string> Map(IList<string> h, char sep) { foreach (var s in h) yield return Escape(s, sep); }

        public static string Format(object o)
        {
            if (o == null) return "";
            if (o is double) return ((double)o).ToString("R", CultureInfo.InvariantCulture);
            if (o is float) return ((float)o).ToString("R", CultureInfo.InvariantCulture);
            if (o is DateTime) return ((DateTime)o).ToString("yyyy-MM-ddTHH:mm:ss.fff", CultureInfo.InvariantCulture);
            var f = o as IFormattable;
            return f != null ? f.ToString(null, CultureInfo.InvariantCulture) : o.ToString();
        }
    }
}
