using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Windows.Threading;
using MultiDesk.Models;
using NM = MultiDesk.Interop.NativeMethods;

namespace MultiDesk.Services
{
    /// <summary>
    /// The managed desktop engine. MultiDesk owns desktops 1..N and assigns every tracked window to
    /// one. Switching shows the active desktop's windows and hides the rest with documented show/hide
    /// calls, so nothing depends on undocumented COM. Off-desktop windows can have their working set
    /// trimmed after a delay to return physical memory under heavy load.
    /// </summary>
    public sealed class DesktopManager
    {
        public ObservableCollection<DesktopModel> Desktops { get; } = new ObservableCollection<DesktopModel>();
        public int ActiveIndex { get; private set; }
        public event Action ActiveChanged;

        // Available height for the desktop list, so sections can auto-fill it. The main window updates
        // it on resize and raises LayoutChanged; structural changes raise it too.
        public double ContentHeight { get; private set; }
        public event Action LayoutChanged;
        public void NotifyLayout(double height) { ContentHeight = height; RaiseLayout(); }
        public void RaiseLayout() { var h = LayoutChanged; if (h != null) h(); }

        private readonly Dictionary<IntPtr, WindowModel> _byHwnd = new Dictionary<IntPtr, WindowModel>();
        private readonly SettingsStore _settings;
        private DispatcherTimer _trimTimer;
        private DispatcherTimer _altTabCommit;
        private DispatcherTimer _placementExpiry;
        private bool _altTabEnding; // true between Alt release and the commit that switches to the selection
        private IntPtr _altTabStartFg; // foreground window when the gesture began, to tell a cancel from a choice
        // A window that grabbed foreground while the switcher was still up. The user cannot select
        // anything before releasing Alt, so such a grab is an app asserting itself (a media window
        // being re-shown is the classic case) and must never be mistaken for the user's choice.
        // The timestamp separates those early grabs from the user's own selection event, which can
        // race a fast Alt release by a few milliseconds and land just before the session ends.
        private IntPtr _altTabSuspect;
        private int _altTabSuspectAt;
        // One-shot guard: right after focus is placed deliberately, an unrequested grab by a different
        // window is reverted once. Media windows re-assert right after a switch touches them.
        private IntPtr _fgGuardHwnd;
        private int _fgGuardUntil;
        private bool _fgGuardUsed;
        private List<PlacementEntry> _pendingPlacements;
        private int _switchGuardUntil; // ignore focus-follow briefly after a switch, to avoid bouncing back

        /// <summary>True briefly after a desktop switch, while async show/hide is still settling.</summary>
        public bool InSwitchSettle { get { return Environment.TickCount < _switchGuardUntil; } }

        public DesktopManager(SettingsStore settings) { _settings = settings; }

        public void Initialize()
        {
            Desktops.Clear();
            int count = _settings.Current.DesktopCount;
            var names = _settings.Current.DesktopNames;
            var rows = _settings.Current.DesktopRows;
            for (int i = 0; i < count; i++)
                Desktops.Add(new DesktopModel { Index = i, Name = NameFor(names, i), Rows = RowsFor(rows, i), IsActive = (i == 0) });
            ActiveIndex = 0;

            _pendingPlacements = (_settings.Current.Placements != null)
                ? new List<PlacementEntry>(_settings.Current.Placements)
                : new List<PlacementEntry>();

            // Remembered placements restore the previous session at launch only. After a short boot grace
            // period, drop them so a window opened by hand later goes to the current desktop, not its old
            // one. App pins are separate and keep applying for the whole session.
            _placementExpiry = new DispatcherTimer { Interval = TimeSpan.FromSeconds(15) };
            _placementExpiry.Tick += (s, e) =>
            {
                _placementExpiry.Stop();
                if (_pendingPlacements != null) _pendingPlacements.Clear();
            };
            _placementExpiry.Start();

            _trimTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(_settings.Current.TrimDelayMs) };
            _trimTimer.Tick += OnTrimTick;
        }

        private static string NameFor(List<string> names, int i)
        {
            if (names != null && i < names.Count && !string.IsNullOrWhiteSpace(names[i])) return names[i];
            return "Desktop " + (i + 1);
        }

        private static int RowsFor(List<int> rows, int i)
        {
            return (rows != null && i < rows.Count && rows[i] > 0) ? rows[i] : 0;
        }

        public DesktopModel DesktopAt(int index)
        {
            return (index >= 0 && index < Desktops.Count) ? Desktops[index] : null;
        }

