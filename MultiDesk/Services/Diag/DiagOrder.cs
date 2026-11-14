using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using MultiDesk.Models;

namespace MultiDesk.Services
{
    /// <summary>
    /// Icon order tracing. Defines DiagOrder, which holds the slot every window occupies in the pane
    /// and compares it against the live one on each heartbeat. Code that means to change the order
    /// declares why first, so a difference found with nothing declared is an icon that moved on its
    /// own, reported by name with the slot it left and the slot it took.
    /// Doc: docs/notes/MultiDesk/Services/Diag.md
    /// </summary>
    internal static class DiagOrder
    {
        // How long a declared cause covers the change it announced. One heartbeat is 500ms, and a
        // reorder can take a further frame or two to reach the collections, so this spans several.
        private const int CauseWindowMs = 2500;
        private const int MaxNamed = 12;
        private const int MaxCauses = 4;

        private sealed class Slot
        {
            public int Desktop;
            public int Index;
            public string Title;
        }

        private static Dictionary<IntPtr, Slot> _last;
        private static string _cause;
        private static int _causeTick;

        public static void Arm() { DiagChannels.Arm(DiagChannels.Order); }

        /// <summary>Announce a reorder this code is about to perform, so the change it produces is
        /// recognized as asked for rather than reported as spontaneous.</summary>
        public static void Declare(string cause)
        {
            if (cause == null || !Diag.Enabled) return;
            if (_cause == null || Environment.TickCount - _causeTick >= CauseWindowMs) _cause = cause;
            else if (_cause.IndexOf(cause, StringComparison.Ordinal) < 0) _cause = Chain(_cause, cause);
            _causeTick = Environment.TickCount;
        }

        // The chain keeps the newest causes and drops the oldest, so it stays readable and can never
        // reach a length at which further causes stop being recorded.
        private static string Chain(string existing, string added)
        {
            var parts = existing.Split('>');
            int keep = Math.Min(parts.Length, MaxCauses - 1);
            var text = "";
            for (int i = parts.Length - keep; i < parts.Length; i++) text += parts[i] + ">";
            return text + added;
        }

        /// <summary>Read the live order and account for any difference from the last reading.</summary>
        public static void Sample(IEnumerable<DesktopModel> desktops)
        {
            if (!Diag.Enabled || desktops == null) return;
            try
            {
                var now = Read(desktops);
                if (_last == null) { _last = now; return; }
                var diff = Diff(_last, now);
                if (diff == null) return;
                _last = now;

                bool declared = _cause != null && Environment.TickCount - _causeTick < CauseWindowMs;
                if (declared)
                {
                    Diag.Write(DiagChannels.Order, "reordered", "cause=" + _cause + " | " + diff);
                    _cause = null;
                    return;
                }
                Diag.Write(DiagChannels.Order, "changed-unasked", diff);
                DiagBug.Report("icon-order-changed-with-no-cause", "pane",
                    diff + " | last-recorded-cause=" + (_cause ?? "none")
                    + " age=" + (_cause == null ? "n/a"
                        : (Environment.TickCount - _causeTick).ToString(CultureInfo.InvariantCulture) + "ms"));
            }
            catch { }
        }

        private static Dictionary<IntPtr, Slot> Read(IEnumerable<DesktopModel> desktops)
        {
            var map = new Dictionary<IntPtr, Slot>();
            foreach (var d in desktops)
            {
                if (d == null || d.Windows == null) continue;
                for (int i = 0; i < d.Windows.Count; i++)
                {
                    var w = d.Windows[i];
                    if (w == null || w.Hwnd == IntPtr.Zero) continue;
                    map[w.Hwnd] = new Slot { Desktop = d.Index, Index = i, Title = w.Title };
                }
            }
            return map;
        }

        // Every window whose slot differs, named. Null when the two readings agree exactly.
        private static string Diff(Dictionary<IntPtr, Slot> before, Dictionary<IntPtr, Slot> after)
        {
            var moved = new List<string>();
            var gone = new List<string>();
            var fresh = new List<string>();
            foreach (var kv in before)
            {
                Slot a;
                if (!after.TryGetValue(kv.Key, out a)) { gone.Add(DiagFormat.Q(kv.Value.Title) + " " + At(kv.Value)); continue; }
                if (a.Desktop != kv.Value.Desktop || a.Index != kv.Value.Index)
                    moved.Add(DiagFormat.Q(a.Title) + " " + At(kv.Value) + "->" + At(a));
            }
            foreach (var kv in after)
                if (!before.ContainsKey(kv.Key)) fresh.Add(DiagFormat.Q(kv.Value.Title) + " " + At(kv.Value));
            if (moved.Count == 0 && gone.Count == 0 && fresh.Count == 0) return null;

            var text = new StringBuilder();
            Append(text, "moved", moved);
            Append(text, "added", fresh);
            Append(text, "removed", gone);
            return text.ToString();
        }

        private static void Append(StringBuilder text, string label, List<string> items)
        {
            if (items.Count == 0) return;
            if (text.Length > 0) text.Append(' ');
            text.Append(label).Append('=').Append(items.Count.ToString(CultureInfo.InvariantCulture)).Append(" [");
            int shown = Math.Min(MaxNamed, items.Count);
            for (int i = 0; i < shown; i++)
            {
                if (i > 0) text.Append(", ");
                text.Append(items[i]);
            }
            if (items.Count > shown)
                text.Append(", and ").Append((items.Count - shown).ToString(CultureInfo.InvariantCulture))
                    .Append(" further entries not named on this line");
            text.Append(']');
        }

        private static string At(Slot s)
        {
            return "d" + s.Desktop.ToString(CultureInfo.InvariantCulture)
                + "#" + s.Index.ToString(CultureInfo.InvariantCulture);
        }
    }
}
