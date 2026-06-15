using System;
using System.Windows;
using System.Windows.Media;
using Microsoft.Win32;

namespace MultiDesk.Services
{
    /// <summary>
    /// Owns the active theme. The theme setting can force dark or light, or follow the system, in which
    /// case AppsUseLightTheme and AccentColor are read from the registry. The matching dictionary is
    /// loaded and its accent brushes are overridden from the live system accent. SystemEvents and the
    /// settings Changed event both trigger a restyle with no restart.
    /// </summary>
    public sealed class ThemeService : IDisposable
    {
        public event Action ThemeChanged;

        public bool IsDark { get; private set; }
        public Color Accent { get; private set; } = Color.FromArgb(0xFF, 0x00, 0x78, 0xD4);

        private const string PersonalizeKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Themes\Personalize";
        private ResourceDictionary _current;
        private SettingsStore _settings;
        private bool _registryDark;

        public void Start(SettingsStore settings)
        {
            _settings = settings;
            ReadRegistry();
            SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
            if (_settings != null) _settings.Changed += OnSettingsChanged;
        }

        public void Apply()
        {
            var app = Application.Current;
            if (app == null) return;

            IsDark = ComputeDark();

            var uri = IsDark
                ? new Uri("/MultiDesk;component/Themes/Theme.Dark.xaml", UriKind.Relative)
                : new Uri("/MultiDesk;component/Themes/Theme.Light.xaml", UriKind.Relative);

            ResourceDictionary dict;
            try { dict = (ResourceDictionary)Application.LoadComponent(uri); }
            catch (Exception ex) { Log.Error("theme dictionary load failed", ex); return; }

            var accentBrush = new SolidColorBrush(Accent); accentBrush.Freeze();
            var accentSoft = new SolidColorBrush(WithAlpha(Accent, 0x40)); accentSoft.Freeze();
            var accentHover = new SolidColorBrush(WithAlpha(Accent, 0x26)); accentHover.Freeze();
            dict["AccentBrush"] = accentBrush;
            dict["ItemActiveBackground"] = accentSoft;
            dict["ItemHoverBackground"] = accentHover;

            var merged = app.Resources.MergedDictionaries;
            if (_current != null) merged.Remove(_current);
            merged.Add(dict);
            _current = dict;

            ThemeChanged?.Invoke();
        }

        /// <summary>Kept for call-site clarity. HwndSource-rooted windows resolve through Application
        /// resources, so no per-window attach is needed.</summary>
        public void AttachWindow(Window window) { }

        private bool ComputeDark()
        {
            var f = _settings != null && _settings.Current != null ? _settings.Current.Theme : "system";
            if (f == "dark") return true;
            if (f == "light") return false;
            return _registryDark;
        }

        private void ReadRegistry()
        {
            try
            {
                using (var key = Registry.CurrentUser.OpenSubKey(PersonalizeKey))
                {
                    if (key != null)
                    {
                        var light = key.GetValue("AppsUseLightTheme");
                        _registryDark = light is int && (int)light == 0;
                        var accent = key.GetValue("AccentColor");
                        if (accent is int) Accent = AbgrToColor(unchecked((uint)(int)accent));
                    }
                    else _registryDark = true;
                }
            }
            catch (Exception ex)
            {
                Log.Error("theme registry read failed", ex);
                _registryDark = true;
            }
        }

        private void OnSettingsChanged(object sender, EventArgs e)
        {
            var app = Application.Current;
            if (app == null) return;
            app.Dispatcher.BeginInvoke(new Action(() => { ReadRegistry(); Apply(); }));
        }

        private void OnUserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
        {
            if (e.Category != UserPreferenceCategory.General &&
                e.Category != UserPreferenceCategory.Color &&
                e.Category != UserPreferenceCategory.VisualStyle)
                return;

            var app = Application.Current;
            if (app == null) return;
            app.Dispatcher.BeginInvoke(new Action(() => { ReadRegistry(); Apply(); }));
        }

        // Registry AccentColor is 0xAABBGGRR; WPF wants ARGB.
        private static Color AbgrToColor(uint v)
        {
            byte a = (byte)((v >> 24) & 0xFF);
            byte b = (byte)((v >> 16) & 0xFF);
            byte g = (byte)((v >> 8) & 0xFF);
            byte r = (byte)(v & 0xFF);
            if (a == 0) a = 0xFF;
            return Color.FromArgb(a, r, g, b);
        }

        private static Color WithAlpha(Color c, byte a) { return Color.FromArgb(a, c.R, c.G, c.B); }

        public void Dispose()
        {
            try { SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged; } catch { }
            try { if (_settings != null) _settings.Changed -= OnSettingsChanged; } catch { }
        }
    }
}