        // ---- window lifecycle (driven by WindowTracker) --------------------
        public WindowModel EnsureWindow(IntPtr hwnd, string title, uint pid, string exe)
        {
            WindowModel m;
            if (_byHwnd.TryGetValue(hwnd, out m))
            {
                if (!string.IsNullOrEmpty(title) && title != m.Title) m.Title = title;
                return m;
            }
            if (string.IsNullOrWhiteSpace(title)) title = FallbackTitle(exe); // some windows report no text
            string profile = ProcessInfo.ProfileArg(pid, exe);
            int target = PinIndexFor(exe, profile);
            int placeOrder = -1;
            if (target < 0)
            {
                target = TakePlacement(exe, profile, title, out placeOrder); // remembered desktop, if any
                if (target < 0) target = ActiveIndex;
            }
            m = new WindowModel { Hwnd = hwnd, Title = title, Pid = pid, ExePath = exe, ProfileArg = profile, DesktopIndex = target };
            try { m.Icon = IconCache.ForWindow(hwnd, exe); } catch (Exception ex) { Log.Error("icon resolve", ex); }
            _byHwnd[hwnd] = m;
            var d = DesktopAt(target);
            if (d != null)
            {
                if (placeOrder >= 0 && placeOrder <= d.Windows.Count) d.Windows.Insert(placeOrder, m);
                else d.Windows.Add(m);
            }
            if (target != ActiveIndex) HideOrPark(m); // pinned or remembered on another desktop
            return m;
        }

        public void RemoveWindow(IntPtr hwnd)
        {
            WindowModel m;
            if (!_byHwnd.TryGetValue(hwnd, out m)) return;
            _byHwnd.Remove(hwnd);
            PreviewCache.Remove(hwnd);
            var d = DesktopAt(m.DesktopIndex);
            if (d != null) d.Windows.Remove(m);
        }

        public void UpdateTitle(IntPtr hwnd, string title)
        {
            WindowModel m;
            if (_byHwnd.TryGetValue(hwnd, out m) && !string.IsNullOrEmpty(title)) m.Title = title;
        }

        public bool IsTracked(IntPtr hwnd) { return _byHwnd.ContainsKey(hwnd); }

        public IEnumerable<WindowModel> AllWindows { get { return _byHwnd.Values.ToList(); } }

        /// <summary>Update the active-window highlight and, optionally, follow focus across desktops.</summary>
        public void SetForeground(IntPtr hwnd)
        {
            WindowModel active = null;
            foreach (var kv in _byHwnd)
            {
                var w = kv.Value;
                bool on = (w.Hwnd == hwnd);
                if (w.IsActive != on) w.IsActive = on;
                if (on) active = w;
            }
            // A tracked window taking foreground while the switcher is still up cannot be the user's
            // choice, because nothing is selected until Alt is released. It is an app asserting itself
            // (media windows do this when a reveal shows them), so remember it and never commit to it.
            if (AltTabActive && !_altTabEnding && active != null)
            {
                // The session-start window regaining foreground is the tail of the previous commit,
                // not a grab, so it stays trusted.
                if (hwnd != _altTabStartFg)
                {
                    _altTabSuspect = hwnd;
                    _altTabSuspectAt = Environment.TickCount;
                }
                return;
            }

            // Completing an Alt+Tab: the OS just activated the window the user chose, so switch to its
            // desktop now. Acting on this event is reliable, unlike reading the foreground on a fixed timer,
            // which can fire before the selection has settled and snap back to the previous window.
            if (_altTabEnding && active != null)
            {
                // The suspect re-asserting after the release is still not a selection; hold out for
                // the real choice's event, with the timer as the backstop.
                if (hwnd == _altTabSuspect) return;
                // Dismissing the switcher makes foreground hop back to the starting window for a
                // moment before it lands on the selection. Committing on that hop locked the switch
                // onto the window the user was leaving, and the guard then fought off the real
                // selection when its event arrived. A genuine cancel also returns here, which the
                // backstop timer commits on its own, so ignoring the start window costs nothing.
                if (hwnd == _altTabStartFg) return;
                _altTabEnding = false;
                AltTabActive = false;
                if (_altTabCommit != null) _altTabCommit.Stop();
                GuardForeground(hwnd);
                SwitchTo(active.DesktopIndex, false, false);
                return;
            }

            // One-shot revert: an unrequested grab by a different tracked window right after focus was
            // placed deliberately gets pushed back once, which stops a media window from planting
            // itself in front the moment a switch shows or hides it. Single-shot, so a genuine fast
            // user action can never end up in a tug of war.
            if (active != null && _fgGuardHwnd != IntPtr.Zero && hwnd != _fgGuardHwnd && !_fgGuardUsed
                && Environment.TickCount < _fgGuardUntil && NM.IsWindow(_fgGuardHwnd))
            {
                _fgGuardUsed = true;
                WindowActions.ForceForeground(_fgGuardHwnd);
                return;
            }

            // Follow focus onto another desktop. Visible windows respect the settle window, because
            // during a switch the OS reassigns foreground to whichever old-desktop window has not
            // hidden yet, and following that would bounce the switch back. A hidden window can never
            // be that reassignment target; foreground landing on a hidden window is always a
            // deliberate external activation (a relaunched single-instance app, or an Alt+Tab choice
            // whose activation arrived after the commit), so it is followed even while settling.
            if (active != null && active.DesktopIndex != ActiveIndex && _settings.Current.AutoSwitchOnForeground
                && !AltTabActive)
            {
                bool wasHidden = !NM.IsWindowVisible(hwnd);
                if (!wasHidden && Environment.TickCount < _switchGuardUntil) return;
                SwitchTo(active.DesktopIndex, false);
                // The switch shows it without activating; raise it as well so the window the user
                // asked for is on top, not buried under the desktop's existing z-order.
                if (wasHidden) WindowActions.ForceForeground(hwnd);
            }
        }

