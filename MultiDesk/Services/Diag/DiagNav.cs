using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows.Threading;
using NM = MultiDesk.Interop.NativeMethods;

namespace MultiDesk.Services
{
    /// <summary>
    /// Navigation tracing. Defines DiagNav, which records the outcome every foreground event was
    /// given and then reads back where focus actually landed once the switch has settled. A commit
    /// arriving from the system task switcher is a window the user chose by hand, so a landing that
    /// does not match it names the guard that decided otherwise. Also records desktop switches, the
    /// launch desktop and the outcome-code vocabulary the trace is written in.
    /// Doc: docs/notes/MultiDesk/Services/Diag.md
    /// </summary>
    internal static class DiagNav
    {
        // Outcomes, one per decision SetForeground can reach. The name in the trace is the name here.
        public const string OutUntracked = "untracked";
        public const string OutSameDesktop = "same-desktop";
        public const string OutGuardRevert = "guard-revert";
        public const string OutSelfParkedFg = "self-parked-foreground";
        public const string OutHandoffPark = "handoff-after-park";
        public const string OutHandoffVanished = "handoff-previous-vanished";
        public const string OutBreakerCooldown = "breaker-cooldown";
        public const string OutBreakerTrip = "breaker-tripped";
        public const string OutSuppressed = "suppressed";
        public const string OutSuppressedSteer = "suppressed-steered-back";
        public const string OutNoFollowSetting = "follow-focus-off";
        public const string OutGrabSteeredBack = "focus-grab-steered-back";
        public const string OutReunited = "reunited-sibling";
        public const string OutSwitchGuard = "switch-guard";
        public const string OutSwitched = "switched";

        // Focus is forced right after a decision and the target app answers over the next few frames,
        // so the reading that says where the user ended up is taken once that has finished.
        private const int LandingMs = 550;
        private const int PingPongWindowMs = 1500;
        private const int PingPongCount = 4;

        private sealed class Landing
        {
            public IntPtr Hwnd;
            public string Title;
            public int WindowDesktop;
            public int ActiveAtDecision;
            public string Outcome;
            public bool ViaTaskChooser;
            public IntPtr Steered;
            public int DueTick;
        }

        private static readonly List<Landing> Waiting = new List<Landing>();
        private static readonly int[] SwitchTicks = new int[PingPongCount];
        private static DispatcherTimer _settle;
        private static int _lastActive = -1;
        private static string _switchCause;
        private static int _switchCauseTick;

        /// <summary>The active desktop as of the last heartbeat sample, which the landing check reads
        /// so it needs no reference to the desktop manager.</summary>
        public static int LastActive { get { return _lastActive; } }

        public static void Arm()
        {
            DiagChannels.Arm(DiagChannels.Nav);
            DiagChannels.Arm(DiagChannels.Fg);
        }

        /// <summary>A desktop switch, with what asked for it.</summary>
        public static void Switched(int from, int to, string cause, int windowCount, IntPtr activated)
        {
            if (!Diag.Enabled) return;
            try
            {
                _lastActive = to;
                _switchCause = cause;
                _switchCauseTick = Environment.TickCount;
                Diag.Write(DiagChannels.Nav, "switch", "desktop=" + from.ToString(CultureInfo.InvariantCulture)
                    + "->" + to.ToString(CultureInfo.InvariantCulture)
                    + " cause=" + cause
                    + " windows=" + windowCount.ToString(CultureInfo.InvariantCulture)
                    + " activated=" + (activated == IntPtr.Zero ? "none" : DiagFormat.H(activated)));
                if (from == to) return;
                for (int i = SwitchTicks.Length - 1; i > 0; i--) SwitchTicks[i] = SwitchTicks[i - 1];
                SwitchTicks[0] = Environment.TickCount;
                int oldest = SwitchTicks[SwitchTicks.Length - 1];
                if (oldest != 0 && Environment.TickCount - oldest < PingPongWindowMs)
                    DiagBug.Report("desktop-switch-ping-pong", "desktops",
                        PingPongCount.ToString(CultureInfo.InvariantCulture) + " switches within "
                        + (Environment.TickCount - oldest).ToString(CultureInfo.InvariantCulture)
                        + "ms, last cause=" + cause);
            }
            catch { }
        }

