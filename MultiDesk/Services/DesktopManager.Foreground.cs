using System;
using MultiDesk.Models;
using NM = MultiDesk.Interop.NativeMethods;

namespace MultiDesk.Services
{
    /// <summary>
    /// Foreground decisions. Part of DesktopManager: a new foreground window on another desktop is
    /// followed with a desktop switch, handed back to the active desktop, or left alone. A person
    /// picking the window through Alt+Tab, the taskbar, the tray overflow or Start always switches.
    /// A window grabbing focus on its own switches only while Follow focus across desktops is on.
    /// Focus never stays on a window off screen: it goes to the active desktop's top window, or to
    /// the Windows desktop when the active desktop shows none.
    /// Doc: docs/notes/MultiDesk/Services/DesktopManager.Foreground.cs.md
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

            // The shell route whose window held the foreground right up to this event, if any. With
            // recent input, a window reaching the foreground out of Alt+Tab, the taskbar, the tray
            // overflow or Start is a window a person picked there (ShellRoutes.InputRecent). The trace
            // marks the Alt+Tab case, and the landing check reports focus MultiDesk sent away from it.
            string route = ShellRoutes.RouteOf(prevFg);
            bool viaChooser = route == ShellRoutes.AltTab;

            // The previous foreground window closed, hid itself or was minimized.
            bool prevVanished = prevFg != IntPtr.Zero && prevFg != hwnd
                && (!NM.IsWindow(prevFg) || !NM.IsWindowVisible(prevFg) || NM.IsIconic(prevFg));

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
                    var steer = ActiveDesktopFocusTarget(prevFg);
                    DiagNav.Foreground(hwnd, active.Title, active.DesktopIndex, ActiveIndex,
                        DiagNav.OutHandoffPark, viaChooser, steer, state);
                    FocusActiveDesktop(steer);
                    return;
                }

                // Structural hand-off detection, immune to event timing: when the PREVIOUS foreground
                // window has vanished (closed, hid itself, or minimized), this foreground change is
                // Windows replacing a dead spot, never the user picking a destination, so it must not
                // switch desktops no matter when its event arrives; closing or minimizing the last
                // window of a desktop stays on that desktop. A pick through Alt+Tab, a taskbar
                // thumbnail, the tray overflow or Start leaves that shell window as the previous
                // window, hidden by its own close. A shell route is exempt.
                if (prevVanished && route == null)
                {
                    var steer = ActiveDesktopFocusTarget(prevFg);
                    DiagNav.Foreground(hwnd, active.Title, active.DesktopIndex, ActiveIndex,
                        DiagNav.OutHandoffVanished, viaChooser, steer, state);
                    FocusActiveDesktop(steer);
                    return;
                }

                // Circuit breaker: whatever still manages to manufacture stuck events, honoring
                // them at feedback-loop speed is never right, so during the cooldown they are
                // answered by steering focus back to the active desktop instead of switching, which
                // starves any loop instead of feeding it.
                if (Environment.TickCount < _stuckCooldownUntil)
                {
                    var t = ActiveDesktopFocusTarget(prevFg);
                    DiagNav.Foreground(hwnd, active.Title, active.DesktopIndex, ActiveIndex,
                        DiagNav.OutBreakerCooldown, viaChooser, t,
                        state + " cooldown-remaining=" + (_stuckCooldownUntil - Environment.TickCount) + "ms");
                    FocusActiveDesktop(t);
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
                    var top = ActiveDesktopFocusTarget(prevFg);
                    DiagNav.Foreground(hwnd, active.Title, active.DesktopIndex, ActiveIndex,
                        DiagNav.OutSuppressedSteer, viaChooser, top, state + left);
                    FocusActiveDesktop(top);
                    return;
                }
                DiagNav.Foreground(hwnd, active.Title, active.DesktopIndex, ActiveIndex,
                    DiagNav.OutSuppressed, viaChooser, IntPtr.Zero, state + left);
                return;
            }

            // A person picking a window on another desktop through Alt+Tab, the taskbar, the tray
            // overflow or Start always switches to it. Anything else is the window grabbing focus on
            // its own, and Follow focus across desktops decides whether the desktop follows it, for
            // parked and on-screen windows alike. Under parking, a grab that is not followed hands
            // focus back to the active desktop, and a grabbing window standing on screen goes back to
            // its own desktop.
            // Recent input with the previous window still in place, or after a shell route window,
            // is the person's own pick. With input that recent, Windows grants the foreground only
            // through that input or the foreground app (SetForegroundWindow remarks), and a taskbar
            // click is input to the taskbar.
            bool userPicked = ShellRoutes.InputRecent() && (route != null || !prevVanished);
            if (!userPicked && !_settings.Current.AutoSwitchOnForeground)
            {
                if (stuckOffScreen || ParkingEnabled)
                {
                    var back = ActiveDesktopFocusTarget(prevFg);
                    DiagNav.Foreground(hwnd, active.Title, active.DesktopIndex, ActiveIndex,
                        DiagNav.OutGrabSteeredBack, viaChooser, back, state);
                    // Parking a focused window hands focus back as part of the park.
                    if (stuckOffScreen) FocusActiveDesktop(back);
                    else HideWin(active);
                    return;
                }
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
                    var t = ActiveDesktopFocusTarget(prevFg);
                    DiagNav.Foreground(hwnd, active.Title, active.DesktopIndex, ActiveIndex,
                        DiagNav.OutBreakerTrip, viaChooser, t,
                        state + " four-switches-within=" + (now - _stuckSwitch3) + "ms");
                    FocusActiveDesktop(t);
                    return;
                }
                _stuckSwitch3 = _stuckSwitch2; _stuckSwitch2 = _stuckSwitch1; _stuckSwitch1 = now;
            }
            DiagNav.Foreground(hwnd, active.Title, active.DesktopIndex, ActiveIndex,
                DiagNav.OutSwitched, viaChooser, IntPtr.Zero, state);
            SwitchTo(active.DesktopIndex, userPicked ? (route ?? "input") + "-pick" : "focus-follow", false);
            WindowActions.ForceForeground(hwnd);
        }

        // Arm the one-shot foreground guard around a deliberate focus placement.
        private void GuardForeground(IntPtr hwnd)
        {
            _fgGuardHwnd = hwnd;
            _fgGuardUntil = Environment.TickCount + 600;
            _fgGuardUsed = false;
        }

        // The highest window in the z-order that belongs to the active desktop and stands on screen,
        // skipping the given one, or null when the active desktop shows none. Windows hands the
        // foreground down the z-order, and this is the same successor with other desktops' windows
        // left out. ForceForeground restores a minimized window as its first step, and minimized
        // windows are skipped: this runs most often milliseconds after the user minimized a window,
        // and that minimize must stick. A hide mode switch shows windows asynchronously, and passes
        // requireOnScreen false.
        private WindowModel TopOfActiveDesktop(WindowModel except, bool requireOnScreen = true)
        {
            for (IntPtr h = NM.GetTopWindow(IntPtr.Zero); h != IntPtr.Zero; h = NM.GetWindow(h, NM.GW_HWNDNEXT))
            {
                WindowModel c;
                if (!_byHwnd.TryGetValue(h, out c) || c == except || c.DesktopIndex != ActiveIndex) continue;
                if (NM.IsIconic(h) || (requireOnScreen && !OnScreen(h))) continue;
                return c;
            }
            return null;
        }

        // Where focus goes when a foreground change is not followed, or when the focused window is
        // parked: the window that held the foreground before, while it stands on screen on the active
        // desktop, else the active desktop's top window, else the Windows desktop. Input then never
        // stays on a window no monitor shows.
        private IntPtr ActiveDesktopFocusTarget(IntPtr previous, WindowModel except = null)
        {
            if (IsActiveDesktopWindow(previous) && (except == null || except.Hwnd != previous)) return previous;
            var top = TopOfActiveDesktop(except);
            return top != null ? top.Hwnd : NM.GetShellWindow();
        }

        // A window standing on screen on the active desktop: a tracked one, or an untracked one owned
        // by a tracked one, such as a dialog.
        private bool IsActiveDesktopWindow(IntPtr h)
        {
            if (h == IntPtr.Zero || NM.IsIconic(h) || !OnScreen(h)) return false;
            WindowModel m;
            if (_byHwnd.TryGetValue(h, out m)) return m.DesktopIndex == ActiveIndex;
            IntPtr owner = NM.GetWindow(h, NM.GW_OWNER);
            return owner != IntPtr.Zero && _byHwnd.TryGetValue(owner, out m) && m.DesktopIndex == ActiveIndex;
        }

        // Focus for a target ActiveDesktopFocusTarget returned. The Windows desktop is focused only,
        // never raised or restored.
        private static void FocusActiveDesktop(IntPtr target)
        {
            if (target == IntPtr.Zero) return;
            if (target == NM.GetShellWindow()) WindowActions.FocusDesktop(target);
            else WindowActions.ForceForeground(target);
        }
    }
}
