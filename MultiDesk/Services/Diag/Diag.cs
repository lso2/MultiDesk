using System;
using System.Globalization;
using System.IO;

namespace MultiDesk.Services
{
    /// <summary>
    /// The diagnostic trace writer. One chronological file per day under %APPDATA%\MultiDesk\logs,
    /// kept for two weeks, so a navigation fault noticed days after it happened is still on record.
    /// Defines Diag, whose Enabled and Verbose flags gate every call site in the app. Every write is
    /// guarded: a window manager may not die of its own logging. Channel and event names come from
    /// DiagChannels, value formatting from DiagFormat.
    /// Doc: docs/notes/MultiDesk/Services/Diag.md
    /// </summary>
    internal static class Diag
    {
        private static readonly object Gate = new object();
        private static string _folder;
        private static string _path;
        private static string _day;
        private const long RollBytes = 4L * 1024 * 1024;
        private const long BugRollBytes = 1L * 1024 * 1024;
        private const int KeepDays = 14;

        public static bool Enabled { get; set; }
        public static bool Verbose { get; set; }
        public static string Folder { get { return _folder; } }
        public static string SessionId { get; private set; }

        /// <summary>Prepare the log folder and open the session. Called once, after settings load.</summary>
        public static void Init(bool enabled, bool verbose)
        {
            Enabled = enabled;
            Verbose = verbose;
            SessionId = DateTime.Now.ToString("HHmmss", CultureInfo.InvariantCulture)
                + "-" + (Environment.TickCount & 0xFFF).ToString("X3", CultureInfo.InvariantCulture);
            try
            {
                _folder = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MultiDesk", "logs");
                Directory.CreateDirectory(_folder);
            }
            catch (Exception ex)
            {
                Log.Error("diagnostic log folder", ex);
                _folder = Path.GetTempPath();
            }
            Prune();
        }

        /// <summary>One trace line. Detail may be null.</summary>
        public static void Write(string channel, string evt, string detail)
        {
            if (!Enabled) return;
            Emit(channel, evt, detail);
        }

        /// <summary>A trace line that only appears when verbose tracing is on, for per-heartbeat
        /// sampling that would otherwise bury the events worth reading.</summary>
        public static void Detail(string channel, string evt, string detail)
        {
            if (!Enabled || !Verbose) return;
            Emit(channel, evt, detail);
        }

        /// <summary>A line written exactly as given, for the session banner.</summary>
        public static void Raw(string line)
        {
            if (!Enabled) return;
            Append(Resolve(), line);
        }

        /// <summary>Append one line to the separate anomaly file. It carries no day in its name, so
        /// it is rolled by size alone, keeping one previous generation.</summary>
        public static void Mirror(string line)
        {
            if (!Enabled || _folder == null) return;
            var path = Path.Combine(_folder, "bugs.log");
            try
            {
                var fi = new FileInfo(path);
                if (fi.Exists && fi.Length >= BugRollBytes)
                {
                    var previous = Path.Combine(_folder, "bugs.1.log");
                    if (File.Exists(previous)) File.Delete(previous);
                    File.Move(path, previous);
                }
            }
            catch { }
            Append(path, line);
        }

        private static void Emit(string channel, string evt, string detail)
        {
            var line = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture)
                + " [" + (channel ?? "?").PadRight(5) + "] " + (evt ?? "").PadRight(24)
                + (string.IsNullOrEmpty(detail) ? "" : " " + detail);
            Append(Resolve(), line);
        }

        // The current day file, rolled aside when it grows past the size cap so no single file
        // becomes too large to open.
        private static string Resolve()
        {
            var day = DateTime.Now.ToString("yyyyMMdd", CultureInfo.InvariantCulture);
            if (_path != null && day == _day)
            {
                try
                {
                    var fi = new FileInfo(_path);
                    if (!fi.Exists || fi.Length < RollBytes) return _path;
                }
                catch { return _path; }
            }
            bool newDay = _day != null && _day != day;
            _day = day;
            if (newDay) Prune(); // a session running past midnight prunes without waiting for a relaunch
            var baseDir = _folder ?? Path.GetTempPath();
            var candidate = Path.Combine(baseDir, "diag-" + day + ".log");
            try
            {
                var fi = new FileInfo(candidate);
                if (fi.Exists && fi.Length >= RollBytes)
                {
                    candidate = Path.Combine(baseDir, "diag-" + day + "-"
                        + DateTime.Now.ToString("HHmmss", CultureInfo.InvariantCulture) + ".log");
                    Prune();
                }
            }
            catch { }
            _path = candidate;
            return _path;
        }

        private static void Append(string path, string line)
        {
            if (path == null) return;
            try { lock (Gate) { File.AppendAllText(path, line + Environment.NewLine); } }
            catch { }
        }

        // Drop trace files older than the retention window. The anomaly file is never pruned.
        private static void Prune()
        {
            try
            {
                if (_folder == null || !Directory.Exists(_folder)) return;
                var cutoff = DateTime.Now.AddDays(-KeepDays);
                foreach (var f in Directory.GetFiles(_folder, "diag-*.log"))
                {
                    try { if (File.GetLastWriteTime(f) < cutoff) File.Delete(f); } catch { }
                }
            }
            catch (Exception ex) { Log.Error("diagnostic log prune", ex); }
        }
    }
}
