using System;
using System.Collections.Generic;
using MultiDesk.Models;
using NM = MultiDesk.Interop.NativeMethods;

namespace MultiDesk.Services
{
    /// <summary>
    /// The visibility engine. Every window not on the active desktop must never be seen on screen,
    /// under any circumstance: that invariant is never relaxed by anything below. How it is enforced
    /// depends on settings.AltTabAllDesktops.
    ///
    /// Off: a real hide (SW_HIDE), the pre-2.9.2 mechanism. Nothing can draw a hidden window, and the
    /// system Alt+Tab shows only the active desktop, like plain Windows.
    ///
    /// On: windows are parked instead of hidden. A parked window stays WS_VISIBLE, relocated far below
    /// its own monitor's column, which is what lets the real, unmodified Windows Alt+Tab list every
    /// desktop's windows with live thumbnails, nothing drawn by MultiDesk. Parking below the window's
    /// OWN column matters: the nearest monitor to the park spot is then the window's own, so
    /// per-monitor-DPI windows receive no DPI change while parked, which is what used to return
    /// portrait windows stretched to the wrong proportions when the park spot sat beside a
    /// different-DPI monitor.
    ///
    /// Some windows defeat a plain position move: a window can clamp the move to nothing inside its
    /// own message handling while the call reports success, and maximized or fullscreen browser
    /// windows, which re-derive their geometry from their monitor, can also revert an off-screen
    /// placement later. So every park is VERIFIED against the real on-screen rectangle, and each
    /// window earns its own mechanism on a three-step ladder, escalating only by defeating the level
    /// below. Level 0, a pure position move, size and state untouched. Level 1, one atomic placement
    /// change that restores the window to normal state DIRECTLY INTO the off-screen spot (it is never
    /// visible un-maximized on screen) and re-maximizes it onto its own monitor in one call on
    /// return; the window stays WS_VISIBLE and live in Alt+Tab throughout. Level 2, a minimize with
    /// that window's DWM animation suppressed: invisible like everything minimized, yet STILL LISTED
    /// in the real Alt+Tab, and restored natively on return, where Windows itself rebuilds maximized
    /// or fullscreen geometry from the monitor, exactly. No level removes a window from the switcher;
    /// escalations are logged with the window's title.
    ///
    /// A window found minimized is left alone, already off screen and correct in Alt+Tab. A window
    /// minimized while parked gets its remembered restore rectangle repaired in place, staying
    /// minimized, so un-minimizing later lands at its true position. Hung windows are skipped, not
    /// stalled on; the heal pass retries once they answer. A parked window's way back lives only in
    /// this process's memory, so a window found standing at the park spot with no capture to its name
    /// (a killed session) is given a centered position at its own size rather than left stranded.
    /// </summary>
    public sealed partial class DesktopManager
    {
        private bool ParkingEnabled { get { return _settings.Current.AltTabAllDesktops; } }

        // Everything at or below OffscreenY is "parked territory": far below every monitor, in the
        // window's own column. ParkY is where windows are sent; the threshold is looser than the
        // exact coordinate so workspace-coordinate offsets and app-side rounding cannot fool the
        // detection.
        private static int ParkY()
        {
            return NM.GetSystemMetrics(NM.SM_YVIRTUALSCREEN) + NM.GetSystemMetrics(NM.SM_CYVIRTUALSCREEN) + 30000;
        }

        private static int OffscreenY()
        {
            return NM.GetSystemMetrics(NM.SM_YVIRTUALSCREEN) + NM.GetSystemMetrics(NM.SM_CYVIRTUALSCREEN) + 20000;
        }

        private static bool AtPark(ref NM.RECT r) { return r.Top >= OffscreenY(); }

        private static bool AtParkNow(IntPtr hwnd)
        {
            NM.RECT r;
            if (!NM.GetWindowRect(hwnd, out r)) return false;
            return AtPark(ref r);
        }

        // True when an off-desktop window is properly out of the way right now: minimized, genuinely
        // hidden, or its real rectangle in parked territory. Reads what the screen shows, never a
        // bookkeeping value, so "believed parked but visibly on screen" cannot exist as a state.
        private static bool IsParked(IntPtr hwnd)
        {
            if (!NM.IsWindowVisible(hwnd)) return true;
            if (NM.IsIconic(hwnd)) return true;
            NM.RECT r;
            if (!NM.GetWindowRect(hwnd, out r)) return true; // unreadable; do not fight it blindly
            return AtPark(ref r);
        }