        // ---- desktop operations --------------------------------------------
        public void SwitchTo(int index, bool activateTop = true, bool capturePreviews = true)
        {
            if (index < 0 || index >= Desktops.Count) return;
            _switchGuardUntil = Environment.TickCount + 600; // settle window for focus-follow
            ActiveIndex = index;
            for (int i = 0; i < Desktops.Count; i++) Desktops[i].IsActive = (i == index);
            ApplyVisibility(capturePreviews);
            if (activateTop)
            {
                var d = DesktopAt(index);
                var top = (d != null && d.Windows.Count > 0) ? d.Windows[d.Windows.Count - 1] : null;
                if (top != null)
                {
                    GuardForeground(top.Hwnd);
                    WindowActions.ForceForeground(top.Hwnd);
                }
            }
            ScheduleTrim();
            var h = ActiveChanged; if (h != null) h();
        }

        // Arm the one-shot foreground guard around a deliberate focus placement.
        private void GuardForeground(IntPtr hwnd)
        {
            _fgGuardHwnd = hwnd;
            _fgGuardUntil = Environment.TickCount + 600;
            _fgGuardUsed = false;
        }

        public void AddDesktop()
        {
            int i = Desktops.Count;
            Desktops.Add(new DesktopModel { Index = i, Name = "Desktop " + (i + 1) });
            PersistDesktops();
            RaiseLayout();
        }

        public void RemoveDesktop(int index)
        {
            if (Desktops.Count <= 1) return;
            var d = DesktopAt(index);
            if (d == null) return;
            var survivor = DesktopAt(index > 0 ? index - 1 : index + 1);
            if (survivor == null) return;

            var activeModel = DesktopAt(ActiveIndex);

            // Move the removed desktop's windows onto the survivor rather than stranding them.
            foreach (var w in d.Windows.ToList())
            {
                d.Windows.Remove(w);
                survivor.Windows.Add(w);
            }
            Desktops.Remove(d);
            Reindex();

            var newActive = (activeModel != null && Desktops.Contains(activeModel)) ? activeModel : survivor;
            ActiveIndex = newActive.Index;
            for (int i = 0; i < Desktops.Count; i++) Desktops[i].IsActive = (i == ActiveIndex);
            ApplyVisibility();
            PersistDesktops();
            var h = ActiveChanged; if (h != null) h();
            RaiseLayout();
        }

        /// <summary>Move a desktop to a new position, carrying its windows. Used for section reorder.</summary>
        public void ReorderDesktop(int fromIndex, int toIndex)
        {
            if (fromIndex < 0 || fromIndex >= Desktops.Count) return;
            if (toIndex < 0) toIndex = 0;
            if (toIndex >= Desktops.Count) toIndex = Desktops.Count - 1;
            if (fromIndex == toIndex) return;
            var activeModel = DesktopAt(ActiveIndex);
            Desktops.Move(fromIndex, toIndex);
            Reindex();
            ActiveIndex = (activeModel != null) ? activeModel.Index : ActiveIndex;
            for (int i = 0; i < Desktops.Count; i++) Desktops[i].IsActive = (i == ActiveIndex);
            PersistDesktops();
            RaiseLayout();
        }

        public void RenameDesktop(int index, string name)
        {
            var d = DesktopAt(index);
            if (d == null || string.IsNullOrWhiteSpace(name)) return;
            d.Name = name.Trim();
            PersistDesktops();
        }

        public void MoveWindowToDesktop(IntPtr hwnd, int target)
        {
            WindowModel m;
            if (!_byHwnd.TryGetValue(hwnd, out m)) return;
            if (target < 0 || target >= Desktops.Count) return;
            var from = DesktopAt(m.DesktopIndex);
            var to = DesktopAt(target);
            if (from != null) from.Windows.Remove(m);
            m.DesktopIndex = target;
            if (to != null) to.Windows.Add(m);
            if (target == ActiveIndex) ShowWin(m); else HideOrPark(m);
        }

        /// <summary>Reorder a window into the display slot of another, moving desktops if they differ.
        /// Used for drag-to-reorder of the icons within or across desktops. The target's index is
        /// captured BEFORE the dragged icon is removed: within one desktop, computing it after the
        /// removal shifted every later icon back a slot, so a forward drag landed one spot early and
        /// a one-step forward drag put the icon straight back where it started. Capturing first means
        /// the icon lands exactly where it was dropped in both directions.</summary>
        public void MoveWindowToSpotOf(IntPtr draggedHwnd, IntPtr targetHwnd)
        {
            WindowModel dragged, target;
            if (!_byHwnd.TryGetValue(draggedHwnd, out dragged)) return;
            if (!_byHwnd.TryGetValue(targetHwnd, out target)) return;
            if (dragged == target) return;
            int targetDesktop = target.DesktopIndex;
            var from = DesktopAt(dragged.DesktopIndex);
            var to = DesktopAt(targetDesktop);
            int idx = (to != null) ? to.Windows.IndexOf(target) : -1;
            if (from != null) from.Windows.Remove(dragged);
            dragged.DesktopIndex = targetDesktop;
            if (to != null)
            {
                if (idx < 0 || idx > to.Windows.Count) idx = to.Windows.Count;
                to.Windows.Insert(idx, dragged);
            }
            if (targetDesktop == ActiveIndex) ShowWin(dragged); else HideOrPark(dragged);
        }

