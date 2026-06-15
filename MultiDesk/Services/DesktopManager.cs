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
            if (target != ActiveIndex) HideWin(m.Hwnd); // pinned or remembered on another desktop
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
            // Completing an Alt+Tab: the OS just activated the window the user chose, so switch to its
            // desktop now. Acting on this event is reliable, unlike reading the foreground on a fixed timer,
            // which can fire before the selection has settled and snap back to the previous window.
            if (_altTabEnding && active != null)
            {
                _altTabEnding = false;
                AltTabActive = false;
                SwitchTo(active.DesktopIndex, false);
                return;
            }

            // Only follow focus to a window that is genuinely visible (not one being hidden during a
            // switch), and not during the brief settle window after a switch, so we never bounce back.
            if (active != null && active.DesktopIndex != ActiveIndex && _settings.Current.AutoSwitchOnForeground
                && Environment.TickCount >= _switchGuardUntil && !AltTabActive && NM.IsWindowVisible(hwnd))
                SwitchTo(active.DesktopIndex, false);
        }

        // ---- desktop operations --------------------------------------------
        public void SwitchTo(int index, bool activateTop = true)
        {
            if (index < 0 || index >= Desktops.Count) return;
            _switchGuardUntil = Environment.TickCount + 600; // settle window for focus-follow
            ActiveIndex = index;
            for (int i = 0; i < Desktops.Count; i++) Desktops[i].IsActive = (i == index);
            ApplyVisibility();
            if (activateTop)
            {
                var d = DesktopAt(index);
                var top = (d != null && d.Windows.Count > 0) ? d.Windows[d.Windows.Count - 1] : null;
                if (top != null) WindowActions.ForceForeground(top.Hwnd);
            }
            ScheduleTrim();
            var h = ActiveChanged; if (h != null) h();
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
            if (target == ActiveIndex) ShowWin(m.Hwnd); else HideWin(m.Hwnd);
        }

        /// <summary>Reorder a window to sit before another, moving desktops if they differ. Used for
        /// drag-to-reorder of the icons within or across desktops.</summary>
        public void MoveWindowBefore(IntPtr draggedHwnd, IntPtr targetHwnd)
        {
            WindowModel dragged, target;
            if (!_byHwnd.TryGetValue(draggedHwnd, out dragged)) return;
            if (!_byHwnd.TryGetValue(targetHwnd, out target)) return;
            if (dragged == target) return;
            int targetDesktop = target.DesktopIndex;
            var from = DesktopAt(dragged.DesktopIndex);
            var to = DesktopAt(targetDesktop);
            if (from != null) from.Windows.Remove(dragged);
            dragged.DesktopIndex = targetDesktop;
            if (to != null)
            {
                int idx = to.Windows.IndexOf(target);
                if (idx < 0) idx = to.Windows.Count;
                to.Windows.Insert(idx, dragged);
            }
            if (targetDesktop == ActiveIndex) ShowWin(dragged.Hwnd); else HideWin(dragged.Hwnd);
        }

        /// <summary>Safety: reveal every managed window so none is left hidden on a hidden desktop.</summary>
        public void ShowAllWindows()
        {
            foreach (var w in _byHwnd.Values) ShowWin(w.Hwnd);
        }

        public void RefreshAll() { ApplyVisibility(); }

        // ---- Alt+Tab across desktops (optional) ----------------------------
        /// <summary>True while the user holds an Alt+Tab that is revealing all desktops' windows.</summary>
        public bool AltTabActive { get; private set; }

        /// <summary>Reveal every window so the system Alt+Tab can list windows from all desktops.</summary>
        public void BeginAltTab()
        {
            AltTabActive = true;
            _altTabEnding = false;
            // A fresh Alt+Tab cancels a still-pending commit from the previous one, so a late commit can
            // never fire mid-gesture and re-hide the windows we just revealed.
            if (_altTabCommit != null) _altTabCommit.Stop();
            // Reveal synchronously (not the async ShowAllWindows) so each window has WS_VISIBLE set before
            // Windows enumerates the Alt+Tab list. That is what makes all desktops appear every time.
            foreach (var w in _byHwnd.Values.ToList())
                if (w.Hwnd != IntPtr.Zero && NM.IsWindow(w.Hwnd)) NM.ShowWindow(w.Hwnd, NM.SW_SHOWNA);
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
            if (_altTabCommit == null)
            {
                _altTabCommit = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
                _altTabCommit.Tick += (s, e) =>
                {
                    _altTabCommit.Stop();
                    if (!_altTabEnding) return; // already committed from the foreground event
                    _altTabEnding = false;
                    AltTabActive = false;
                    IntPtr fg = NM.GetForegroundWindow();
                    WindowModel m;
                    if (fg != IntPtr.Zero && _byHwnd.TryGetValue(fg, out m)) SwitchTo(m.DesktopIndex, false);
                    else ApplyVisibility();
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
        private void ApplyVisibility()
        {
            bool persist = _settings.Current.PersistPreviews;
            foreach (var kv in _byHwnd)
            {
                var w = kv.Value;
                if (w.DesktopIndex == ActiveIndex) ShowWin(w.Hwnd);
                else
                {
                    // Snapshot the window for the hover preview while it is still on screen, then hide it.
                    if (persist && NM.IsWindowVisible(w.Hwnd)) PreviewCache.Capture(w.Hwnd);
                    HideWin(w.Hwnd);
                }
            }
        }

        private static void ShowWin(IntPtr h)
        {
            if (h == IntPtr.Zero || !NM.IsWindow(h)) return;
            // Async show avoids blocking on a hung target, and SHOWNA does not steal focus.
            NM.ShowWindowAsync(h, NM.SW_SHOWNA);
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
