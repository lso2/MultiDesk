using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Win32;
using MultiDesk.Services;

namespace MultiDesk.UI
{
    /// <summary>
    /// Settings, applied live. Controls initialize from the stored configuration and write straight
    /// back to it, with Save raising the Changed event the rest of the app already reacts to.
    /// </summary>
    public partial class SettingsWindow : Window
    {
        private static SettingsWindow _instance;
        private bool _loading;

        public SettingsWindow()
        {
            InitializeComponent();
            Loaded += (s, e) => LoadValues();
            Closed += (s, e) => { if (ReferenceEquals(_instance, this)) _instance = null; };

            ChkTitles.Click += (s, e) => { Cur.ShowTitles = ChkTitles.IsChecked == true; Save(); };
            ChkAutoHide.Click += (s, e) => { Cur.AutoHide = ChkAutoHide.IsChecked == true; Save(); };
            ChkAlwaysOnTop.Click += (s, e) => { Cur.AlwaysOnTop = ChkAlwaysOnTop.IsChecked == true; Save(); };
            ChkReserveSpace.Click += (s, e) => { Cur.ReserveSpace = ChkReserveSpace.IsChecked == true; Save(); };
            ChkShowCoffee.Click += (s, e) => { Cur.ShowCoffee = ChkShowCoffee.IsChecked == true; Save(); };
            ChkRemember.Click += (s, e) => { Cur.RememberPlacement = ChkRemember.IsChecked == true; Save(); };
            ChkAltTab.Click += (s, e) => { Cur.AltTabAllDesktops = ChkAltTab.IsChecked == true; Save(); App.AltTab.Apply(); };
            ChkPersistPreviews.Click += (s, e) => { Cur.PersistPreviews = ChkPersistPreviews.IsChecked == true; Save(); if (!Cur.PersistPreviews) PreviewCache.Clear(); };
            ChkAutoSwitch.Click += (s, e) => { Cur.AutoSwitchOnForeground = ChkAutoSwitch.IsChecked == true; Save(); };
            ChkTrim.Click += (s, e) => { Cur.TrimHiddenMemory = ChkTrim.IsChecked == true; Save(); };
            ChkSwitcher.Click += (s, e) => { Cur.SwitcherHotkeyEnabled = ChkSwitcher.IsChecked == true; Save(); };
            ChkStartup.Click += (s, e) => { Cur.StartWithWindows = ChkStartup.IsChecked == true; SetStartup(Cur.StartWithWindows); Save(); };
        }

        public static void ShowSingleton()
        {
            if (_instance != null) { _instance.Activate(); return; }
            var w = new SettingsWindow();
            _instance = w;
            w.Show();
            w.Activate();
        }

        private static Models.MultiDeskSettings Cur { get { return App.Settings.Current; } }

        private void LoadValues()
        {
            _loading = true;
            ChkTitles.IsChecked = Cur.ShowTitles;
            ChkAutoHide.IsChecked = Cur.AutoHide;
            ChkAlwaysOnTop.IsChecked = Cur.AlwaysOnTop;
            ChkReserveSpace.IsChecked = Cur.ReserveSpace;
            ChkShowCoffee.IsChecked = Cur.ShowCoffee;
            ChkRemember.IsChecked = Cur.RememberPlacement;
            ChkAltTab.IsChecked = Cur.AltTabAllDesktops;
            ChkPersistPreviews.IsChecked = Cur.PersistPreviews;
            ChkAutoSwitch.IsChecked = Cur.AutoSwitchOnForeground;
            ChkTrim.IsChecked = Cur.TrimHiddenMemory;
            ChkSwitcher.IsChecked = Cur.SwitcherHotkeyEnabled;
            ChkStartup.IsChecked = Cur.StartWithWindows;

            SldThickness.Value = Cur.BarThicknessPx;
            TxtThickness.Text = Cur.BarThicknessPx + " px";
            SldIcon.Value = Cur.IconSizePx;
            TxtIcon.Text = Cur.IconSizePx + " px";
            SldTrim.Value = Cur.TrimDelayMs / 1000.0;
            TxtTrim.Text = (Cur.TrimDelayMs / 1000) + " s";

            UpdateThemeButtons();
            UpdateEdgeButtons();
            UpdateAlignButtons();
            _loading = false;
        }

        private void Save() { App.Settings.Save(); }

        // ---- theme ----------------------------------------------------------
        private void OnThemeSystem(object sender, RoutedEventArgs e) { SetTheme("system"); }
        private void OnThemeDark(object sender, RoutedEventArgs e) { SetTheme("dark"); }
        private void OnThemeLight(object sender, RoutedEventArgs e) { SetTheme("light"); }

        private void SetTheme(string t) { Cur.Theme = t; Save(); UpdateThemeButtons(); }

        private void UpdateThemeButtons()
        {
            Select(BtnThemeSystem, Cur.Theme == "system");
            Select(BtnThemeDark, Cur.Theme == "dark");
            Select(BtnThemeLight, Cur.Theme == "light");
        }

