using System;
using System.Diagnostics;
using System.IO;

namespace MultiDesk.Services
{
    /// <summary>
    /// Console-style logger. Writes to the Visual Studio Output window through Trace and appends to
    /// %APPDATA%\MultiDesk\debug.log, falling back to the temp folder. It never touches the Windows
    /// Event Log. Logging must never crash the app, so every write is guarded.
    /// </summary>
    internal static class Log
    {
        private static readonly object Gate = new object();
        private static string _path;

        public static void Init()
        {
            try
            {
                var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MultiDesk");
                Directory.CreateDirectory(dir);
                _path = Path.Combine(dir, "debug.log");
                RollIfLarge();
            }
            catch
            {
                _path = Path.Combine(Path.GetTempPath(), "multidesk.log");
            }
        }

        public static void Info(string message) { Write("INFO ", message); }

        public static void Error(string message) { Write("ERROR", message); }

        public static void Error(string message, Exception ex)
        {
            Write("ERROR", message + " :: " + ex.GetType().Name + ": " + ex.Message + Environment.NewLine + ex.StackTrace);
        }

        private static void Write(string level, string message)
        {
            if (_path == null) _path = Path.Combine(Path.GetTempPath(), "multidesk.log");
            var line = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff") + " [" + level + "] " + message;
            try { Trace.WriteLine(line); } catch { }
            try
            {
                lock (Gate) { File.AppendAllText(_path, line + Environment.NewLine); }
            }
            catch { }
        }

        private static void RollIfLarge()
        {
            try
            {
                var fi = new FileInfo(_path);
                if (fi.Exists && fi.Length > 1024 * 1024)
                {
                    var bak = _path + ".1";
                    if (File.Exists(bak)) File.Delete(bak);
                    File.Move(_path, bak);
                }
            }
            catch { }
        }
    }
}