        /// <summary>Safety: reveal every managed window so none is left hidden on a hidden desktop.</summary>
        public void ShowAllWindows()
        {
            foreach (var w in _byHwnd.Values) ShowWin(w);
        }

        public void RefreshAll() { ApplyVisibility(); }

        // ---- Alt+Tab across desktops (optional) ----------------------------
        /// <summary>True while the user holds an Alt+Tab that is revealing all desktops' windows.</summary>
        public bool AltTabActive { get; private set; }

        /// <summary>Reveal every window so the system Alt+Tab can list windows from all desktops.</summary>
        public void BeginAltTab()
        {
            // A new gesture while the previous commit is still pending resolves that commit right now,
            // so rapid re-taps always toggle from a settled state instead of racing the backstop timer,
            // which is what made quick tab-tab sequences land on whatever the scroll passed through.
            if (_altTabEnding)
            {
                _altTabEnding = false;
                if (_altTabCommit != null) _altTabCommit.Stop();
                IntPtr pfg = NM.GetForegroundWindow();
                WindowModel pm;
                if (pfg != IntPtr.Zero && _byHwnd.TryGetValue(pfg, out pm) && pm.DesktopIndex != ActiveIndex)
                    SwitchTo(pm.DesktopIndex, false, false);
            }
            AltTabActive = true;
            _altTabStartFg = NM.GetForegroundWindow();
            _altTabSuspect = IntPtr.Zero;
            _fgGuardHwnd = IntPtr.Zero; // a new gesture supersedes any pending guard
            if (_altTabCommit != null) _altTabCommit.Stop();
            // No reveal happens here anymore. In all-desktops mode windows are never hidden: every
            // off-desktop window stays permanently visible parked under the wallpaper, so the system
            // Alt+Tab list is always warm and the switcher opens complete, with nothing shown per
            // gesture. This call only parks stragglers and is a no-op in the steady state.
            ApplyVisibility(false);
        }

        /// <summary>Alt released: switch to the desktop of whichever window was chosen, hiding the rest.</summary>
        public void EndAltTab()
        {
            // The switch normally happens the instant the OS fires the foreground event for the chosen
            // window (see SetForeground). This timer is only a fallback for when that event never arrives,
            // for example the selection is an untracked window or Alt+Tab was cancelled: it then commits to
            // whatever is in front. Keeping AltTabActive true until the commit also stops the tracker
            // pruning the revealed windows.
            _altTabEnding = true;
            // Commit immediately only when the foreground has genuinely moved off the window the
            // gesture started on (a mouse-click selection, or the activation outran the Alt release).
            // Committing while the starting window was still in front is what made a quick flick land
            // back where it began: Windows had not activated the real choice yet, so the choice then
            // activated invisibly on a desktop that had just been re-hidden.
            IntPtr now = NM.GetForegroundWindow();
            WindowModel chosen;
            // A suspect marked more than a beat before the release is an app's own grab; one marked
            // within the last 120ms is the user's selection event that outran the release.
            bool poisoned = now == _altTabSuspect && (Environment.TickCount - _altTabSuspectAt) > 120;
            if (now != IntPtr.Zero && now != _altTabStartFg && !poisoned && _byHwnd.TryGetValue(now, out chosen))
            {
                _altTabEnding = false;
                AltTabActive = false;
                GuardForeground(now);
                SwitchTo(chosen.DesktopIndex, false, false);
                return;
            }
            // Otherwise wait for the foreground event of the real selection (SetForeground commits the
            // instant it arrives). The timer is only the backstop for a cancelled gesture, where no
            // foreground change is coming, so its delay must outlast a slow activation to avoid
            // committing to the wrong window first.
            if (_altTabCommit == null)
            {
                _altTabCommit = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
                _altTabCommit.Tick += (s, e) =>
                {
                    _altTabCommit.Stop();
                    if (!_altTabEnding) return; // already committed from the foreground event
                    _altTabEnding = false;
                    AltTabActive = false;
                    // The suspect is accepted here: with no other foreground change arriving by the
                    // deadline, the user may genuinely have selected that window.
                    IntPtr fg = NM.GetForegroundWindow();
                    WindowModel m;
                    if (fg != IntPtr.Zero && _byHwnd.TryGetValue(fg, out m)) { GuardForeground(fg); SwitchTo(m.DesktopIndex, false, false); }
                    else ApplyVisibility(false);
                };
            }
            _altTabCommit.Stop();
            _altTabCommit.Start();
        }

        // ---- app pinning (per profile, persists across reboots) ------------
        public bool IsPinnedTo(WindowModel w, int index)
        {
            if (w == null) return false;
            var n = ExeName(w.ExePath);
            if (n == null || _settings.Current.AppPins == null) return false;
            foreach (var p in _settings.Current.AppPins)
                if (p.Exe == n && SameProfile(p.Profile, w.ProfileArg)) return p.DesktopIndex == index;
            return false;
        }

