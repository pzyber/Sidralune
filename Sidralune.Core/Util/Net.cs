using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Text;
using System.Threading;

namespace Sidralune.Core.Util
{
    /// <summary>HTTP helpers on top of HttpWebRequest (no external dependencies).</summary>
    public static class Net
    {
        public static string UserAgent = "Sidralune/0.3.0 (astronomy suite)";

        static Net()
        {
            try { ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12; } catch { }
            ServicePointManager.DefaultConnectionLimit = Math.Max(ServicePointManager.DefaultConnectionLimit, 8);
        }

        private static HttpWebRequest Create(string url, string method, int timeoutMs)
        {
            var req = (HttpWebRequest)WebRequest.Create(url);
            req.Method = method;
            req.UserAgent = UserAgent;
            req.Timeout = timeoutMs;
            req.ReadWriteTimeout = timeoutMs;
            req.AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate;
            return req;
        }

        public static string GetString(string url, int timeoutMs = 60000, CancellationToken ct = default(CancellationToken))
        {
            var req = Create(url, "GET", timeoutMs);
            using (StopRequest.Or(ct).Register(() => req.Abort()))
            using (var resp = (HttpWebResponse)req.GetResponse())
            using (var s = resp.GetResponseStream())
            using (var r = new StreamReader(s, Encoding.UTF8))
                return r.ReadToEnd();
        }

        public static string PostForm(string url, IDictionary<string, string> form, int timeoutMs = 120000)
        {
            var sb = new StringBuilder();
            foreach (var kv in form)
            {
                if (sb.Length > 0) sb.Append('&');
                sb.Append(Uri.EscapeDataString(kv.Key)).Append('=').Append(Uri.EscapeDataString(kv.Value ?? ""));
            }
            return Post(url, "application/x-www-form-urlencoded", Encoding.UTF8.GetBytes(sb.ToString()), timeoutMs);
        }

        public static string Post(string url, string contentType, byte[] body, int timeoutMs = 120000, IDictionary<string, string> headers = null)
        {
            var req = Create(url, "POST", timeoutMs);
            req.ContentType = contentType;
            if (headers != null) foreach (var h in headers) req.Headers[h.Key] = h.Value;
            req.ContentLength = body.Length;
            using (StopRequest.Current.Register(() => req.Abort())) // a stopped job ends its request
            {
                using (var rs = req.GetRequestStream()) rs.Write(body, 0, body.Length);
                using (var resp = (HttpWebResponse)req.GetResponse())
                using (var r = new StreamReader(resp.GetResponseStream(), Encoding.UTF8))
                    return r.ReadToEnd();
            }
        }

        /// <summary>Multipart/form-data POST with one file part (used by nova.astrometry.net uploads).</summary>
        public static string PostMultipart(string url, IDictionary<string, string> fields, string fileField, string fileName, byte[] fileData, int timeoutMs = 300000)
        {
            string boundary = "----Sidralune" + Guid.NewGuid().ToString("N");
            var ms = new MemoryStream();
            var enc = Encoding.UTF8;
            Action<string> w = s => { var b = enc.GetBytes(s); ms.Write(b, 0, b.Length); };
            foreach (var kv in fields)
            {
                w("--" + boundary + "\r\n");
                w("Content-Type: text/plain\r\nMIME-Version: 1.0\r\nContent-disposition: form-data; name=\"" + kv.Key + "\"\r\n\r\n");
                w(kv.Value + "\r\n");
            }
            if (fileData != null)
            {
                w("--" + boundary + "\r\n");
                w("Content-Type: application/octet-stream\r\nMIME-Version: 1.0\r\nContent-disposition: form-data; name=\"" + fileField + "\"; filename=\"" + fileName + "\"\r\n\r\n");
                ms.Write(fileData, 0, fileData.Length);
                w("\r\n");
            }
            w("--" + boundary + "--\r\n");
            return Post(url, "multipart/form-data; boundary=" + boundary, ms.ToArray(), timeoutMs);
        }

