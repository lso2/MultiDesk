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
    /// one. Switching shows the active desktop's windows and hides or parks the rest (see
    /// DesktopManager.Visibility.cs for why there are two mechanisms and why parking is what lets the
    /// real, unmodified Windows Alt+Tab see every desktop's windows on its own). Off-desktop windows
    /// can have their working set trimmed after a delay to return physical memory under heavy load.
    /// Split into partials: this file holds the lifecycle, desktop operations, and pins;
    /// DesktopManager.Foreground.cs holds the foreground-follow logic that keeps focus-follow honest;
    /// DesktopManager.Placements.cs holds remembered window placement across restarts;
    /// DesktopManager.Visibility.cs holds the one visibility engine.
    /// </summary>
    public sealed partial class DesktopManager
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
        private DispatcherTimer _placementExpiry;
        private DispatcherTimer _placementSaveTimer;
        private List<PlacementEntry> _pendingPlacements;
        private int _switchGuardUntil; // ignore focus-follow briefly after a switch, to avoid bouncing back
        private string _switchCause = "startup"; // what asked for the switch whose visibility pass is running

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

            // Save where every window sits periodically, not only on a clean exit, so a crash or a kill
            // never loses more than a short moment of window movement. This is what lets a window found
            // hidden and untracked after an unexpected restart be matched back to its desktop instead of
            // staying invisible with nothing left running that still remembers where it belongs.
            _placementSaveTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(45) };
            _placementSaveTimer.Tick += (s, e) => { if (_settings.Current.RememberPlacement) SavePlacements(); };
            _placementSaveTimer.Start();
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
                // A process that already has a tracked window keeps that window's desktop. Many apps
                // (an IDE's floating tool panel, a file picker, Explorer's secondary frames) open a
                // second top-level HWND lazily, sometime after the main window, and defaulting it to
                // "whatever is active right now" splits one session across two desktops. Interacting
                // with the split-off piece later then reads as a deliberate switch to some other
                // desktop and drags that desktop's unrelated windows into view with it.
                //
                // Never for the window the user is in RIGHT NOW, though: a new window discovered
                // while it holds the foreground (a form's dialog, a save prompt, a picker) just
                // opened under the user's cursor, and inheriting a sibling's desktop would park it
                // off screen mid-interaction, which deactivates it, and a dialog or modal that loses
                // activation is pushed behind its app or closes itself on the spot. A focused new
                // window belongs on the desktop the user is on.
                if (target < 0 && NM.GetForegroundWindow() != hwnd) target = SiblingDesktopFor(pid);
                if (target < 0) target = ActiveIndex;
            }
            m = new WindowModel { Hwnd = hwnd, Title = title, Pid = pid, ExePath = exe, ProfileArg = profile, DesktopIndex = target };
            try { m.Icon = IconCache.ForWindow(hwnd, exe); } catch (Exception ex) { Log.Error("icon resolve", ex); }
            _byHwnd[hwnd] = m;
            DiagOrder.Declare("window-discovered");
            var d = DesktopAt(target);
            if (d != null)
            {
                if (placeOrder >= 0 && placeOrder <= d.Windows.Count) d.Windows.Insert(placeOrder, m);
                else d.Windows.Add(m);
            }
            if (Diag.Enabled)
                Diag.Write(DiagChannels.Track, "discovered", DiagFormat.Q(title)
                    + " hwnd=" + DiagFormat.H(hwnd)
                    + " desktop=" + target
                    + " slot=" + (placeOrder >= 0 ? placeOrder.ToString() : "end")
                    + " " + DiagWindow.Describe(DiagWindow.Capture(hwnd)));
            _switchCause = "window-discovered";
            // Onto another desktop, hidden or parked immediately. Onto the active desktop, repaired in
            // case it is a window an earlier MultiDesk process parked and never got to restore before
            // being killed, rather than trusted on sight to already be genuinely on screen.
            if (target != ActiveIndex) HideWin(m);
            else EnsureOnScreen(m);
            return m;
        }

        // The desktop of an existing window from the same process, or -1 if this process has none
        // yet. A sibling on the ACTIVE desktop wins outright: a background window of an app the user
        // is working with right here belongs right here, and picking whichever sibling happened to
        // enumerate first was scattering new windows onto other desktops seemingly at random.
        private int SiblingDesktopFor(uint pid)
        {
            if (pid == 0) return -1;
            int found = -1;
            foreach (var kv in _byHwnd)
            {
                if (kv.Value.Pid != pid) continue;
                if (kv.Value.DesktopIndex == ActiveIndex) return ActiveIndex;
                if (found < 0) found = kv.Value.DesktopIndex;
            }
            return found;
        }

        // A hidden window from a process that already has another window on the active desktop is a
        // secondary top-level window of a session the user is already in, not a deliberate visit to
        // wherever it happened to be tracked. Reuniting it here in place, instead of switching the
        // whole desktop to its DesktopIndex, is what stops clicking into an app's own popped-out
        // window from yanking the user to an unrelated desktop and surfacing that desktop's other
        // windows along with it. Returns true when it reunited the window (caller should not also
        // SwitchTo), false when no sibling was found on the active desktop (normal switch applies).
        private bool TryReuniteSibling(WindowModel w)
        {
            if (w == null || w.Pid == 0) return false;
            bool hasSiblingHere = false;
            foreach (var kv in _byHwnd)
            {
                if (kv.Value != w && kv.Value.Pid == w.Pid && kv.Value.DesktopIndex == ActiveIndex)
                { hasSiblingHere = true; break; }
            }
            if (!hasSiblingHere) return false;
            DiagOrder.Declare("sibling-reunite");
            _switchCause = "sibling-reunite";
            var from = DesktopAt(w.DesktopIndex);
            var to = DesktopAt(ActiveIndex);
            if (from != null) from.Windows.Remove(w);
            w.DesktopIndex = ActiveIndex;
            if (to != null) to.Windows.Add(w);
            ShowWin(w);
            return true;
        }

        public void RemoveWindow(IntPtr hwnd)
        {
            WindowModel m;
            if (!_byHwnd.TryGetValue(hwnd, out m)) return;
            if (Diag.Enabled)
                Diag.Write(DiagChannels.Track, "removed", DiagFormat.Q(m.Title)
                    + " hwnd=" + DiagFormat.H(hwnd) + " desktop=" + m.DesktopIndex
                    + " alive=" + (NM.IsWindow(hwnd) ? "1" : "0") + " state=" + DiagFormat.Flags(hwnd));
            DiagOrder.Declare("window-removed");
            DiagPark.Forget(hwnd);
            DiagNav.Forget(hwnd);
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

        // ---- desktop operations --------------------------------------------
        public void SwitchTo(int index, bool activateTop = true, bool capturePreviews = true, string cause = "unspecified")
        {
            if (index < 0 || index >= Desktops.Count) return;
            try
            {
                int from = ActiveIndex;
                _switchGuardUntil = Environment.TickCount + 600; // settle window for focus-follow
                ActiveIndex = index;
                for (int i = 0; i < Desktops.Count; i++) Desktops[i].IsActive = (i == index);
                _switchCause = cause;
                ApplyVisibility(capturePreviews);
                IntPtr activated = IntPtr.Zero;
                if (activateTop)
                {
                    var d = DesktopAt(index);
                    var top = (d != null && d.Windows.Count > 0) ? d.Windows[d.Windows.Count - 1] : null;
                    if (top != null)
                    {
                        activated = top.Hwnd;
                        GuardForeground(top.Hwnd);
                        WindowActions.ForceForeground(top.Hwnd);
                    }
                }
                var landed = DesktopAt(index);
                DiagNav.Switched(from, index, cause, landed != null ? landed.Windows.Count : 0, activated);
                ScheduleTrim();
                var h = ActiveChanged; if (h != null) h();
            }
            catch (Exception ex) { Log.Error("switch to desktop", ex); }
        }

        public void AddDesktop()
        {
            DiagOrder.Declare("desktop-added");
            int i = Desktops.Count;
            Desktops.Add(new DesktopModel { Index = i, Name = "Desktop " + (i + 1) });
            PersistDesktops();
            RaiseLayout();
        }

        public void RemoveDesktop(int index)
        {
            if (Desktops.Count <= 1) return;
            DiagOrder.Declare("desktop-removed-" + index);
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
            int wasActive = ActiveIndex;
            ActiveIndex = newActive.Index;
            DiagNav.ActiveChanged(wasActive, ActiveIndex, "desktop-removed-" + index);
            for (int i = 0; i < Desktops.Count; i++) Desktops[i].IsActive = (i == ActiveIndex);
            _switchCause = "desktop-removed";
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
            DiagOrder.Declare("desktop-reordered");
            var activeModel = DesktopAt(ActiveIndex);
            int wasActive = ActiveIndex;
            Desktops.Move(fromIndex, toIndex);
            Reindex();
            ActiveIndex = (activeModel != null) ? activeModel.Index : ActiveIndex;
            DiagNav.ActiveChanged(wasActive, ActiveIndex, "desktop-reordered");
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
            try
            {
                DiagOrder.Declare("moved-to-desktop-" + target);
                _switchCause = "moved-to-desktop-" + target;
                var from = DesktopAt(m.DesktopIndex);
                var to = DesktopAt(target);
                if (from != null) from.Windows.Remove(m);
                m.DesktopIndex = target;
                if (to != null) to.Windows.Add(m);
                if (target == ActiveIndex) ShowWin(m); else HideWin(m);
            }
            catch (Exception ex) { Log.Error("move window to desktop", ex); }
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
            try
            {
                DiagOrder.Declare("dragged-onto-tile");
                _switchCause = "dragged-onto-tile";
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
                if (targetDesktop == ActiveIndex) ShowWin(dragged); else HideWin(dragged);
            }
            catch (Exception ex) { Log.Error("move window to spot", ex); }
        }

        /// <summary>Safety: reveal every managed window so none is left hidden on a hidden desktop.</summary>
        public void ShowAllWindows()
        {
            _switchCause = "show-all-windows";
            foreach (var w in _byHwnd.Values) ShowWin(w);
        }

        public void RefreshAll() { _switchCause = "refresh-all"; ApplyVisibility(); }

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

        // The visibility engine (OnScreen, ApplyVisibility, ShowWin, HideWin, HealVisibility) lives in
        // DesktopManager.Visibility.cs, kept separate so this file stays about lifecycle and desktop
        // operations rather than the mechanics of how a window actually leaves and returns to the screen.

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
            DiagOrder.Declare("settings-reload");
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
            int wasActive = ActiveIndex;
            if (ActiveIndex >= Desktops.Count) ActiveIndex = Desktops.Count - 1;
            if (ActiveIndex < 0) ActiveIndex = 0;
            DiagNav.ActiveChanged(wasActive, ActiveIndex, "settings-reload");
            for (int i = 0; i < Desktops.Count; i++) Desktops[i].IsActive = (i == ActiveIndex);
            _switchCause = "settings-reload";
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