        public void PinApp(WindowModel w, int index)
        {
            if (w == null) return;
            var n = ExeName(w.ExePath);
            if (n == null) return;
            var s = _settings.Current;
            if (s.AppPins == null) s.AppPins = new List<AppPin>();
            var existing = s.AppPins.Find(p => p.Exe == n && SameProfile(p.Profile, w.ProfileArg));
            if (existing != null) existing.DesktopIndex = index;
            else s.AppPins.Add(new AppPin { Exe = n, Profile = w.ProfileArg, DesktopIndex = index });
            _settings.Save();
        }

        public void UnpinApp(WindowModel w)
        {
            if (w == null) return;
            var n = ExeName(w.ExePath);
            if (n == null || _settings.Current.AppPins == null) return;
            _settings.Current.AppPins.RemoveAll(p => p.Exe == n && SameProfile(p.Profile, w.ProfileArg));
            _settings.Save();
        }

        public string AppDisplayName(WindowModel w)
        {
            var n = ExeName(w != null ? w.ExePath : null);
            if (string.IsNullOrEmpty(n)) return "this app";
            int dot = n.LastIndexOf('.');
            string baseName = dot > 0 ? n.Substring(0, dot) : n;
            if (w != null && !string.IsNullOrEmpty(w.ProfileArg) && w.ProfileArg != "Default")
                baseName += " (" + w.ProfileArg + ")";
            return baseName;
        }

        private int PinIndexFor(string exePath, string profile)
        {
            var n = ExeName(exePath);
            if (n == null || _settings.Current.AppPins == null) return -1;
            int exeOnly = -1;
            foreach (var p in _settings.Current.AppPins)
            {
                if (p.Exe != n) continue;
                if (!string.IsNullOrEmpty(p.Profile)) { if (p.Profile == profile) return Clamp(p.DesktopIndex); }
                else exeOnly = p.DesktopIndex;
            }
            return exeOnly >= 0 ? Clamp(exeOnly) : -1;
        }

        private int Clamp(int i)
        {
            if (i < 0) i = 0;
            if (i >= Desktops.Count) i = Desktops.Count - 1;
            return i;
        }

        private static bool SameProfile(string a, string b)
        {
            return string.Equals(a ?? "", b ?? "", StringComparison.OrdinalIgnoreCase);
        }

        private static string ExeName(string exePath)
        {
            if (string.IsNullOrEmpty(exePath)) return null;
            try { return Path.GetFileName(exePath).ToLowerInvariant(); }
            catch { return null; }
        }

        private static string FallbackTitle(string exePath)
        {
            var n = ExeName(exePath);
            if (string.IsNullOrEmpty(n)) return "Window";
            int dot = n.LastIndexOf('.');
            var b = dot > 0 ? n.Substring(0, dot) : n;
            return b.Length > 0 ? char.ToUpperInvariant(b[0]) + b.Substring(1) : b;
        }

        // ---- remembered window placement -----------------------------------
        /// <summary>Capture where every window currently sits, to restore on the next launch.</summary>
        public void SavePlacements()
        {
            var list = new List<PlacementEntry>();
            foreach (var d in Desktops)
            {
                for (int i = 0; i < d.Windows.Count; i++)
                {
                    var w = d.Windows[i];
                    var n = ExeName(w.ExePath);
                    if (n == null) continue;
                    list.Add(new PlacementEntry { Exe = n, Profile = w.ProfileArg, Title = w.Title, Desktop = d.Index, Order = i });
                }
            }
            _settings.Current.Placements = list;
            _settings.Save();
        }

        // Consume the best matching saved placement for a window (same exe and profile, title preferred),
        // so duplicate windows of one app distribute across their remembered desktops.
        private int TakePlacement(string exePath, string profile, string title, out int order)
        {
            order = -1;
            if (!_settings.Current.RememberPlacement || _pendingPlacements == null || _pendingPlacements.Count == 0) return -1;
            var n = ExeName(exePath);
            if (n == null) return -1;
            PlacementEntry best = null;
            int bestScore = 0;
            foreach (var p in _pendingPlacements)
            {
                if (p.Exe != n || !SameProfile(p.Profile, profile)) continue;
                int score = (!string.IsNullOrEmpty(title) && p.Title == title) ? 2 : 1;
                if (score > bestScore) { bestScore = score; best = p; if (score == 2) break; }
            }
            if (best == null) return -1;
            _pendingPlacements.Remove(best);
            order = best.Order;
            return Clamp(best.Desktop);
        }

