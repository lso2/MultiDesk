using System;
using System.Threading;
using System.Windows;
using WinForms = System.Windows.Forms;
using Drawing = System.Drawing;
using MultiDesk.Services;
using MultiDesk.UI;

namespace MultiDesk
{
    /// <summary>
    /// Application entry point and process-wide service host. MultiDesk runs as a tray app that owns
    /// a docked sidebar. The sidebar shows one section per managed desktop and the windows on it.
    /// Services are created once at startup and exposed statically so the UI can reach them.
    /// </summary>
    public partial class App : Application
    {
        private const string SingleInstanceMutex = "MultiDesk.SingleInstance.{B7E5D3A1-9C4F-4E2A-8D6B}";

        private Mutex _mutex;
        private bool _ownsMutex;
        private WinForms.NotifyIcon _tray;
        private MainWindow _bar;

        public static SettingsStore Settings { get; private set; }
        public static ThemeService Theme { get; private set; }
        public static DesktopManager Desktops { get; private set; }
        public static WindowTracker Tracker { get; private set; }
        public static AltTabService AltTab { get; private set; }

        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);
            Log.Init();
            Log.Info("MultiDesk starting.");

            try
            {
                if (!ClaimSingleInstance())
                {
                    Log.Info("Another MultiDesk instance is already running. Exiting.");
                    Shutdown();
                    return;
                }
                StartApp();
            }
            catch (Exception ex)
            {
                Log.Error("Fatal during startup", ex);
                Shutdown();
            }
        }

        private void StartApp()
        {
            Settings = new SettingsStore();
            Settings.Load();

            Theme = new ThemeService();
            Theme.Start(Settings);
            Theme.Apply();

            Desktops = new DesktopManager(Settings);
            Desktops.Initialize();

            Tracker = new WindowTracker(Desktops);
            Tracker.Start();

            // Optional "Alt+Tab across desktops" key watcher. Installs only when the setting is on.
            AltTab = new AltTabService(Settings);
            AltTab.Apply();

            CreateTrayIcon();

            CreateBar();

            // First population once the bar handle exists, so the dock can place it.
            Desktops.RefreshAll();

            Log.Info("MultiDesk running.");
        }

        private bool ClaimSingleInstance()
        {
            _mutex = new Mutex(true, SingleInstanceMutex, out _ownsMutex);
            if (_ownsMutex) return true;

            // An upgrade relaunch can race the previous instance that is still shutting down. Wait a few
            // seconds for it to release the lock before giving up, so the new copy is not left invisible.
            try
            {
                if (_mutex.WaitOne(TimeSpan.FromSeconds(3)))
                {
                    _ownsMutex = true;
                    return true;
                }
            }
            catch (AbandonedMutexException)
            {
                _ownsMutex = true; // the previous instance exited without releasing; we own it now
                return true;
            }
            return false;
        }

        private void CreateTrayIcon()
        {
            _tray = new WinForms.NotifyIcon
            {
                Icon = LoadAppIcon(),
                Text = "MultiDesk",
                Visible = true
            };

            var menu = new WinForms.ContextMenuStrip();
            menu.Items.Add("Show / hide bar", null, (s, a) => Dispatcher.Invoke(ToggleBar));
            menu.Items.Add("Add desktop", null, (s, a) => Dispatcher.Invoke(() => Desktops.AddDesktop()));
            menu.Items.Add("Show all windows", null, (s, a) => Dispatcher.Invoke(() => Desktops.ShowAllWindows()));
            menu.Items.Add(new WinForms.ToolStripSeparator());
            menu.Items.Add("Settings", null, (s, a) => Dispatcher.Invoke(ShowSettings));
            menu.Items.Add(new WinForms.ToolStripSeparator());
            menu.Items.Add("Exit", null, (s, a) => Dispatcher.Invoke(Shutdown));
            _tray.ContextMenuStrip = menu;

            _tray.MouseClick += (s, a) =>
            {
                if (a.Button == WinForms.MouseButtons.Left) Dispatcher.Invoke(ToggleBar);
            };
        }

        private void ToggleBar()
        {
            // Closing the bar frees its docked space; reopening recreates it. Tracking Closed and
            // recreating here means reopening from the tray can never hit a closed-window error.
            if (_bar != null && _bar.IsVisible) { try { _bar.CloseBar(); } catch (Exception ex) { Log.Error("hide bar", ex); } }
            else CreateBar();
        }

        private void CreateBar()
        {
            _bar = new MainWindow();
            _bar.Closed += OnBarClosed;
            Theme.AttachWindow(_bar);
            _bar.Show();
        }

        private void OnBarClosed(object sender, EventArgs e)
        {
            if (ReferenceEquals(_bar, sender)) _bar = null;
        }

        private void ShowSettings()
        {
            SettingsWindow.ShowSingleton();
        }

        public static void OpenSupportLink()
        {
            try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("https://plexpixel.com/donate") { UseShellExecute = true }); }
            catch (Exception ex) { Log.Error("support link", ex); }
        }

        private static Drawing.Icon LoadAppIcon()
        {
            try
            {
                var exe = System.Reflection.Assembly.GetExecutingAssembly().Location;
                var ico = Drawing.Icon.ExtractAssociatedIcon(exe);
                if (ico != null) return ico;
            }
            catch (Exception ex) { Log.Error("load tray icon", ex); }
            return Drawing.SystemIcons.Application;
        }

        protected override void OnExit(ExitEventArgs e)
        {
            Log.Info("MultiDesk shutting down.");

            // Stop tracking first so no late event re-hides a window during teardown.
            try { Tracker?.Dispose(); } catch (Exception ex) { Log.Error("tracker dispose", ex); }
            try { AltTab?.Dispose(); } catch (Exception ex) { Log.Error("alt-tab dispose", ex); }
            // Remember where each window sat so it can be restored on the next launch.
            try { Desktops?.SavePlacements(); } catch (Exception ex) { Log.Error("save placements", ex); }
            // Safety: never leave a managed window hidden on a desktop the user cannot reach.
            try { Desktops?.ShowAllWindows(); } catch (Exception ex) { Log.Error("show all on exit", ex); }
            try { _bar?.CloseBar(); } catch (Exception ex) { Log.Error("bar close", ex); }
            try { Theme?.Dispose(); } catch (Exception ex) { Log.Error("theme dispose", ex); }

            if (_tray != null)
            {
                _tray.Visible = false;
                _tray.Dispose();
            }

            if (_mutex != null)
            {
                if (_ownsMutex) { try { _mutex.ReleaseMutex(); } catch { } }
                _mutex.Dispose();
            }

            base.OnExit(e);
        }
    }
}
