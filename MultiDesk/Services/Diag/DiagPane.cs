using System;
using System.Globalization;
using System.Windows.Threading;
using NM = MultiDesk.Interop.NativeMethods;

namespace MultiDesk.Services
{
    /// <summary>
    /// Pane geometry tracing. Defines DiagPane, which holds the bar's last known rectangle and
    /// thickness and compares them against the live ones. Readings arrive on every frame of a drag,
    /// so they are held and evaluated once the bar has stopped moving, which is what keeps one drag
    /// to one line. Code that means to resize the bar declares why first, and a change found with
    /// nothing declared is reported. Also measures the width the icon grid leaves beyond its last
    /// whole column.
    /// Doc: docs/notes/MultiDesk/Services/Diag.md
    /// </summary>
    internal static class DiagPane
    {
        private const int CauseWindowMs = 2500;
        private const int SettleMs = 400;
        private const int MaxCauses = 4;
        // A quarter of a cell is wide enough to read as empty space next to the icons, and narrow
        // enough that ordinary device-pixel rounding never reaches it.
        private const double SlackShare = 0.25;

        private static string _cause;
        private static int _causeTick;
        private static bool _have;
        private static NM.RECT _rect;
        private static int _thickness;
        private static string _edge;
        private static int _lastSlackPx = int.MinValue;

        private static DispatcherTimer _settle;
        private static IntPtr _pendingBar;
        private static int _pendingThickness;
        private static string _pendingEdge;
        private static double _pendingViewport, _pendingCell, _pendingScale;

        public static void Arm() { DiagChannels.Arm(DiagChannels.Pane); }

        /// <summary>Announce a resize this code is about to perform. Calls arriving while an earlier
        /// cause is still current extend it into a chain of the most recent few, so a resize that
        /// passed through several layers reads as the route it took.</summary>
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

        /// <summary>Hand in the bar's live geometry and the icon grid's fit. The reading is held and
        /// acted on once the bar has been still for a moment. Viewport and cell are the wrapping axis
        /// in device-independent pixels: width for a bar docked to a side, height for one docked top
        /// or bottom.</summary>
        public static void Sample(IntPtr bar, int thicknessPx, string edge,
            double viewportDip, double cellDip, double dpiScale)
        {
            if (!Diag.Enabled) return;
            _pendingBar = bar;
            _pendingThickness = thicknessPx;
            _pendingEdge = edge;
            _pendingViewport = viewportDip;
            _pendingCell = cellDip;
            _pendingScale = dpiScale;
            if (_settle == null)
            {
                _settle = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(SettleMs) };
                _settle.Tick += OnSettled;
            }
            _settle.Stop();
            _settle.Start();
        }

        private static void OnSettled(object sender, EventArgs e)
        {
            if (_settle != null) _settle.Stop();
            if (!Diag.Enabled) return;
            try
            {
                var r = new NM.RECT();
                if (_pendingBar != IntPtr.Zero && NM.IsWindow(_pendingBar))
                {
                    NM.RECT live;
                    if (NM.GetWindowRect(_pendingBar, out live)) r = live;
                }

                if (!_have)
                {
                    _have = true; _rect = r; _thickness = _pendingThickness; _edge = _pendingEdge;
                    Diag.Write(DiagChannels.Pane, "first-reading", Describe(r, _pendingThickness, _pendingEdge));
                    Slack();
                    return;
                }

                bool rectChanged = !DiagFormat.Near(r, _rect, 1);
                bool thickChanged = _pendingThickness != _thickness;
                bool edgeChanged = !string.Equals(_pendingEdge, _edge, StringComparison.Ordinal);
                if (rectChanged || thickChanged || edgeChanged)
                {
                    var change = "was " + Describe(_rect, _thickness, _edge)
                        + " | now " + Describe(r, _pendingThickness, _pendingEdge);
                    _rect = r; _thickness = _pendingThickness; _edge = _pendingEdge;
                    bool declared = _cause != null && Environment.TickCount - _causeTick < CauseWindowMs;
                    if (declared)
                    {
                        Diag.Write(DiagChannels.Pane, "resized", "cause=" + _cause + " | " + change);
                        _cause = null;
                    }
                    else
                    {
                        Diag.Write(DiagChannels.Pane, "resized-unasked", change);
                        DiagBug.Report("pane-resized-with-no-cause", "pane", change
                            + " | last-recorded-cause=" + (_cause ?? "none")
                            + " age=" + (_cause == null ? "n/a"
                                : (Environment.TickCount - _causeTick).ToString(CultureInfo.InvariantCulture) + "ms"));
                    }
                }
                Slack();
            }
            catch { }
        }

        // The width the icon grid has beyond its last whole column, which shows as empty space beside
        // the icons until the bar is resized by hand. The reading is taken once the bar has settled,
        // and an unchanged measurement is not reported again.
        private static void Slack()
        {
            double viewportDip = _pendingViewport, cellDip = _pendingCell;
            if (viewportDip <= 4 || cellDip <= 4) { _lastSlackPx = int.MinValue; return; }
            int cols = (int)Math.Floor((viewportDip + 0.5) / cellDip);
            if (cols < 1) { _lastSlackPx = int.MinValue; return; }
            double slackDip = viewportDip - cols * cellDip;
            if (slackDip < cellDip * SlackShare) { _lastSlackPx = int.MinValue; return; }

            double scale = _pendingScale > 0 ? _pendingScale : 1.0;
            int slackPx = (int)Math.Round(slackDip * scale);
            if (slackPx == _lastSlackPx) return;
            _lastSlackPx = slackPx;

            var detail = "columns=" + cols.ToString(CultureInfo.InvariantCulture)
                + " cell=" + cellDip.ToString("0.0", CultureInfo.InvariantCulture) + "dip"
                + " viewport=" + viewportDip.ToString("0.0", CultureInfo.InvariantCulture) + "dip"
                + " unused=" + slackDip.ToString("0.0", CultureInfo.InvariantCulture) + "dip"
                + " (" + slackPx.ToString(CultureInfo.InvariantCulture) + "px)"
                + " thickness=" + _pendingThickness.ToString(CultureInfo.InvariantCulture) + "px"
                + " whole-column-thickness=" + WholeColumns(viewportDip, cellDip, scale, _pendingThickness, cols)
                    .ToString(CultureInfo.InvariantCulture) + "px";
            Diag.Write(DiagChannels.Pane, "grid-slack", detail);
            DiagBug.Report("icon-grid-leaves-unused-width", "pane", detail);
        }

        // The bar thickness that would leave no gap, using the chrome the current reading implies.
        private static int WholeColumns(double viewportDip, double cellDip, double scale, int thicknessPx, int cols)
        {
            double chromeDip = thicknessPx / scale - viewportDip;
            if (chromeDip < 0) chromeDip = 0;
            return (int)Math.Round((chromeDip + cols * cellDip) * scale);
        }

        private static string Describe(NM.RECT r, int thicknessPx, string edge)
        {
            return "rect=" + DiagFormat.R(r)
                + " thickness=" + thicknessPx.ToString(CultureInfo.InvariantCulture) + "px"
                + " edge=" + (edge ?? "?");
        }
    }
}
