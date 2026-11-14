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
        public static UpdateService Updates { get; private set; }

        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);
            Log.Init();
            Log.Info("MultiDesk starting.");

            // A bug anywhere in the app used to be able to take the whole process down with it, which
            // for a window manager means every window it had hidden for another desktop is stranded
            // with nothing left running to ever show it again. These two handlers are the backstop: a
            // fault on the interface thread is logged and swallowed so the process keeps running instead
            // of dying, and a fault on any other thread is at least logged before the runtime's own
            // handling of it proceeds, so what happened is never a mystery on the next launch.
            DispatcherUnhandledException += OnDispatcherUnhandledException;
            AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandledException;

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

        private void OnDispatcherUnhandledException(object sender, System.Windows.Threading.DispatcherUnhandledExceptionEventArgs e)
        {
            Log.Error("Unhandled interface-thread exception, MultiDesk kept running", e.Exception);
            e.Handled = true;
        }

        private void OnDomainUnhandledException(object sender, UnhandledExceptionEventArgs e)
        {
            Log.Error("Unhandled exception outside the interface thread", e.ExceptionObject as Exception);
        }

        private void StartApp()
        {
            Settings = new SettingsStore();
            Settings.Load();

            // The trace opens before any service that writes to it exists, so nothing a service does
            // on the way up happens off the record.
            Diag.Init(Settings.Current.Diagnostics, Settings.Current.DiagnosticsVerbose);
            DiagSession.Open(Settings.Current);
            DiagNav.Arm();
            DiagPark.Arm();
            DiagOrder.Arm();
            DiagPane.Arm();

            Theme = new ThemeService();
            Theme.Start(Settings);
            Theme.Apply();

            Desktops = new DesktopManager(Settings);
            Desktops.Initialize();

            Tracker = new WindowTracker(Desktops);
            Tracker.Start();

            // Before the bar, which subscribes to it as it is built.
            Updates = new UpdateService(Settings);
            Updates.Start();

            CreateTrayIcon();

            CreateBar();

            // First population once the bar handle exists, so the dock can place it.
            Desktops.RefreshAll();

            // Every watcher has had its chance to arm by now, so a channel still dark is a watcher
            // that failed to start rather than one with nothing to say yet.
            DiagChannels.BootCheck();

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
#if DEBUG
            // The real lock problem: ShutdownMode is OnExplicitShutdown, so in every build closing the
            // bar (the X button, Alt+F4, or Visual Studio's Stop) only hides it and the process keeps
            // running in the tray, exactly like clicking "Hide pane" would. That is correct for the
            // installed app, which is meant to stay resident, but during development it means the debug
            // exe is still running and holding its own file open, so the next build fails to overwrite
            // it, no matter how the window was closed. A Debug build has no reason to stay resident
            // once its only window is gone, so here, and only in Debug, closing the bar ends the process
            // the same way an ordinary window's app does, which is what releases the exe. Release is
            // untouched: the installed, always-on copy still only exits from "Exit MultiDesk".
            if (Application.Current != null && !Application.Current.Dispatcher.HasShutdownStarted) Shutdown();
#endif
        }

        private void ShowSettings()
        {
            SettingsWindow.ShowSingleton();
        }

        public static void OpenSupportLink()
        {
            OpenLink("https://plexpixel.com/donate", "support link");
        }

        /// <summary>The project page, opened by the update button on the bar.</summary>
        public static void OpenProjectLink()
        {
            OpenLink(UpdateService.ReleasesPage, "project page");
        }

        /// <summary>The folder the navigation trace is written to, opened from settings.</summary>
        public static void OpenDiagnosticFolder()
        {
            var folder = Diag.Folder;
            if (string.IsNullOrEmpty(folder)) return;
            OpenLink(folder, "diagnostic log folder");
        }

        private static void OpenLink(string target, string what)
        {
            try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(target) { UseShellExecute = true }); }
            catch (Exception ex) { Log.Error(what, ex); }
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
            // Remember where each window sat so it can be restored on the next launch.
            try { Desktops?.SavePlacements(); } catch (Exception ex) { Log.Error("save placements", ex); }
            // Safety: never leave a managed window hidden on a desktop the user cannot reach.
            try { Desktops?.ShowAllWindows(); } catch (Exception ex) { Log.Error("show all on exit", ex); }
            try { _bar?.CloseBar(); } catch (Exception ex) { Log.Error("bar close", ex); }
            try { Theme?.Dispose(); } catch (Exception ex) { Log.Error("theme dispose", ex); }
            try { Updates?.Dispose(); } catch (Exception ex) { Log.Error("update service dispose", ex); }
            try { DiagSession.Close(); } catch (Exception ex) { Log.Error("diagnostic session close", ex); }

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