        /// <summary>The decision a foreground event was given. Tracked windows get their landing read
        /// back; everything else is a trace line only.</summary>
        public static void Foreground(IntPtr hwnd, string title, int windowDesktop, int activeDesktop,
            string outcome, bool viaTaskChooser, IntPtr steered, string detail)
        {
            if (!Diag.Enabled) return;
            try
            {
                Diag.Write(DiagChannels.Fg, outcome, DiagFormat.Q(title)
                    + " hwnd=" + DiagFormat.H(hwnd)
                    + " desktop=" + windowDesktop.ToString(CultureInfo.InvariantCulture)
                    + " active=" + activeDesktop.ToString(CultureInfo.InvariantCulture)
                    + (viaTaskChooser ? " via=alt-tab" : "")
                    + " state=" + DiagFormat.Flags(hwnd)
                    + (steered == IntPtr.Zero ? "" : " focus-to=" + DiagFormat.H(steered)
                        + (steered == NM.GetShellWindow() ? " (desktop)" : ""))
                    + (string.IsNullOrEmpty(detail) ? "" : " | " + detail));
                if (windowDesktop < 0) return;
                lock (Waiting)
                {
                    Waiting.RemoveAll(l => l.Hwnd == hwnd);
                    Waiting.Add(new Landing
                    {
                        Hwnd = hwnd,
                        Title = title,
                        WindowDesktop = windowDesktop,
                        ActiveAtDecision = activeDesktop,
                        Outcome = outcome,
                        ViaTaskChooser = viaTaskChooser,
                        Steered = steered,
                        DueTick = Environment.TickCount + LandingMs
                    });
                }
                EnsureTimer();
            }
            catch { }
        }

        /// <summary>A foreground change that needs no landing read: an untracked window, or one already
        /// on the active desktop. Recorded for sequence only, at verbose level.</summary>
        public static void ForegroundQuiet(IntPtr hwnd, int windowDesktop, int activeDesktop, string outcome)
        {
            if (!Diag.Enabled || !Diag.Verbose) return;
            Diag.Detail(DiagChannels.Fg, outcome, DiagFormat.Q(NM.TextOf(hwnd))
                + " hwnd=" + DiagFormat.H(hwnd)
                + " desktop=" + windowDesktop.ToString(CultureInfo.InvariantCulture)
                + " active=" + activeDesktop.ToString(CultureInfo.InvariantCulture));
        }

        /// <summary>The active desktop moved through a path other than a switch: a desktop was
        /// reordered, removed, or the count was lowered. Recorded so the heartbeat sample does not
        /// read an accounted-for move as one nobody asked for.</summary>
        public static void ActiveChanged(int from, int to, string cause)
        {
            _lastActive = to;
            _switchCause = cause;
            _switchCauseTick = Environment.TickCount;
            if (from == to) return;
            Diag.Write(DiagChannels.Nav, "active-desktop-moved", "desktop="
                + from.ToString(CultureInfo.InvariantCulture) + "->" + to.ToString(CultureInfo.InvariantCulture)
                + " cause=" + cause);
        }

        /// <summary>The desktop chosen at launch, the rule that chose it, and the foreground window at
        /// that moment.</summary>
        public static void Started(int active, string reason, IntPtr foreground)
        {
            _lastActive = active;
            _switchCause = "startup-" + reason;
            _switchCauseTick = Environment.TickCount;
            if (!Diag.Enabled) return;
            Diag.Write(DiagChannels.Nav, "startup-desktop", "active=" + active.ToString(CultureInfo.InvariantCulture)
                + " reason=" + reason + " foreground=" + DiagFormat.H(foreground)
                + " " + DiagFormat.Q(foreground == IntPtr.Zero ? "" : NM.TextOf(foreground)));
        }

        /// <summary>Sampled on the tracker heartbeat. The active desktop changing with no switch on
        /// record means something moved the user without going through the one path that switches.</summary>
        public static void SampleActive(int activeIndex)
        {
            if (!Diag.Enabled) return;
            if (_lastActive == -1) { _lastActive = activeIndex; return; }
            if (activeIndex == _lastActive) return;
            int was = _lastActive;
            _lastActive = activeIndex;
            if (Environment.TickCount - _switchCauseTick < 2000) return; // a switch just recorded it
            DiagBug.Report("desktop-changed-with-no-cause", "desktops",
                "desktop=" + was.ToString(CultureInfo.InvariantCulture)
                + "->" + activeIndex.ToString(CultureInfo.InvariantCulture)
                + " last-recorded-cause=" + (_switchCause ?? "none"));
        }

        public static void Forget(IntPtr hwnd)
        {
            try { lock (Waiting) { Waiting.RemoveAll(l => l.Hwnd == hwnd); } }
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
            var due = new List<Landing>();
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
            foreach (var l in due) DiagNavLanding.Check(l.Hwnd, l.Title, l.WindowDesktop, l.ActiveAtDecision,
                l.Outcome, l.ViaTaskChooser, l.Steered);
        }
    }
}
