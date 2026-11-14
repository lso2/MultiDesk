using System.Collections.Generic;
using MultiDesk.Models;

namespace MultiDesk.Services
{
    /// <summary>
    /// Remembered window placement. Defines SavePlacements, the placement matching used at launch,
    /// and HasPendingPlacementFor. A placement is matched by executable, browser profile and title.
    /// Doc: docs/notes/MultiDesk/Services/DesktopManager.Placements.cs.md
    /// </summary>
    public sealed partial class DesktopManager
    {
        /// <summary>Records the desktop and slot of every window, for the next launch.</summary>
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
            _settings.Persist();
        }

        // The saved placement matching a window best: same executable and profile, an exact title
        // preferred. null after the startup period (_placementExpiry in Initialize) or with none.
        private PlacementEntry BestPlacement(string exePath, string profile, string title)
        {
            if (!_settings.Current.RememberPlacement || _pendingPlacements == null || _pendingPlacements.Count == 0) return null;
            var n = ExeName(exePath);
            if (n == null) return null;
            PlacementEntry best = null;
            int bestScore = 0;
            foreach (var p in _pendingPlacements)
            {
                if (p.Exe != n || !SameProfile(p.Profile, profile)) continue;
                int score = (!string.IsNullOrEmpty(title) && p.Title == title) ? 2 : 1;
                if (score > bestScore) { bestScore = score; best = p; if (score == 2) break; }
            }
            return best;
        }

        // Consumes the best placement, and duplicate windows of one app spread across their
        // remembered desktops.
        private int TakePlacement(string exePath, string profile, string title, out int order)
        {
            var best = BestPlacement(exePath, profile, title);
            order = best != null ? best.Order : -1;
            if (best == null) return -1;
            _pendingPlacements.Remove(best);
            return Clamp(best.Desktop);
        }

        // The desktop TakePlacement gives this window, with the placement left in the list.
        private int PeekPlacement(string exePath, string profile, string title)
        {
            var best = BestPlacement(exePath, profile, title);
            return best != null ? Clamp(best.Desktop) : -1;
        }

        /// <summary>True when this exact window (same executable and title) has a saved placement,
        /// up to the last periodic save. WindowTracker admits a hidden window on this match only.</summary>
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