        // ---- internals ------------------------------------------------------
        // All-desktops mode routes to the parked engine, where windows are never hidden; the classic
        // engine below hides off-desktop windows and is used only in single-desktop mode. Both apply
        // their changes in one deferred batch so a switch paints in a single visual update.
        private void ApplyVisibility(bool capturePreviews = true)
        {
            if (_settings.Current.AltTabAllDesktops) { ApplyVisibilityParked(); return; }

            bool persist = _settings.Current.PersistPreviews && capturePreviews;
            var d = DesktopAt(ActiveIndex);

            var hides = new List<WindowModel>();
            foreach (var kv in _byHwnd)
            {
                var w = kv.Value;
                if (w.DesktopIndex == ActiveIndex) continue;
                if (w.Hwnd == IntPtr.Zero || !NM.IsWindow(w.Hwnd)) continue;
                if (!NM.IsWindowVisible(w.Hwnd) && !w.Parked) continue;
                // Snapshot the window for the hover preview while it is still on screen, then hide it.
                if (persist && NM.IsWindowVisible(w.Hwnd)) PreviewCache.Capture(w.Hwnd);
                hides.Add(w);
            }

            var shows = new List<WindowModel>();
            if (d != null)
                foreach (var w in d.Windows)
                {
                    if (w.Hwnd == IntPtr.Zero || !NM.IsWindow(w.Hwnd)) continue;
                    if (NM.IsWindowVisible(w.Hwnd) && !w.Parked) continue;
                    shows.Add(w);
                }

            if (hides.Count == 0 && shows.Count == 0) return;

            IntPtr batch = NM.BeginDeferWindowPos(hides.Count + shows.Count);
            foreach (var w in hides)
            {
                // A window that was parked off-screen gets its real position back as it hides, so it
                // can never be shown later at the parking coordinates and look gone.
                int x = 0, y = 0;
                uint move = NM.SWP_NOMOVE;
                if (w.HasParkPos) { x = w.ParkLeft; y = w.ParkTop; move = 0; w.HasParkPos = false; }
                w.Parked = false;
                if (batch != IntPtr.Zero && !NM.IsHungAppWindow(w.Hwnd))
                {
                    batch = NM.DeferWindowPos(batch, w.Hwnd, IntPtr.Zero, x, y, 0, 0,
                        NM.SWP_HIDEWINDOW | NM.SWP_NOSIZE | NM.SWP_NOACTIVATE | NM.SWP_NOZORDER | move);
                    if (batch != IntPtr.Zero) continue;
                }
                NM.ShowWindowAsync(w.Hwnd, NM.SW_HIDE);
                if (move == 0)
                    NM.SetWindowPos(w.Hwnd, IntPtr.Zero, x, y, 0, 0,
                        NM.SWP_NOSIZE | NM.SWP_NOACTIVATE | NM.SWP_NOZORDER | NM.SWP_ASYNCWINDOWPOS);
            }
            foreach (var w in shows)
            {
                bool lift = w.Parked;
                int x = 0, y = 0;
                uint move = NM.SWP_NOMOVE;
                if (w.HasParkPos) { x = w.ParkLeft; y = w.ParkTop; move = 0; w.HasParkPos = false; }
                w.Parked = false;
                if (batch != IntPtr.Zero && !NM.IsHungAppWindow(w.Hwnd))
                {
                    batch = NM.DeferWindowPos(batch, w.Hwnd, lift ? NM.HWND_TOP : IntPtr.Zero, x, y, 0, 0,
                        NM.SWP_SHOWWINDOW | NM.SWP_NOSIZE | NM.SWP_NOACTIVATE | move | (lift ? 0 : NM.SWP_NOZORDER));
                    if (batch != IntPtr.Zero) continue;
                }
                NM.ShowWindowAsync(w.Hwnd, NM.SW_SHOWNA);
            }
            if (batch != IntPtr.Zero) NM.EndDeferWindowPos(batch);
        }

        // ---- parked visibility engine (all-desktops mode) --------------------
        // Windows are never hidden here, because a hidden window drops out of the system Alt+Tab list
        // and the list then has to be rebuilt on every gesture, which is what made the switcher
        // visibly assemble its tiles. Off-desktop windows instead stay visible parked under the
        // wallpaper (native desktops keep windows visible too; they cloak them, which is not
        // available cross-process). A desktop switch is one atomic z shuffle with no show or hide,
        // so nothing rebuilds and rapid cross-desktop tabbing sees a complete list every time.
        // 0 = untested, 1 = below-shell parking works, 2 = rejected, park off-screen instead.
        private int _parkProbe;

