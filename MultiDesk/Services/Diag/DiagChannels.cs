using System;
using System.Collections.Generic;
using System.Text;

namespace MultiDesk.Services
{
    /// <summary>
    /// The channel manifest. Defines DiagChannels, the one ordered list of every trace channel the
    /// app writes, and the arming record that proves each channel's watcher actually started. A
    /// channel that never arms writes nothing and looks exactly like a channel with nothing to
    /// report, so the boot check names the difference out loud at startup.
    /// Doc: docs/notes/MultiDesk/Services/Diag.md
    /// </summary>
    internal static class DiagChannels
    {
        public const string Nav = "NAV";     // desktop switches
        public const string Fg = "FG";       // foreground changes and Alt+Tab landings
        public const string Park = "PARK";   // park and unpark mechanics
        public const string Geom = "GEOM";   // window geometry across a park and back
        public const string Order = "ORDER"; // icon order within the pane
        public const string Pane = "PANE";   // pane thickness, grid columns, padding slack
        public const string Track = "TRACK"; // window discovery, removal, heal repairs
        public const string Upd = "UPD";     // version and update checks
        public const string Bug = "BUG";     // anomalies

        /// <summary>Every channel the trace can carry.</summary>
        public static readonly string[] All = { Nav, Fg, Park, Geom, Order, Pane, Track, Upd, Bug };

        /// <summary>The channels a watcher owns, which are the ones the boot check can speak for. The
        /// anomaly channel has no watcher of its own: every detector writes to it, so it is live
        /// exactly when one of these is.</summary>
        public static readonly string[] Watched = { Nav, Fg, Park, Geom, Order, Pane, Track, Upd };

        private static readonly HashSet<string> Armed = new HashSet<string>(StringComparer.Ordinal);

        /// <summary>Called by each watcher once it is running and able to write.</summary>
        public static void Arm(string channel)
        {
            if (channel == null) return;
            lock (Armed) { Armed.Add(channel); }
        }

        public static bool IsArmed(string channel)
        {
            lock (Armed) { return channel != null && Armed.Contains(channel); }
        }

        /// <summary>Report which channels are live. Any channel listed in All but never armed is a
        /// watcher that failed to start, which is written to both the trace and the app log so an
        /// empty channel is never read as a quiet one.</summary>
        public static void BootCheck()
        {
            var live = new StringBuilder();
            var dark = new StringBuilder();
            lock (Armed)
            {
                foreach (var c in Watched)
                {
                    var target = Armed.Contains(c) ? live : dark;
                    if (target.Length > 0) target.Append(',');
                    target.Append(c);
                }
            }
            Diag.Write(Nav, "boot-check", "armed=" + (live.Length > 0 ? live.ToString() : "none")
                + " unarmed=" + (dark.Length > 0 ? dark.ToString() : "none"));
            if (dark.Length > 0)
                Log.Info("Diagnostic channels that never armed: " + dark + ". Those channels record nothing.");
        }
    }
}