        // ---- edge -----------------------------------------------------------
        private void OnEdgeLeft(object sender, RoutedEventArgs e) { SetEdge("left"); }
        private void OnEdgeRight(object sender, RoutedEventArgs e) { SetEdge("right"); }
        private void OnEdgeTop(object sender, RoutedEventArgs e) { SetEdge("top"); }
        private void OnEdgeBottom(object sender, RoutedEventArgs e) { SetEdge("bottom"); }
        private void OnEdgeFloat(object sender, RoutedEventArgs e) { SetEdge("float"); }

        private void SetEdge(string edge) { Cur.DockEdge = edge; Save(); UpdateEdgeButtons(); }

        private void UpdateEdgeButtons()
        {
            Select(BtnEdgeLeft, Cur.DockEdge == "left");
            Select(BtnEdgeRight, Cur.DockEdge == "right");
            Select(BtnEdgeTop, Cur.DockEdge == "top");
            Select(BtnEdgeBottom, Cur.DockEdge == "bottom");
            Select(BtnEdgeFloat, Cur.DockEdge == "float");
        }

        private void Select(Button b, bool selected)
        {
            if (selected)
            {
                b.Background = TryFindResource("AccentBrush") as Brush ?? Brushes.SteelBlue;
                b.Foreground = Brushes.White;
            }
            else
            {
                b.Background = Brushes.Transparent;
                b.Foreground = TryFindResource("TextPrimary") as Brush ?? Brushes.Black;
            }
        }

        // ---- title alignment ------------------------------------------------
        private void OnAlignLeft(object sender, RoutedEventArgs e) { SetAlign("left"); }
        private void OnAlignCenter(object sender, RoutedEventArgs e) { SetAlign("center"); }
        private void OnAlignRight(object sender, RoutedEventArgs e) { SetAlign("right"); }

        private void SetAlign(string a) { Cur.TitleAlignment = a; Save(); UpdateAlignButtons(); }

        private void UpdateAlignButtons()
        {
            Select(BtnAlignLeft, Cur.TitleAlignment == "left");
            Select(BtnAlignCenter, Cur.TitleAlignment == "center");
            Select(BtnAlignRight, Cur.TitleAlignment == "right");
        }

        // ---- sliders --------------------------------------------------------
        private void OnThicknessChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_loading) return;
            int v = (int)e.NewValue;
            TxtThickness.Text = v + " px";
            App.Settings.NotifyThicknessLive(v); // live reposition without disk writes
        }

        private void OnIconChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_loading) return;
            int v = (int)e.NewValue;
            TxtIcon.Text = v + " px";
            Cur.IconSizePx = v;
        }

        private void OnTrimChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_loading) return;
            int secs = (int)e.NewValue;
            TxtTrim.Text = secs + " s";
            Cur.TrimDelayMs = secs * 1000;
        }

        private void OnSliderCommit(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            if (_loading) return;
            Save();
        }

        // ---- backup / restore ----------------------------------------------
        private void OnBackup(object sender, RoutedEventArgs e)
        {
            var dlg = new SaveFileDialog
            {
                FileName = "MultiDesk-settings.json",
                Filter = "JSON file (*.json)|*.json|All files (*.*)|*.*"
            };
            if (dlg.ShowDialog() == true)
            {
                try { App.Settings.ExportTo(dlg.FileName); BackupStatus.Text = "Backed up to " + dlg.FileName; }
                catch (Exception ex) { BackupStatus.Text = "Backup failed: " + ex.Message; }
            }
        }

        private void OnRestore(object sender, RoutedEventArgs e)
        {
            var dlg = new OpenFileDialog { Filter = "JSON file (*.json)|*.json|All files (*.*)|*.*" };
            if (dlg.ShowDialog() == true)
            {
                if (App.Settings.ImportFrom(dlg.FileName))
                {
                    App.Desktops.ReloadFromSettings();
                    App.AltTab.Apply();
                    LoadValues();
                    BackupStatus.Text = "Settings restored.";
                }
                else BackupStatus.Text = "Restore failed: the file could not be read.";
            }
        }

        private void OnCoffee(object sender, RoutedEventArgs e) { App.OpenSupportLink(); }

        // Fixed, gentle wheel step of 24 px per notch. Default handling multiplies the OS wheel-lines
        // setting into jumps that overshoot whole sections in a panel this dense.
        private void OnSettingsWheel(object sender, System.Windows.Input.MouseWheelEventArgs e)
        {
            Scroller.ScrollToVerticalOffset(Scroller.VerticalOffset - e.Delta / 120.0 * 24.0);
            e.Handled = true;
        }

        // ---- startup entry --------------------------------------------------
        private void SetStartup(bool on)
        {
            try
            {
                using (var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", true))
                {
                    if (key == null) return;
                    if (on)
                    {
                        var exe = System.Reflection.Assembly.GetExecutingAssembly().Location;
                        key.SetValue("MultiDesk", "\"" + exe + "\"");
                    }
                    else key.DeleteValue("MultiDesk", false);
                }
            }
            catch (Exception ex) { Log.Error("startup registry", ex); }
        }
    }
}