        // A visible, centered position at the window's own current size, for a window whose true
        // rectangle is lost information (parked by a session that was killed before restoring it).
        private static NM.RECT FallbackRect(NM.RECT current)
        {
            int sw = NM.GetSystemMetrics(NM.SM_CXSCREEN);
            int sh = NM.GetSystemMetrics(NM.SM_CYSCREEN);
            int w = current.Width > 0 ? current.Width : 800;
            int h = current.Height > 0 ? current.Height : 600;
            int x = Math.Max(0, (sw - w) / 2);
            int y = Math.Max(0, (sh - h) / 2);
            return new NM.RECT { Left = x, Top = y, Right = x + w, Bottom = y + h };
        }

        // True if this tracked window is in an ordinary, user-reachable state: visible, not cloaked,
        // and, under parking, not in parked territory. Minimized counts as ordinary, a state the user
        // reaches and leaves through normal Windows means.
        public bool OnScreen(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero || !NM.IsWindow(hwnd)) return false;
            if (NM.IsCloaked(hwnd)) return false;
            if (!NM.IsWindowVisible(hwnd)) return false;
            if (NM.IsIconic(hwnd)) return true;
            if (ParkingEnabled && AtParkNow(hwnd)) return false;
            return true;
        }

        private bool? _lastParkingEnabled;

        // A mode change is caught at the next ApplyVisibility regardless of which call site changed
        // the setting. Only turning parking off needs repair, because a real hide never touches
        // position, so a still-parked window would otherwise be hidden at the park spot forever.
        private void MigrateModeIfChanged()
        {
            bool park = ParkingEnabled;
            if (_lastParkingEnabled.HasValue && _lastParkingEnabled.Value && !park)
            {
                foreach (var w in _byHwnd.Values)
                {
                    if (w.ParkedWith >= 0) Unpark(w);
                }
            }
            _lastParkingEnabled = park;
        }

        private void ApplyVisibility(bool capturePreviews = true)
        {
            try
            {
                MigrateModeIfChanged();
                bool persist = _settings.Current.PersistPreviews && capturePreviews;
                var d = DesktopAt(ActiveIndex);
                bool park = ParkingEnabled;

                var toHide = new List<WindowModel>();
                foreach (var kv in _byHwnd)
                {
                    var w = kv.Value;
                    if (w.DesktopIndex == ActiveIndex) continue;
                    if (w.Hwnd == IntPtr.Zero || !NM.IsWindow(w.Hwnd)) continue;
                    bool alreadyAway = park ? IsParked(w.Hwnd) : !NM.IsWindowVisible(w.Hwnd);
                    if (alreadyAway) continue;
                    // Snapshot for the hover preview while still on screen; under parking only a
                    // fallback, since a parked window stays composited and previews live.
                    if (persist) PreviewCache.Capture(w.Hwnd);
                    toHide.Add(w);
                }

                var toShow = new List<WindowModel>();
                if (d != null)
                    foreach (var w in d.Windows)
                    {
                        if (w.Hwnd == IntPtr.Zero || !NM.IsWindow(w.Hwnd)) continue;
                        // A restore is owed (ParkedWith set), or the window stands in parked
                        // territory anyway (orphan of a killed session), or it is genuinely hidden
                        // (level 2, or left over from hide mode).
                        bool needsShow = park
                            ? (w.ParkedWith >= 0 || !NM.IsWindowVisible(w.Hwnd) || AtParkNow(w.Hwnd))
                            : !NM.IsWindowVisible(w.Hwnd);
                        if (needsShow) toShow.Add(w);
                    }

                if (toHide.Count == 0 && toShow.Count == 0) return;

                if (park)
                {
                    foreach (var w in toHide) Park(w);
                    foreach (var w in toShow) EnsureOnScreen(w);
                    return;
                }

                IntPtr batch = NM.BeginDeferWindowPos(toHide.Count + toShow.Count);
                foreach (var w in toHide)
                {
                    if (batch != IntPtr.Zero && !NM.IsHungAppWindow(w.Hwnd))
                    {
                        batch = NM.DeferWindowPos(batch, w.Hwnd, IntPtr.Zero, 0, 0, 0, 0,
                            NM.SWP_HIDEWINDOW | NM.SWP_NOSIZE | NM.SWP_NOMOVE | NM.SWP_NOACTIVATE | NM.SWP_NOZORDER);
                        if (batch != IntPtr.Zero) continue;
                    }
                    NM.ShowWindowAsync(w.Hwnd, NM.SW_HIDE);
                }
                foreach (var w in toShow)
                {
                    if (batch != IntPtr.Zero && !NM.IsHungAppWindow(w.Hwnd))
                    {
                        batch = NM.DeferWindowPos(batch, w.Hwnd, IntPtr.Zero, 0, 0, 0, 0,
                            NM.SWP_SHOWWINDOW | NM.SWP_NOSIZE | NM.SWP_NOMOVE | NM.SWP_NOACTIVATE | NM.SWP_NOZORDER);
                        if (batch != IntPtr.Zero) continue;
                    }
                    NM.ShowWindowAsync(w.Hwnd, NM.SW_SHOWNA);
                }
                if (batch != IntPtr.Zero) NM.EndDeferWindowPos(batch);
                // A show never moves a window; anything revealed standing in parked territory (a
                // parked session killed, then the setting turned off) is repaired, not left there.
                foreach (var w in toShow) EnsureOnScreen(w);
            }
            catch (Exception ex) { Log.Error("apply visibility", ex); }
        }