        /// <summary>Download to a file with progress (0..1, or -1 if size unknown). Writes to .part then renames.</summary>
        public static void Download(string url, string path, Action<long, long> progress = null, CancellationToken ct = default(CancellationToken))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            string tmp = path + ".part";
            var req = Create(url, "GET", 600000);
            req.AutomaticDecompression = DecompressionMethods.None;
            try
            {
                using (StopRequest.Or(ct).Register(() => req.Abort()))
                using (var resp = (HttpWebResponse)req.GetResponse())
                using (var s = resp.GetResponseStream())
                using (var f = File.Create(tmp))
                {
                    long total = resp.ContentLength, done = 0;
                    var buf = new byte[1 << 16];
                    int n;
                    while ((n = s.Read(buf, 0, buf.Length)) > 0)
                    {
                        StopRequest.Or(ct).ThrowIfCancellationRequested();
                        f.Write(buf, 0, n);
                        done += n;
                        if (progress != null) progress(done, total);
                    }
                }
            }
            catch
            {
                // a stopped or failed download leaves no partial file behind
                try { File.Delete(tmp); } catch { }
                throw;
            }
            if (File.Exists(path)) File.Delete(path);
            File.Move(tmp, path);
        }

        /// <summary>Gunzip a file to a destination path.</summary>
        public static void Gunzip(string gzPath, string outPath)
        {
            using (var i = File.OpenRead(gzPath))
            using (var gz = new GZipStream(i, CompressionMode.Decompress))
            using (var o = File.Create(outPath))
                gz.CopyTo(o);
        }

        public static string UrlEncode(string s) => Uri.EscapeDataString(s ?? "");
    }

    /// <summary>Standard application folders.</summary>
    public static class AppPaths
    {
        private static string _root;

        public static string Root
        {
            get
            {
                if (_root == null)
                {
                    _root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Sidralune");
                    Directory.CreateDirectory(_root);
                }
                return _root;
            }
            set { _root = value; Directory.CreateDirectory(value); }
        }

        public static string Data => Ensure(Path.Combine(Root, "Data"));
        public static string Catalogs => Ensure(Path.Combine(Root, "Data", "Catalogs"));
        public static string Ephemerides => Ensure(Path.Combine(Root, "Data", "Ephemerides"));
        public static string Settings => Ensure(Path.Combine(Root, "Settings"));
        public static string Logs => Ensure(Path.Combine(Root, "Logs"));
        public static string Scripts => Ensure(Path.Combine(Root, "Scripts"));
        public static string Cache => Ensure(Path.Combine(Root, "Cache"));
        public static string Sessions => Ensure(Path.Combine(Root, "Sessions"));

        private static string Ensure(string p) { Directory.CreateDirectory(p); return p; }
    }

    /// <summary>Thread-safe application log with an event for the UI.</summary>
    public static class Log
    {
        public enum Level { Debug, Info, Warning, Error }
        public class Entry { public DateTime Time; public Level Level; public string Source; public string Message; }

        private static readonly List<Entry> _entries = new List<Entry>();
        private static readonly object _lock = new object();
        public static event Action<Entry> Logged;

        public static void Write(Level level, string source, string msg)
        {
            var e = new Entry { Time = DateTime.Now, Level = level, Source = source, Message = msg };
            lock (_lock)
            {
                _entries.Add(e);
                if (_entries.Count > 5000) _entries.RemoveRange(0, 1000);
                try { File.AppendAllText(Path.Combine(AppPaths.Logs, DateTime.Now.ToString("yyyy-MM-dd") + ".log"), string.Format("{0:HH:mm:ss.fff} [{1}] {2}: {3}\r\n", e.Time, level, source, msg)); } catch { }
            }
            var h = Logged;
            if (h != null) h(e);
        }

        public static void Info(string src, string msg) => Write(Level.Info, src, msg);
        public static void Warn(string src, string msg) => Write(Level.Warning, src, msg);
        public static void Error(string src, string msg) => Write(Level.Error, src, msg);
        public static void Error(string src, Exception ex) => Write(Level.Error, src, ex.Message);
        public static void Debug(string src, string msg) => Write(Level.Debug, src, msg);

        public static List<Entry> Snapshot() { lock (_lock) return new List<Entry>(_entries); }
    }
}