        private void ApplyVisibilityParked()
        {
            var d = DesktopAt(ActiveIndex);

            var parks = new List<WindowModel>();
            foreach (var kv in _byHwnd)
            {
                var w = kv.Value;
                if (w.DesktopIndex == ActiveIndex) continue;
                if (w.Hwnd == IntPtr.Zero || !NM.IsWindow(w.Hwnd)) continue;
                if (w.Parked) continue;
                parks.Add(w);
            }

            var unparks = new List<WindowModel>();
            if (d != null)
                foreach (var w in d.Windows)
                {
                    if (w.Hwnd == IntPtr.Zero || !NM.IsWindow(w.Hwnd)) continue;
                    if (!w.Parked && NM.IsWindowVisible(w.Hwnd)) continue;
                    unparks.Add(w);
                }

            if (parks.Count == 0 && unparks.Count == 0) return;

            IntPtr shell = NM.GetShellWindow();
            bool offscreen = _parkProbe == 2 || shell == IntPtr.Zero;
            IntPtr batch = NM.BeginDeferWindowPos(parks.Count + unparks.Count + 1);
            foreach (var w in parks) batch = ParkOne(batch, w, shell, offscreen);
            foreach (var w in unparks) batch = UnparkOne(batch, w);
            // The OS raises the chosen window when it activates it, BEFORE this batch runs, and every
            // unpark above lands on top, so the last icon's window buried the very window the user
            // picked, which read as the switch going to some third window. Cap the batch by raising
            // whichever window holds focus on this desktop, so the stack always ends with the focused
            // window in front.
            IntPtr fg = NM.GetForegroundWindow();
            WindowModel fgw;
            if (fg != IntPtr.Zero && _byHwnd.TryGetValue(fg, out fgw) && fgw.DesktopIndex == ActiveIndex)
            {
                if (batch != IntPtr.Zero && !NM.IsHungAppWindow(fg))
                    batch = NM.DeferWindowPos(batch, fg, NM.HWND_TOP, 0, 0, 0, 0,
                        NM.SWP_NOMOVE | NM.SWP_NOSIZE | NM.SWP_NOACTIVATE);
                if (batch == IntPtr.Zero)
                    NM.SetWindowPos(fg, NM.HWND_TOP, 0, 0, 0, 0,
                        NM.SWP_NOMOVE | NM.SWP_NOSIZE | NM.SWP_NOACTIVATE | NM.SWP_ASYNCWINDOWPOS);
            }
            if (batch != IntPtr.Zero) NM.EndDeferWindowPos(batch);

            // Probe once whether the OS honored below-shell placement. Nothing sits under the shell
            // window otherwise, so an empty slot there means the request was clamped to the normal
            // bottom, where parked windows would peek out around the desktop; those windows are then
            // re-parked off-screen, which cannot be refused.
            if (_parkProbe == 0 && parks.Count > 0 && shell != IntPtr.Zero)
            {
                _parkProbe = (NM.GetWindow(shell, NM.GW_HWNDNEXT) != IntPtr.Zero) ? 1 : 2;
                if (_parkProbe == 2)
                {
                    foreach (var w in parks) w.Parked = false;
                    ApplyVisibilityParked();
                }
            }
        }

        private static IntPtr ParkOne(IntPtr batch, WindowModel w, IntPtr shell, bool offscreen)
        {
            const uint baseFlags = NM.SWP_NOSIZE | NM.SWP_NOACTIVATE | NM.SWP_SHOWWINDOW;
            int x = 0, y = 0;
            uint move = NM.SWP_NOMOVE;
            if (offscreen)
            {
                NM.RECT r;
                if (!w.HasParkPos && NM.GetWindowRect(w.Hwnd, out r))
                {
                    w.ParkLeft = r.Left;
                    w.ParkTop = r.Top;
                    w.HasParkPos = true;
                }
                x = -31000; y = -31000; move = 0;
            }
            IntPtr after = offscreen ? NM.HWND_BOTTOM : shell;
            w.Parked = true;
            if (batch != IntPtr.Zero && !NM.IsHungAppWindow(w.Hwnd))
            {
                batch = NM.DeferWindowPos(batch, w.Hwnd, after, x, y, 0, 0, baseFlags | move);
                if (batch != IntPtr.Zero) return batch;
            }
            NM.SetWindowPos(w.Hwnd, after, x, y, 0, 0, baseFlags | move | NM.SWP_ASYNCWINDOWPOS);
            return batch;
        }

        private static IntPtr UnparkOne(IntPtr batch, WindowModel w)
        {
            const uint baseFlags = NM.SWP_NOSIZE | NM.SWP_NOACTIVATE | NM.SWP_SHOWWINDOW;
            int x = 0, y = 0;
            uint move = NM.SWP_NOMOVE;
            if (w.HasParkPos) { x = w.ParkLeft; y = w.ParkTop; move = 0; w.HasParkPos = false; }
            w.Parked = false;
            if (batch != IntPtr.Zero && !NM.IsHungAppWindow(w.Hwnd))
            {
                batch = NM.DeferWindowPos(batch, w.Hwnd, NM.HWND_TOP, x, y, 0, 0, baseFlags | move);
                if (batch != IntPtr.Zero) return batch;
            }
            NM.SetWindowPos(w.Hwnd, NM.HWND_TOP, x, y, 0, 0, baseFlags | move | NM.SWP_ASYNCWINDOWPOS);
            return batch;
        }

        /// <summary>Push back off-desktop windows that raised themselves above the shell band, using
        /// the tracker's z scan (EnumWindows order, top to bottom). An app raising its own parked
        /// window would otherwise appear over the current desktop without a foreground event.</summary>
        public void HealStrayParks(List<IntPtr> zTopToBottom)
        {
            if (!_settings.Current.AltTabAllDesktops || _parkProbe != 1) return;
            if (AltTabActive || _altTabEnding || InSwitchSettle) return;
            IntPtr shell = NM.GetShellWindow();
            if (shell == IntPtr.Zero) return;
            IntPtr fg = NM.GetForegroundWindow();
            foreach (var h in zTopToBottom)
            {
                if (h == shell) break; // everything from here down is parked deep enough
                if (h == fg) continue;
                WindowModel w;
                if (!_byHwnd.TryGetValue(h, out w)) continue;
                if (w.DesktopIndex == ActiveIndex || !w.Parked) continue;
                ParkOne(IntPtr.Zero, w, shell, false);
            }
        }

