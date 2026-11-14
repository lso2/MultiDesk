using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows.Threading;
using MultiDesk.Models;

namespace MultiDesk.Services
{
    /// <summary>
    /// Park and restore tracing. Defines DiagPark, which records the reading taken when a window
    /// leaves its desktop and checks the window against that same reading once it has come back and
    /// settled. A window restored to the size of the screen without the maximized state it was left
    /// in is reported here, with both readings on the line.
    /// Doc: docs/notes/MultiDesk/Services/Diag.md
    /// </summary>
    internal static class DiagPark
    {
        // An app repaints and re-derives its own geometry over several frames after a restore, so the
        // reading that decides whether the return was faithful is taken once that has finished.
        private const int SettleMs = 450;

        private sealed class Pending
        {
            public IntPtr Hwnd;
            public string Title;
            public int Level;
            public int DueTick;
            public DiagWinState Parked;
        }

        // The repair pass runs twice a second and a window it cannot move stays wrong forever, so
        // each window is written about at most this often.
        private const int RepeatWindowMs = 10000;

        private static readonly Dictionary<IntPtr, DiagWinState> AtPark = new Dictionary<IntPtr, DiagWinState>();
        private static readonly Dictionary<IntPtr, int> LastSaid = new Dictionary<IntPtr, int>();
        private static readonly List<Pending> Waiting = new List<Pending>();
        private static DispatcherTimer _settle;

        public static void Arm()
        {
            DiagChannels.Arm(DiagChannels.Park);
            DiagChannels.Arm(DiagChannels.Geom);
        }

        /// <summary>A window left its desktop. The reading taken while it was still standing where the
        /// user left it is what its return is measured against.</summary>
        public static void Parked(WindowModel w, string cause, int level, DiagWinState before, DiagWinState after)
        {
            if (!Diag.Enabled || w == null) return;
            try
            {
                lock (AtPark) { AtPark[w.Hwnd] = before; }
                Diag.Write(DiagChannels.Park, "parked", DiagFormat.Q(w.Title)
                    + " hwnd=" + DiagFormat.H(w.Hwnd)
                    + " desktop=" + w.DesktopIndex.ToString(CultureInfo.InvariantCulture)
                    + " level=" + level.ToString(CultureInfo.InvariantCulture)
                    + " cause=" + cause
                    + " | left " + DiagWindow.Describe(before)
                    + " | now " + DiagWindow.Describe(after));
            }
            catch { }
        }

        /// <summary>The mechanism holding a window moved up a step, which only happens when the step
        /// below was defeated by the window itself.</summary>
        public static void Escalated(WindowModel w, int from, int to, string reason)
        {
            if (!Diag.Enabled || w == null) return;
            Diag.Write(DiagChannels.Park, "escalated", DiagFormat.Q(w.Title)
                + " hwnd=" + DiagFormat.H(w.Hwnd)
                + " level=" + from.ToString(CultureInfo.InvariantCulture) + "->" + to.ToString(CultureInfo.InvariantCulture)
                + " reason=" + reason);
            DiagBug.Report("park-defeated", DiagFormat.Q(w.Title),
                "level=" + from.ToString(CultureInfo.InvariantCulture) + "->" + to.ToString(CultureInfo.InvariantCulture)
                + " reason=" + reason + " " + DiagWindow.Describe(DiagWindow.Capture(w.Hwnd)));
        }

        public static void Refused(WindowModel w, string reason)
        {
            if (!Diag.Enabled || w == null) return;
            if (!Due("refused", w.Hwnd)) return;
            Diag.Write(DiagChannels.Park, "refused", DiagFormat.Q(w.Title)
                + " hwnd=" + DiagFormat.H(w.Hwnd) + " reason=" + reason);
            DiagBug.Report("park-refused", DiagFormat.Q(w.Title), "reason=" + reason
                + " " + DiagWindow.Describe(DiagWindow.Capture(w.Hwnd)));
        }

        /// <summary>The repair pass found a window standing on screen that belongs to another
        /// desktop. A window this process cannot move stays in that state permanently, so the
        /// readings are taken and written at most once per window per repeat window.</summary>
        public static void HealRepark(WindowModel w, int activeIndex)
        {
            if (!Diag.Enabled || w == null) return;
            if (!Due("heal", w.Hwnd)) return;
            var reading = DiagWindow.Describe(DiagWindow.Capture(w.Hwnd));
            var detail = "desktop=" + w.DesktopIndex.ToString(CultureInfo.InvariantCulture)
                + " active=" + activeIndex.ToString(CultureInfo.InvariantCulture)
                + " parked-with=" + w.ParkedWith.ToString(CultureInfo.InvariantCulture)
                + " escalation=" + w.ParkEscalation.ToString(CultureInfo.InvariantCulture)
                + " " + reading;
            Diag.Write(DiagChannels.Track, "heal-repark", DiagFormat.Q(w.Title)
                + " hwnd=" + DiagFormat.H(w.Hwnd) + " " + detail);
            DiagBug.Report("window-on-screen-off-its-desktop", DiagFormat.Q(w.Title), detail);
        }

