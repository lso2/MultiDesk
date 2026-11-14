using System;
using MultiDesk.Models;
using NM = MultiDesk.Interop.NativeMethods;

namespace MultiDesk.Services
{
    /// <summary>
    /// Foreground bookkeeping that keeps focus-follow honest. There is no custom Alt+Tab gesture to
    /// manage here: cross-desktop switching goes through the real, unmodified Windows Alt+Tab
    /// directly, which the parking engine in DesktopManager.Visibility.cs makes possible by keeping
    /// off-desktop windows genuinely WS_VISIBLE, just relocated off screen, instead of hidden, so the
    /// OS's own switcher already enumerates and thumbnails every desktop's windows on its own with
    /// nothing drawn by MultiDesk. What is left to do here is smaller: when the OS reports a new
    /// foreground window, decide which of three things it is. The user landing on a window through a
    /// normal route (a click, a real Alt+Tab commit) syncs MultiDesk's own ActiveIndex and sidebar
    /// highlight, switching desktops if the window lives on another one. Windows picking a successor
    /// on its own right after something was minimized, closed, or parked follows nobody anywhere,
    /// and if the successor it picked is a parked window (parked windows stay WS_VISIBLE, so they
    /// are eligible in a way hidden windows never were), the foreground is handed straight back to
    /// the active desktop so keystrokes never land on a window no monitor shows. Anything else
    /// holding foreground while still off screen is corrected immediately regardless of any
    /// setting, because nothing may hold foreground while off screen.
    /// </summary>
    public sealed partial class DesktopManager
    {
        // One-shot guard: right after focus is placed deliberately, an unrequested grab by a different
        // window is reverted once. Media windows re-assert right after a switch touches them.
        private IntPtr _fgGuardHwnd;
        private int _fgGuardUntil;
        private bool _fgGuardUsed;

        // Suppresses focus-follow for a moment after MultiDesk itself minimizes or closes a window.
        // Windows then hands foreground to whatever it judges is next in line, which is not a choice
        // the user made and must never be read as "the user wants to visit that window's desktop."
        private int _suppressFocusFollowUntil;
        public void SuppressFocusFollow() { _suppressFocusFollowUntil = Environment.TickCount + 700; }

        // The window MultiDesk itself most recently parked while it still held the foreground, and
        // when. A desktop switch parks the window the user was focused on while it keeps the
        // foreground for a few more milliseconds, so its own stale foreground event arrives looking
        // exactly like a window stuck off screen; recording the park lets that one event be told
        // apart from a real user action and ignored.
        private IntPtr _parkedFgHwnd;
        private int _parkedFgAt;
        internal void NoteParkedForeground(IntPtr hwnd) { _parkedFgHwnd = hwnd; _parkedFgAt = Environment.TickCount; }

        // Timestamps of the last three stuck-driven switches, plus the cooldown gate, for the circuit
        // breaker in SetForeground: four stuck-driven switches inside one second is a feedback loop,
        // never a person.
        private int _stuckSwitch1, _stuckSwitch2, _stuckSwitch3;
        private int _stuckCooldownUntil;

        /// <summary>Called by the tracker when a window is being minimized or destroyed, whoever did
        /// it, MultiDesk or the window's own title-bar buttons, which WindowActions cannot see. Only a
        /// window leaving the active desktop's screen makes Windows pick a foreground successor, so
        /// only that case arms the suppression; a background window closing changes nothing about
        /// where input goes.</summary>
        public void NoteWindowLeaving(IntPtr hwnd)
        {
            WindowModel m;
            if (_byHwnd.TryGetValue(hwnd, out m) && m.DesktopIndex == ActiveIndex) SuppressFocusFollow();
        }

        // When the last foreground EVENT arrived. The heal pass exempts the focused window from
        // repair only while a correction could still be in flight, and corrections only ever start
        // from an event, so a stale focused window (one whose event long passed) gets repaired like
        // any other instead of holding a permanent exemption.
        private int _fgEventAt;

        // The window that held the foreground before the current event, kept so a stuck foreground
        // can be classified structurally: a hand-off Windows performed because the previous window
        // vanished (closed, hid itself, or minimized) versus a destination the user chose while
        // standing somewhere real.
        private IntPtr _prevFg;

        // The system task-switcher windows foreground passes through during a real Alt+Tab. A
        // committed Alt+Tab leaves one of these as the "previous" foreground, and it hides itself the
        // instant it closes, which must never be mistaken for the previous window vanishing.
        private static bool IsTaskChooser(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero) return false;
            var c = NM.ClassOf(hwnd);
            return c == "XamlExplorerHostIslandWindow" || c == "ForegroundStaging"
                || c == "MultitaskingViewFrame" || c == "TaskSwitcherWnd"
                || c == "TaskListThumbnailWnd" || c == "Windows.UI.Core.CoreWindow";
        }

