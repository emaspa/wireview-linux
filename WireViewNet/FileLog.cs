using System;
using System.Globalization;
using System.IO;

namespace WireView2.Net
{
    /// <summary>
    /// Minimal daily-rotating audit log. Writes timestamped lines to
    /// <c>wireview2-YYYY-MM-DD.log</c> in the configured directory (a new file per
    /// day; the headless agent uses its own <c>wireview-agent-</c> prefix so the two
    /// processes never append to one file), and prunes files older than the
    /// retention window. Used to record
    /// remote command activity (sent / received / executed) now that writes cross
    /// the LAN. Best-effort: any I/O error is swallowed so logging never breaks the app.
    /// </summary>
    public static class FileLog
    {
        private static readonly object _gate = new();
        private static string? _dir;
        private static int _retainDays = 14;
        private static DateTime _lastDay = DateTime.MinValue;
        private static string _prefix = "wireview2-";

        /// <summary>Also copy every line here (the agent echoes to stderr).</summary>
        public static TextWriter? Echo { get; set; }

        /// <summary>Point the log at a directory and set retention (call once at startup).</summary>
        public static void Init(string dir, int retainDays) => Init(dir, retainDays, "wireview2");

        /// <summary>Point the log at a directory, set retention and the file name
        /// prefix (<c>&lt;prefix&gt;-YYYY-MM-DD.log</c>).</summary>
        public static void Init(string dir, int retainDays, string filePrefix)
        {
            lock (_gate) { _dir = dir; _retainDays = retainDays; _prefix = filePrefix + "-"; }
            try { Directory.CreateDirectory(dir); } catch { /* best-effort */ }
            Prune();
        }

        /// <summary>Update retention at runtime (e.g. when the setting changes).</summary>
        public static void SetRetentionDays(int days)
        {
            lock (_gate) _retainDays = days;
            Prune();
        }

        public static void Info(string message) => Write("INFO", message);
        public static void Warn(string message) => Write("WARN", message);

        public static void Write(string level, string message)
        {
            string? dir;
            string prefix;
            lock (_gate) { dir = _dir; prefix = _prefix; }
            var now = DateTime.Now;
            string line = $"{now:yyyy-MM-ddTHH:mm:ss} [{level}] {message}{Environment.NewLine}";
            var echo = Echo;
            if (echo != null)
                try { lock (_gate) echo.Write(line); } catch { /* best-effort */ }
            if (dir == null) return;
            try
            {
                if (now.Date != _lastDay) { _lastDay = now.Date; Prune(); }
                string path = Path.Combine(dir, $"{prefix}{now:yyyy-MM-dd}.log");
                lock (_gate) File.AppendAllText(path, line);
            }
            catch { /* best-effort */ }
        }

        private static void Prune()
        {
            string? dir;
            int days;
            string prefix;
            lock (_gate) { dir = _dir; days = _retainDays; prefix = _prefix; }
            if (dir == null || days <= 0) return;
            try
            {
                var cutoff = DateTime.Now.Date.AddDays(-days);
                foreach (var f in Directory.GetFiles(dir, prefix + "*.log"))
                {
                    var stamp = Path.GetFileNameWithoutExtension(f).Replace(prefix, "");
                    if (DateTime.TryParseExact(stamp, "yyyy-MM-dd", CultureInfo.InvariantCulture,
                                               DateTimeStyles.None, out var d) && d.Date < cutoff)
                        File.Delete(f);
                }
            }
            catch { /* best-effort */ }
        }
    }
}
