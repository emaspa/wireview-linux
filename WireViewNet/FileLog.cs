using System;
using System.Globalization;
using System.IO;

namespace WireView2.Net
{
    /// <summary>
    /// Minimal daily-rotating audit log. Writes timestamped lines to
    /// <c>wireview2-YYYY-MM-DD.log</c> in the configured directory (a new file per
    /// day), and prunes files older than the retention window. Used to record
    /// remote command activity (sent / received / executed) now that writes cross
    /// the LAN. Best-effort: any I/O error is swallowed so logging never breaks the app.
    /// </summary>
    public static class FileLog
    {
        private static readonly object _gate = new();
        private static string? _dir;
        private static int _retainDays = 14;
        private static DateTime _lastDay = DateTime.MinValue;

        /// <summary>Point the log at a directory and set retention (call once at startup).</summary>
        public static void Init(string dir, int retainDays)
        {
            lock (_gate) { _dir = dir; _retainDays = retainDays; }
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
            lock (_gate) dir = _dir;
            if (dir == null) return;
            try
            {
                var now = DateTime.Now;
                if (now.Date != _lastDay) { _lastDay = now.Date; Prune(); }
                string path = Path.Combine(dir, $"wireview2-{now:yyyy-MM-dd}.log");
                string line = $"{now:yyyy-MM-ddTHH:mm:ss} [{level}] {message}{Environment.NewLine}";
                lock (_gate) File.AppendAllText(path, line);
            }
            catch { /* best-effort */ }
        }

        private static void Prune()
        {
            string? dir;
            int days;
            lock (_gate) { dir = _dir; days = _retainDays; }
            if (dir == null || days <= 0) return;
            try
            {
                var cutoff = DateTime.Now.Date.AddDays(-days);
                foreach (var f in Directory.GetFiles(dir, "wireview2-*.log"))
                {
                    var stamp = Path.GetFileNameWithoutExtension(f).Replace("wireview2-", "");
                    if (DateTime.TryParseExact(stamp, "yyyy-MM-dd", CultureInfo.InvariantCulture,
                                               DateTimeStyles.None, out var d) && d.Date < cutoff)
                        File.Delete(f);
                }
            }
            catch { /* best-effort */ }
        }
    }
}
