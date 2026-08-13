using System.Collections.Generic;
using MultiDesk.Models;

namespace MultiDesk.Services
{
    /// <summary>
    /// Remembered window placement: where each window sat and its order, matched by executable, browser
    /// profile, and title, since window handles are not stable across a restart. Placements are saved
    /// periodically and on exit (DesktopManager.cs), consumed once at startup to restore the previous
    /// session (Initialize, in DesktopManager.cs), and checked here for as long as the process runs, to
    /// let WindowTracker recognize a window it must not lose even while that window is still hidden.
    /// </summary>
    public sealed partial class DesktopManager
    {
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
        // so duplicate windows of one app distribute across their remembered desktops. Only used for the
        // short startup window (see _placementExpiry in Initialize): after that, a new window goes to the
        // desktop the user is actually on, not an old remembered one.
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

        /// <summary>True if this exact window (same executable and title) was saved on a desktop, at
        /// any point up to the last periodic save. A window MultiDesk hid for another desktop before an
        /// unexpected restart looks, from the outside, exactly like a window that should never be
        /// tracked at all: both are simply not WS_VISIBLE, and nothing in that state says which one it
        /// is. This is what tells them apart instead of that state: only a hidden window that matches a
        /// remembered placement exactly is let through the tracker's discovery while still hidden,
        /// which is what lets a lost session be found again rather than staying invisible forever with
        /// nothing left running that still knew where it belonged. The match is intentionally exact on
        /// title, not just the executable: an executable can own more than one hidden top-level window
        /// that is never meant to be tracked at all, such as a shell helper window explorer.exe keeps
        /// open for its own use, and matching on the executable alone let every one of those through as
        /// soon as that executable had any legitimate placement of its own. Unlike TakePlacement, this
        /// never expires and consumes nothing, since losing no window is worth more than a slightly
        /// stale match.</summary>
        public bool HasPendingPlacementFor(string exePath, string title)
        {
            if (!_settings.Current.RememberPlacement) return false;
            var list = _settings.Current.Placements;
            if (list == null || list.Count == 0) return false;
            var n = ExeName(exePath);
            if (n == null) return false;
            foreach (var p in list) if (p.Exe == n && p.Title == title) return true;
            return false;
        }
    }
}
