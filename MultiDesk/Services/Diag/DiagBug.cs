using System;
using System.Collections.Generic;
using System.Globalization;

namespace MultiDesk.Services
{
    /// <summary>
    /// Anomaly reporting. Defines DiagBug, the one path every detector calls when a measurement
    /// disagrees with what the code intended. Every report reaches the chronological trace beside the
    /// events that led to it. The bugs.log mirror is throttled instead, by kind and subject and never
    /// by the measurements, so a detector whose numbers move on every reading cannot flood the one
    /// file that is meant to stay readable. Every held-back count is stated in the next mirrored line
    /// and again at shutdown.
    /// Doc: docs/notes/MultiDesk/Services/Diag.md
    /// </summary>
    internal static class DiagBug
    {
        private sealed class Entry
        {
            public long Total;
            public long HeldSinceMirror;
            public int LastMirrorTick;
        }

        private const int FreeReports = 5;        // the first reports of a kind always mirror
        private const int RepeatWindowMs = 60000; // afterwards, at most one mirrored line a minute
        private const int MaxSubjects = 512;      // window titles change constantly, so this is bounded

        private static readonly Dictionary<string, Entry> Seen = new Dictionary<string, Entry>(StringComparer.Ordinal);

        /// <summary>Report one anomaly. Kind is the stable identifier to grep for, subject names what
        /// it happened to (a window title, a desktop, the pane), detail carries the measurements.</summary>
        public static void Report(string kind, string subject, string detail)
        {
            if (!Diag.Enabled) return;
            try
            {
                var key = kind + "|" + subject;
                Entry e;
                bool mirror;
                long held = 0;
                lock (Seen)
                {
                    if (Seen.Count >= MaxSubjects && !Seen.ContainsKey(key))
                    {
                        Seen.Clear();
                        Diag.Write(DiagChannels.Bug, "counter-reset", MaxSubjects.ToString(CultureInfo.InvariantCulture)
                            + " distinct subjects had been counted, so the counts start again from here");
                    }
                    if (!Seen.TryGetValue(key, out e)) { e = new Entry(); Seen[key] = e; }
                    e.Total++;
                    mirror = e.Total <= FreeReports || Environment.TickCount - e.LastMirrorTick >= RepeatWindowMs;
                    if (mirror)
                    {
                        held = e.HeldSinceMirror;
                        e.HeldSinceMirror = 0;
                        e.LastMirrorTick = Environment.TickCount;
                    }
                    else e.HeldSinceMirror++;
                }

                var body = subject
                    + (string.IsNullOrEmpty(detail) ? "" : " | " + detail)
                    + " | seen=" + e.Total.ToString(CultureInfo.InvariantCulture);
                Diag.Write(DiagChannels.Bug, kind, body);
                if (!mirror) return;
                Diag.Mirror(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture)
                    + " [" + Diag.SessionId + "] " + kind + " " + body
                    + (held > 0
                        ? " (" + held.ToString(CultureInfo.InvariantCulture)
                            + " further reports of this kind were counted and left in the trace only)"
                        : ""));
            }
            catch { }
        }

        /// <summary>Write the counts that were held back, so shutting down never loses a number.</summary>
        public static void Flush()
        {
            if (!Diag.Enabled) return;
            try
            {
                lock (Seen)
                {
                    foreach (var kv in Seen)
                    {
                        if (kv.Value.HeldSinceMirror <= 0) continue;
                        Diag.Mirror(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture)
                            + " [" + Diag.SessionId + "] " + kv.Key.Replace("|", " ")
                            + " | " + kv.Value.HeldSinceMirror.ToString(CultureInfo.InvariantCulture)
                            + " further reports were counted after the last mirrored line, and are in the trace"
                            + " | seen=" + kv.Value.Total.ToString(CultureInfo.InvariantCulture));
                        kv.Value.HeldSinceMirror = 0;
                    }
                }
            }
            catch { }
        }

        /// <summary>How many times a kind has been reported this session, for a detector that wants
        /// to escalate its own detail after the first few.</summary>
        public static long CountOf(string kind, string subject)
        {
            try
            {
                lock (Seen)
                {
                    Entry e;
                    return Seen.TryGetValue(kind + "|" + subject, out e) ? e.Total : 0;
                }
            }
            catch { return 0; }
        }
    }
}