        // The classes that are only ever the switcher itself. IsTaskChooser above is deliberately
        // wider, since it decides an exemption where a false positive is harmless. This one decides
        // whether the trace calls a foreground change a hand-picked Alt+Tab, where a false positive
        // would put ordinary focus changes on record as faults, so it leaves out ForegroundStaging
        // and Windows.UI.Core.CoreWindow, both of which ordinary windows also use.
        private static bool IsAltTabCommit(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero) return false;
            var c = NM.ClassOf(hwnd);
            return c == "XamlExplorerHostIslandWindow" || c == "MultitaskingViewFrame"
                || c == "TaskSwitcherWnd" || c == "TaskListThumbnailWnd";
        }

        /// <summary>Update the active-window highlight and, optionally, follow focus across desktops.</summary>
        public void SetForeground(IntPtr hwnd)
        {
            _fgEventAt = Environment.TickCount;
            IntPtr prevFg = _prevFg;
            _prevFg = hwnd;
            WindowModel active = null;
            foreach (var kv in _byHwnd)
            {
                var w = kv.Value;
                bool on = (w.Hwnd == hwnd);
                if (w.IsActive != on) w.IsActive = on;
                if (on) active = w;
            }

            // The system task switcher held the foreground right up to this event, which makes this a
            // window the user picked by hand in Alt+Tab. The trace carries that fact, so a landing
            // somewhere else can be told apart from a hand-off Windows performed on its own. Read
            // only while tracing is on: nothing outside the trace uses it.
            bool viaChooser = Diag.Enabled && IsAltTabCommit(prevFg);

            // One-shot revert: an unrequested grab by a different tracked window right after focus was
            // placed deliberately gets pushed back once, which stops a media window from planting
            // itself in front the moment a switch shows or hides it.
            if (active != null && _fgGuardHwnd != IntPtr.Zero && hwnd != _fgGuardHwnd && !_fgGuardUsed
                && Environment.TickCount < _fgGuardUntil && NM.IsWindow(_fgGuardHwnd))
            {
                _fgGuardUsed = true;
                DiagNav.Foreground(hwnd, active.Title, active.DesktopIndex, ActiveIndex,
                    DiagNav.OutGuardRevert, viaChooser, _fgGuardHwnd, null);
                WindowActions.ForceForeground(_fgGuardHwnd);
                return;
            }

            if (active == null)
            {
                DiagNav.ForegroundQuiet(hwnd, -1, ActiveIndex, DiagNav.OutUntracked);
                return;
            }
            if (active.DesktopIndex == ActiveIndex)
            {
                DiagNav.ForegroundQuiet(hwnd, active.DesktopIndex, ActiveIndex, DiagNav.OutSameDesktop);
                return;
            }

            // A window MultiDesk itself parked, at any level, taking the foreground is a
            // cross-desktop selection or a hand-off no matter what its current visual state reads
            // as: a level-2 park is a minimize, which Alt+Tab un-minimizes on commit, so by the time
            // this event arrives the window can look perfectly ordinary while still being a parked
            // window the user just chose. Routing every parked case through the stuck pipeline keeps
            // one set of guards (hand-off detection, loop breakers) deciding all of it.
            bool onScreen = OnScreen(hwnd);
            bool stuckOffScreen = !onScreen || active.ParkedWith >= 0;

            // The readings every decision below is made from, built only while tracing is on so an
            // ordinary foreground change costs nothing when it is off.
            string state = Diag.Enabled
                ? "onscreen=" + (onScreen ? "1" : "0")
                    + " parked-with=" + active.ParkedWith + " escalation=" + active.ParkEscalation
                    + " prev=" + DiagFormat.H(prevFg) + " prev-state=" + DiagFormat.Flags(prevFg)
                    + " since-park=" + DiagFormat.Ms(_parkedFgAt)
                : null;

            if (stuckOffScreen)
            {
                // Every switch parks the window the user was just focused on while it still holds
                // the foreground for a few more milliseconds, so its own stale foreground event
                // lands here looking exactly like a window stuck off screen. Answering it with a
                // switch back would park the other side's focused window in turn, manufacturing the
                // next stale event, a self-sustaining ping-pong needing no user input at all, whose
                // visible form is desktops snapping back and forth while the focus-forcing that
                // rides along floods the input system. The window MultiDesk itself just parked
                // while focused is therefore never followed: the switch that parked it already has
                // a deliberate focus placement in flight, and doing nothing here lets it land.
                if (hwnd == _parkedFgHwnd && Environment.TickCount - _parkedFgAt < 500)
                {
                    DiagNav.Foreground(hwnd, active.Title, active.DesktopIndex, ActiveIndex,
                        DiagNav.OutSelfParkedFg, viaChooser, IntPtr.Zero, state);
                    return;
                }

                // Parking or hiding the focused window also makes Windows hand the foreground to a
                // successor of its own choosing within a few milliseconds, and with parked windows
                // staying WS_VISIBLE that successor is often one of them. Any stuck foreground
                // arriving this hard on the heels of a focused-window park is that hand-off, not a
                // person, who cannot re-Alt+Tab in a quarter second; it is steered back instead of
                // followed.
                if (Environment.TickCount - _parkedFgAt < 250)
                {
                    var steer = TopOfActiveDesktop(null);
                    DiagNav.Foreground(hwnd, active.Title, active.DesktopIndex, ActiveIndex,
                        DiagNav.OutHandoffPark, viaChooser,
                        steer != null ? steer.Hwnd : IntPtr.Zero, state);
                    if (steer != null) WindowActions.ForceForeground(steer.Hwnd);
                    return;
                }

                // Structural hand-off detection, immune to event timing: when the PREVIOUS foreground
                // window has vanished (closed, hid itself, or minimized), this foreground change is
                // Windows replacing a dead spot, never the user picking a destination, so it must not
                // switch desktops no matter when its event arrives; closing or minimizing the last
                // window of a desktop stays on that desktop. A committed Alt+Tab leaves the system
                // task switcher as the previous window, and it hides itself on close, which is why
                // the chooser is recognized and exempted rather than read as a vanish.
                bool prevVanished = prevFg != IntPtr.Zero && prevFg != hwnd
                    && (!NM.IsWindow(prevFg) || !NM.IsWindowVisible(prevFg) || NM.IsIconic(prevFg));
                if (prevVanished && !IsTaskChooser(prevFg))
                {
                    var steer = TopOfActiveDesktop(null);
                    DiagNav.Foreground(hwnd, active.Title, active.DesktopIndex, ActiveIndex,
                        DiagNav.OutHandoffVanished, viaChooser,
                        steer != null ? steer.Hwnd : IntPtr.Zero, state);
                    if (steer != null) WindowActions.ForceForeground(steer.Hwnd);
                    return;
                }

                // Circuit breaker: whatever still manages to manufacture stuck events, honoring
                // them at feedback-loop speed is never right, so during the cooldown they are
                // answered by steering focus back to the active desktop instead of switching, which
                // starves any loop instead of feeding it.
                if (Environment.TickCount < _stuckCooldownUntil)
                {
                    var t = TopOfActiveDesktop(null);
                    DiagNav.Foreground(hwnd, active.Title, active.DesktopIndex, ActiveIndex,
                        DiagNav.OutBreakerCooldown, viaChooser, t != null ? t.Hwnd : IntPtr.Zero,
                        state + " cooldown-remaining=" + (_stuckCooldownUntil - Environment.TickCount) + "ms");
                    if (t != null) WindowActions.ForceForeground(t.Hwnd);
                    return;
                }
            }

            // Right after MultiDesk itself (or a title-bar button the tracker's minimize hook saw)
            // minimized, closed, or parked a window, a foreground change is Windows picking a
            // successor on its own, never the user picking a destination, so it must not switch
            // desktops. For an on-screen window that means simply not following it. A parked window
            // needs one step more: parked windows stay WS_VISIBLE, which makes them eligible
            // successors in a way a hidden window never was, and leaving one focused would send every
            // keystroke to a window no monitor shows, so the foreground is handed straight back to
            // the active desktop instead.
            if (Environment.TickCount < _suppressFocusFollowUntil)
            {
                string left = " suppress-remaining=" + (_suppressFocusFollowUntil - Environment.TickCount) + "ms";
                if (stuckOffScreen)
                {
                    var top = TopOfActiveDesktop(null);
                    DiagNav.Foreground(hwnd, active.Title, active.DesktopIndex, ActiveIndex,
                        DiagNav.OutSuppressedSteer, viaChooser, top != null ? top.Hwnd : IntPtr.Zero,
                        state + left);
                    if (top != null) WindowActions.ForceForeground(top.Hwnd);
                    return;
                }
                DiagNav.Foreground(hwnd, active.Title, active.DesktopIndex, ActiveIndex,
                    DiagNav.OutSuppressed, viaChooser, IntPtr.Zero, state + left);
                return;
            }

            // A window on another desktop taking foreground while genuinely on screen went through
            // some legitimate on-screen route, so the focus-follow setting decides whether to follow.
            // Taking foreground while still off screen (a real Alt+Tab commit on a parked window, a
            // notification activating itself, a relaunch of a single-instance app) is corrected
            // regardless of that setting, because a window cannot usefully stay both foreground and
            // off screen.
            if (!stuckOffScreen && !_settings.Current.AutoSwitchOnForeground)
            {
                DiagNav.Foreground(hwnd, active.Title, active.DesktopIndex, ActiveIndex,
                    DiagNav.OutNoFollowSetting, viaChooser, IntPtr.Zero, state);
                return;
            }
            // A second top-level window of a process that already has a window on the active desktop
            // is reunited here instead of switching the desktop to wherever it was tracked, so an
            // IDE's floating tool panel, a file picker, or a secondary frame taking focus cannot pull
            // the user onto an unrelated desktop and surface its other windows. HIDE MODE ONLY.
            // Under parking, every situation that once justified a reunite is already handled
            // without moving any window between desktops: a new panel opening focused stays on the
            // active desktop by assignment (EnsureWindow), a parked window taking foreground is a
            // hand-off or an Alt+Tab commit (the guards above and the switch below), and an
            // off-desktop window found genuinely on screen is a violation state the heal pass parks,
            // so reuniting it would relocate a window the user placed, which is exactly the
            // windows-migrate-to-the-current-desktop bug. Windows move desktops when the user moves
            // them, never as a side effect of focus.
            if (!ParkingEnabled && !stuckOffScreen && TryReuniteSibling(active))
            {
                DiagNav.Foreground(hwnd, active.Title, active.DesktopIndex, ActiveIndex,
                    DiagNav.OutReunited, viaChooser, hwnd, state);
                WindowActions.ForceForeground(hwnd);
                return;
            }
            if (!stuckOffScreen && Environment.TickCount < _switchGuardUntil)
            {
                DiagNav.Foreground(hwnd, active.Title, active.DesktopIndex, ActiveIndex,
                    DiagNav.OutSwitchGuard, viaChooser, IntPtr.Zero,
                    state + " guard-remaining=" + (_switchGuardUntil - Environment.TickCount) + "ms");
                return;
            }
            if (stuckOffScreen)
            {
                // Trip the breaker on the fourth stuck-driven switch inside one second: a person
                // switching desktops cannot do that, a feedback loop cannot avoid it.
                int now = Environment.TickCount;
                if (now - _stuckSwitch3 < 1000)
                {
                    _stuckCooldownUntil = now + 1200;
                    Log.Info("Stuck-switch circuit breaker tripped; stuck foreground events ignored for 1.2s.");
                    var t = TopOfActiveDesktop(null);
                    DiagNav.Foreground(hwnd, active.Title, active.DesktopIndex, ActiveIndex,
                        DiagNav.OutBreakerTrip, viaChooser, t != null ? t.Hwnd : IntPtr.Zero,
                        state + " four-switches-within=" + (now - _stuckSwitch3) + "ms");
                    if (t != null) WindowActions.ForceForeground(t.Hwnd);
                    return;
                }
                _stuckSwitch3 = _stuckSwitch2; _stuckSwitch2 = _stuckSwitch1; _stuckSwitch1 = now;
            }
            DiagNav.Foreground(hwnd, active.Title, active.DesktopIndex, ActiveIndex,
                DiagNav.OutSwitched, viaChooser, IntPtr.Zero, state);
            SwitchTo(active.DesktopIndex, false, true, viaChooser ? "alt-tab-commit" : "focus-follow");
            WindowActions.ForceForeground(hwnd);
        }

        // Arm the one-shot foreground guard around a deliberate focus placement.
        private void GuardForeground(IntPtr hwnd)
        {
            _fgGuardHwnd = hwnd;
            _fgGuardUntil = Environment.TickCount + 600;
            _fgGuardUsed = false;
        }

        // The topmost tracked window of the active desktop that is actually standing on screen,
        // skipping the given one, or null when the active desktop has nothing restorable to focus.
        // Used wherever the foreground must be steered back onto the active desktop after Windows or
        // a park operation put it somewhere off screen. Minimized windows are skipped deliberately:
        // ForceForeground restores a minimized window as its first step, so steering focus to one
        // here would un-minimize it, and the most common way this code runs is milliseconds after
        // the user minimized exactly that window, whose minimize must stick. When nothing qualifies,
        // the caller leaves the foreground alone, which suppression already keeps from switching
        // desktops.
        private WindowModel TopOfActiveDesktop(WindowModel except)
        {
            var d = DesktopAt(ActiveIndex);
            if (d == null) return null;
            for (int i = d.Windows.Count - 1; i >= 0; i--)
            {
                var c = d.Windows[i];
                if (c == except) continue;
                if (c.Hwnd == IntPtr.Zero || !NM.IsWindow(c.Hwnd)) continue;
                if (NM.IsIconic(c.Hwnd)) continue;
                return c;
            }
            return null;
        }
    }
}