        private void ShowWin(WindowModel w)
        {
            if (w == null || w.Hwnd == IntPtr.Zero || !NM.IsWindow(w.Hwnd)) return;
            // A parked window comes back on top with its position restored; anything else keeps its
            // stored z. Async avoids blocking on a hung target, and no variant steals focus.
            if (w.Parked || w.HasParkPos)
            {
                int x = 0, y = 0;
                uint move = NM.SWP_NOMOVE;
                if (w.HasParkPos) { x = w.ParkLeft; y = w.ParkTop; move = 0; w.HasParkPos = false; }
                w.Parked = false;
                NM.SetWindowPos(w.Hwnd, NM.HWND_TOP, x, y, 0, 0,
                    NM.SWP_NOSIZE | NM.SWP_NOACTIVATE | NM.SWP_SHOWWINDOW | move | NM.SWP_ASYNCWINDOWPOS);
                return;
            }
            NM.ShowWindowAsync(w.Hwnd, NM.SW_SHOWNA);
        }

        // Off-desktop placement for a single window: park it in all-desktops mode (it stays in the
        // Alt+Tab list), hide it in single-desktop mode.
        private void HideOrPark(WindowModel w)
        {
            if (w == null || w.Hwnd == IntPtr.Zero || !NM.IsWindow(w.Hwnd)) return;
            if (_settings.Current.AltTabAllDesktops)
            {
                IntPtr shell = NM.GetShellWindow();
                ParkOne(IntPtr.Zero, w, shell, _parkProbe == 2 || shell == IntPtr.Zero);
                return;
            }
            HideWin(w.Hwnd);
        }

        private static void HideWin(IntPtr h)
        {
            if (h == IntPtr.Zero || !NM.IsWindow(h)) return;
            NM.ShowWindowAsync(h, NM.SW_HIDE);
        }

        private void Reindex()
        {
            for (int i = 0; i < Desktops.Count; i++)
            {
                Desktops[i].Index = i;
                foreach (var w in Desktops[i].Windows) w.DesktopIndex = i;
            }
        }

        public void SaveDesktops() { PersistDesktops(); }

        /// <summary>Rebuild the desktops to match the current settings (after a restore), keeping the
        /// windows that are open and clamping them to a valid desktop.</summary>
        public void ReloadFromSettings()
        {
            var s = _settings.Current;
            int want = s.DesktopCount < 1 ? 1 : s.DesktopCount;
            while (Desktops.Count < want)
            {
                int i = Desktops.Count;
                Desktops.Add(new DesktopModel { Index = i, Name = NameFor(s.DesktopNames, i), Rows = RowsFor(s.DesktopRows, i) });
            }
            while (Desktops.Count > want && Desktops.Count > 1)
            {
                var last = Desktops[Desktops.Count - 1];
                var prev = Desktops[Desktops.Count - 2];
                foreach (var w in last.Windows.ToList()) { last.Windows.Remove(w); prev.Windows.Add(w); }
                Desktops.Remove(last);
            }
            for (int i = 0; i < Desktops.Count; i++)
            {
                Desktops[i].Name = NameFor(s.DesktopNames, i);
                Desktops[i].Rows = RowsFor(s.DesktopRows, i);
            }
            Reindex();
            if (ActiveIndex >= Desktops.Count) ActiveIndex = Desktops.Count - 1;
            if (ActiveIndex < 0) ActiveIndex = 0;
            for (int i = 0; i < Desktops.Count; i++) Desktops[i].IsActive = (i == ActiveIndex);
            ApplyVisibility();
            RaiseLayout();
        }

        private void PersistDesktops()
        {
            _settings.Current.DesktopCount = Desktops.Count;
            _settings.Current.DesktopNames = Desktops.Select(x => x.Name).ToList();
            _settings.Current.DesktopRows = Desktops.Select(x => x.Rows).ToList();
            _settings.Save();
        }

        private void ScheduleTrim()
        {
            if (_trimTimer == null || !_settings.Current.TrimHiddenMemory) return;
            _trimTimer.Stop();
            _trimTimer.Interval = TimeSpan.FromMilliseconds(_settings.Current.TrimDelayMs);
            _trimTimer.Start();
        }

        private void OnTrimTick(object sender, EventArgs e)
        {
            _trimTimer.Stop();
            if (!_settings.Current.TrimHiddenMemory) return;
            try
            {
                var activePids = new HashSet<uint>();
                foreach (var kv in _byHwnd)
                    if (kv.Value.DesktopIndex == ActiveIndex) activePids.Add(kv.Value.Pid);

                var done = new HashSet<uint>();
                foreach (var kv in _byHwnd)
                {
                    var w = kv.Value;
                    if (w.DesktopIndex == ActiveIndex) continue;
                    if (w.Pid == 0 || activePids.Contains(w.Pid)) continue;
                    if (done.Add(w.Pid)) NM.TrimProcessMemory(w.Pid);
                }
            }
            catch (Exception ex) { Log.Error("trim tick", ex); }
        }
    }
}