        // Park with whichever mechanism this window has earned, verifying each attempt against the
        // real rectangle and escalating exactly one level on defeat, so the first switch that meets a
        // clamping window already lands it off screen by the next level in the same call.
        private void Park(WindowModel w)
        {
            if (w == null || w.Hwnd == IntPtr.Zero || !NM.IsWindow(w.Hwnd)) return;
            if (NM.IsIconic(w.Hwnd)) return;
            if (NM.IsHungAppWindow(w.Hwnd)) return;
            NM.RECT r;
            if (!NM.GetWindowRect(w.Hwnd, out r)) return;

            if (AtPark(ref r))
            {
                // Already in parked territory. With a capture on record, all is well. Without one, an
                // earlier process parked it and was killed; a centered position at its own size is
                // captured so it is merely repositioned, never stranded.
                if (w.ParkedWith < 0) { w.SavedRect = FallbackRect(r); w.ParkedWith = 0; }
                return;
            }

            bool hadFg = NM.GetForegroundWindow() == w.Hwnd;
            int parkY = ParkY();

            if (w.ParkEscalation == 0)
            {
                if (NM.SetWindowPos(w.Hwnd, IntPtr.Zero, r.Left, parkY, 0, 0,
                    NM.SWP_NOSIZE | NM.SWP_NOZORDER | NM.SWP_NOOWNERZORDER | NM.SWP_NOACTIVATE))
                {
                    NM.RECT after;
                    if (NM.GetWindowRect(w.Hwnd, out after) && AtPark(ref after))
                    {
                        w.SavedRect = r;
                        w.ParkedWith = 0;
                        if (hadFg) NoteParkedForeground(w.Hwnd);
                        return;
                    }
                    // The call reported success and the window never moved: it clamps position moves
                    // (maximized browsers re-derive position from their monitor). Escalate.
                    w.ParkEscalation = 1;
                }
                else
                {
                    // Windows refused outright: an elevated window no non-elevated process can manage,
                    // under this or any other mechanism. Logged once; nothing more can be done.
                    if (_parkRefused.Add(w.Hwnd))
                        Log.Info("Park refused for \"" + (w.Title ?? "") + "\"; likely an elevated window this process cannot manage.");
                    return;
                }
            }

            if (w.ParkEscalation == 1)
            {
                var place = NM.NewPlacement();
                if (NM.GetWindowPlacement(w.Hwnd, ref place))
                {
                    var parked = place;
                    parked.showCmd = NM.SW_SHOWNOACTIVATE;
                    int width = place.rcNormalPosition.Width > 0 ? place.rcNormalPosition.Width : 800;
                    int height = place.rcNormalPosition.Height > 0 ? place.rcNormalPosition.Height : 600;
                    parked.rcNormalPosition = new NM.RECT { Left = r.Left, Top = parkY, Right = r.Left + width, Bottom = parkY + height };
                    NM.SetWindowPlacement(w.Hwnd, ref parked);

                    NM.RECT after;
                    if (NM.GetWindowRect(w.Hwnd, out after) && AtPark(ref after))
                    {
                        w.SavedPlacement = place;
                        w.ParkedWith = 1;
                        if (hadFg) NoteParkedForeground(w.Hwnd);
                        return;
                    }
                }
                // Defeated the placement route too (fullscreen windows re-derive from the monitor
                // the same way maximized ones do). Minimizing is what remains, and it keeps the
                // window listed in Alt+Tab.
                w.ParkEscalation = 2;
                if (_parkRefused.Add(w.Hwnd))
                    Log.Info("Window \"" + (w.Title ?? "") + "\" defeats every off-screen placement; it is minimized while off its desktop, still listed in Alt+Tab.");
            }

            // Level 2: minimize without activation games, the window's own minimize animation
            // suppressed so nothing visibly shrinks across the screen. The placement captured first
            // is what tells the return path whether to come back maximized or normal; Windows itself
            // then rebuilds that geometry from the monitor, exactly.
            var cur = NM.NewPlacement();
            if (!NM.GetWindowPlacement(w.Hwnd, ref cur)) return;
            w.SavedPlacement = cur;
            w.ParkedWith = 2;
            NM.SetTransitionsDisabled(w.Hwnd, true);
            NM.ShowWindowAsync(w.Hwnd, NM.SW_SHOWMINNOACTIVE);
            if (hadFg) NoteParkedForeground(w.Hwnd);
        }