        // True when this window has not been written about under this label recently.
        private static bool Due(string label, IntPtr hwnd)
        {
            try
            {
                var key = new IntPtr(hwnd.ToInt64() ^ label.GetHashCode());
                lock (LastSaid)
                {
                    int last;
                    if (LastSaid.TryGetValue(key, out last) && Environment.TickCount - last < RepeatWindowMs)
                        return false;
                    LastSaid[key] = Environment.TickCount;
                    return true;
                }
            }
            catch { return true; }
        }

        public static void Skipped(WindowModel w, string reason)
        {
            if (w == null) return;
            Diag.Detail(DiagChannels.Park, "skipped", DiagFormat.Q(w.Title)
                + " hwnd=" + DiagFormat.H(w.Hwnd) + " reason=" + reason);
        }

        /// <summary>A window came back to its desktop. The fidelity check is queued rather than run
        /// now, because the window has not finished rebuilding its own geometry yet.</summary>
        public static void Unparked(WindowModel w, string cause, int level)
        {
            if (!Diag.Enabled || w == null) return;
            try
            {
                Diag.Write(DiagChannels.Park, "unparked", DiagFormat.Q(w.Title)
                    + " hwnd=" + DiagFormat.H(w.Hwnd)
                    + " desktop=" + w.DesktopIndex.ToString(CultureInfo.InvariantCulture)
                    + " level=" + level.ToString(CultureInfo.InvariantCulture)
                    + " cause=" + cause);
                DiagWinState parked;
                lock (AtPark)
                {
                    if (!AtPark.TryGetValue(w.Hwnd, out parked)) return;
                    AtPark.Remove(w.Hwnd);
                }
                lock (Waiting)
                {
                    Waiting.Add(new Pending
                    {
                        Hwnd = w.Hwnd,
                        Title = w.Title,
                        Level = level,
                        DueTick = Environment.TickCount + SettleMs,
                        Parked = parked
                    });
                }
                EnsureTimer();
            }
            catch { }
        }

        /// <summary>Forget a window that is gone, so its park reading cannot outlive it.</summary>
        public static void Forget(IntPtr hwnd)
        {
            try
            {
                lock (AtPark) { AtPark.Remove(hwnd); }
                lock (Waiting) { Waiting.RemoveAll(p => p.Hwnd == hwnd); }
                lock (LastSaid)
                {
                    foreach (var k in new List<IntPtr>(LastSaid.Keys))
                        if ((k.ToInt64() ^ "heal".GetHashCode()) == hwnd.ToInt64()
                            || (k.ToInt64() ^ "refused".GetHashCode()) == hwnd.ToInt64()) LastSaid.Remove(k);
                }
            }
            catch { }
        }

        private static void EnsureTimer()
        {
            if (_settle != null) { if (!_settle.IsEnabled) _settle.Start(); return; }
            _settle = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
            _settle.Tick += OnSettleTick;
            _settle.Start();
        }

        private static void OnSettleTick(object sender, EventArgs e)
        {
            var due = new List<Pending>();
            try
            {
                lock (Waiting)
                {
                    for (int i = Waiting.Count - 1; i >= 0; i--)
                    {
                        if (Environment.TickCount - Waiting[i].DueTick < 0) continue;
                        due.Add(Waiting[i]);
                        Waiting.RemoveAt(i);
                    }
                    if (Waiting.Count == 0 && _settle != null) _settle.Stop();
                }
            }
            catch { }
            foreach (var p in due) Verify(p);
        }

        private static void Verify(Pending p)
        {
            try
            {
                var now = DiagWindow.Capture(p.Hwnd);
                if (!now.Alive) return;
                var fault = DiagWindow.RestoreFault(p.Parked, now);
                var line = "level=" + p.Level.ToString(CultureInfo.InvariantCulture)
                    + " | left " + DiagWindow.Describe(p.Parked)
                    + " | back " + DiagWindow.Describe(now);
                if (fault == null)
                {
                    Diag.Detail(DiagChannels.Geom, "restore-faithful", DiagFormat.Q(p.Title) + " " + line);
                    return;
                }
                Diag.Write(DiagChannels.Geom, "restore-differs", DiagFormat.Q(p.Title) + " fault=" + fault
                    + " " + line + " | changed " + DiagWindow.Differences(p.Parked, now));
                DiagBug.Report(fault, DiagFormat.Q(p.Title), line);
            }
            catch { }
        }
    }
}