        // Bring a parked window back exactly as it was, by whichever mechanism holds it. Level 0
        // restores the full captured rectangle, size included, so nothing a parked window did to
        // itself while away survives the return. Level 1 reapplies the captured placement in one
        // call: a maximized window re-maximizes onto its own monitor, whose geometry defines it, so
        // it cannot come back at a wrong size or corner (maximizing is the one verb Win32 offers no
        // non-activating form of; the desktop switch sets the final foreground itself right after).
        // Level 2 simply shows, hide having touched neither position nor state.
        private void Unpark(WindowModel w)
        {
            if (w == null || w.Hwnd == IntPtr.Zero || !NM.IsWindow(w.Hwnd)) return;
            if (w.ParkedWith < 0) return;
            if (NM.IsHungAppWindow(w.Hwnd)) return; // the heal pass retries once it answers again
            if (w.ParkedWith == 2)
            {
                // The minimize WAS the park, so the return un-does it, back to the exact state the
                // captured placement records: maximized re-maximizes (the one verb with no
                // non-activating form; the desktop switch sets the final foreground right after),
                // anything else un-minimizes without activating. Then the window's animations are
                // handed back.
                var place = w.SavedPlacement;
                place.showCmd = (w.SavedPlacement.showCmd == NM.SW_SHOWMAXIMIZED)
                    ? NM.SW_SHOWMAXIMIZED : NM.SW_SHOWNOACTIVATE;
                NM.SetWindowPlacement(w.Hwnd, ref place);
                NM.SetTransitionsDisabled(w.Hwnd, false);
                w.ParkedWith = -1;
                return;
            }
            if (NM.IsIconic(w.Hwnd))
            {
                RepairMinimizedRestoreRect(w);
                w.ParkedWith = -1;
                return;
            }
            if (w.ParkedWith == 0)
            {
                NM.SetWindowPos(w.Hwnd, IntPtr.Zero, w.SavedRect.Left, w.SavedRect.Top,
                    w.SavedRect.Width, w.SavedRect.Height,
                    NM.SWP_NOZORDER | NM.SWP_NOOWNERZORDER | NM.SWP_NOACTIVATE);
            }
            else
            {
                var place = w.SavedPlacement;
                place.showCmd = (w.SavedPlacement.showCmd == NM.SW_SHOWMAXIMIZED)
                    ? NM.SW_SHOWMAXIMIZED : NM.SW_SHOWNOACTIVATE;
                NM.SetWindowPlacement(w.Hwnd, ref place);
            }
            w.ParkedWith = -1;
        }

        // A window minimized while parked had the park spot recorded by Windows as its restore
        // rectangle; that bookkeeping entry is rewritten to the true captured rectangle while the
        // window stays minimized, so a later un-minimize lands at its real position. Changes nothing
        // visible and activates nothing.
        private void RepairMinimizedRestoreRect(WindowModel w)
        {
            var place = NM.NewPlacement();
            if (!NM.GetWindowPlacement(w.Hwnd, ref place)) return;
            if (place.rcNormalPosition.Top < OffscreenY()) return;
            place.rcNormalPosition = (w.ParkedWith == 1) ? w.SavedPlacement.rcNormalPosition : w.SavedRect;
            if (w.ParkedWith == 1 && w.SavedPlacement.showCmd == NM.SW_SHOWMAXIMIZED)
                place.flags |= NM.WPF_RESTORETOMAXIMIZED;
            NM.SetWindowPlacement(w.Hwnd, ref place);
        }

        // Guarantee a window believed to belong on the active desktop is genuinely on screen, in
        // either mode: shown if hidden (parking never hides below level 2, and hide mode's own
        // tracker rules are respected by only force-showing under parking), repaired if minimized
        // with a park-spot restore rectangle, restored if in parked territory, orphan-rescued if
        // there with no capture, and cleared of stale bookkeeping if found at a real position.
        private void EnsureOnScreen(WindowModel w)
        {
            if (w == null || w.Hwnd == IntPtr.Zero || !NM.IsWindow(w.Hwnd)) return;
            if (ParkingEnabled && !NM.IsWindowVisible(w.Hwnd)) NM.ShowWindowAsync(w.Hwnd, NM.SW_SHOWNA);
            if (NM.IsIconic(w.Hwnd))
            {
                // Minimized BY the parking (level 2) un-minimizes on return, since the minimize was
                // the park itself; minimized by the user while parked at a lower level stays
                // minimized, with only its restore rectangle repaired, since un-minimizing was never
                // asked for by anyone.
                if (w.ParkedWith == 2) Unpark(w);
                else if (w.ParkedWith >= 0) { RepairMinimizedRestoreRect(w); w.ParkedWith = -1; }
                return;
            }
            NM.RECT r;
            if (!NM.GetWindowRect(w.Hwnd, out r)) return;
            if (!AtPark(ref r))
            {
                w.ParkedWith = -1;
                return;
            }
            if (w.ParkedWith < 0) { w.SavedRect = FallbackRect(r); w.ParkedWith = 0; }
            Unpark(w);
        }

        // Off-desktop placement for a single window, dispatching to whichever mechanism is active.
        private void ShowWin(WindowModel w)
        {
            if (w == null || w.Hwnd == IntPtr.Zero || !NM.IsWindow(w.Hwnd)) return;
            if (ParkingEnabled) { EnsureOnScreen(w); return; }
            NM.ShowWindowAsync(w.Hwnd, NM.SW_SHOWNA);
            EnsureOnScreen(w); // rescue a park-territory orphan even in hide mode; a no-op otherwise
        }

        private void HideWin(WindowModel w)
        {
            if (w == null || w.Hwnd == IntPtr.Zero || !NM.IsWindow(w.Hwnd)) return;
            if (ParkingEnabled)
            {
                bool hadFocus = NM.GetForegroundWindow() == w.Hwnd;
                Park(w);
                // A parked window stays WS_VISIBLE, so Windows never takes the foreground from it on
                // its own; left alone, keystrokes would land on a window no monitor shows. Hand focus
                // back to the active desktop, suppressed so the hand-off never reads as a choice.
                if (hadFocus)
                {
                    SuppressFocusFollow();
                    var top = TopOfActiveDesktop(w);
                    if (top != null) WindowActions.ForceForeground(top.Hwnd);
                }
            }
            else NM.ShowWindowAsync(w.Hwnd, NM.SW_HIDE);
        }

        /// <summary>Repair pass, run by the tracker's heartbeat and after every event: re-park any
        /// off-desktop window found showing, escalating its mechanism when a believed-successful park
        /// was reverted behind our back, and bring back any active-desktop window found in parked
        /// territory. No window may stay wrongly visible or wrongly parked, ever.</summary>
        public void HealVisibility()
        {
            IntPtr fg = NM.GetForegroundWindow();
            // The focused popup's owner is spared while the popup holds focus: parking the anchor
            // closes an owned popup on the spot, before the user can touch it.
            IntPtr fgOwner = fg != IntPtr.Zero ? NM.GetWindow(fg, NM.GW_OWNER) : IntPtr.Zero;
            bool park = ParkingEnabled;
            foreach (var kv in _byHwnd)
            {
                var w = kv.Value;
                if (w.Hwnd == IntPtr.Zero || !NM.IsWindow(w.Hwnd)) continue;

                if (w.DesktopIndex == ActiveIndex) { EnsureOnScreen(w); continue; }

                bool away = park ? IsParked(w.Hwnd) : !NM.IsWindowVisible(w.Hwnd);
                if (away) continue;
                // The focused window is exempt only while a foreground event is recent enough that
                // SetForeground's correction could still be in flight; corrections start from events
                // and nothing else, and a window already holding focus fires no new ones, so past the
                // grace window the invariant wins.
                if (w.Hwnd == fg && Environment.TickCount - _fgEventAt < 600) continue;
                if (fgOwner != IntPtr.Zero && w.Hwnd == fgOwner) continue;
                // On screen although a park was believed to have held: the app reverted it behind our
                // back, so the mechanism that "held" has in truth been defeated. Escalate one level.
                if (park && w.ParkedWith >= 0 && w.ParkEscalation == w.ParkedWith && w.ParkEscalation < 2)
                    w.ParkEscalation = w.ParkedWith + 1;
                HideWin(w);
            }
        }

        // Windows whose park was refused or defeated, so each is logged once, not per heal pass.
        private readonly HashSet<IntPtr> _parkRefused = new HashSet<IntPtr>();
    }
}
