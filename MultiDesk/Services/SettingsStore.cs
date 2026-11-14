using System;
using System.IO;
using System.Runtime.Serialization.Json;
using System.Text;
using System.Threading;
using System.Windows.Threading;
using MultiDesk.Models;

namespace MultiDesk.Services
{
    /// <summary>
    /// Settings storage. Defines SettingsStore, which loads, migrates and saves settings.json. With
    /// every write going through a temp file and an atomic replace, a crash mid-write never leaves a
    /// truncated file. A write another process blocks is retried until it lands. Save raises Changed
    /// for the live UI, and ThicknessChangedLive fires while the bar edge is dragged.
    /// Doc: docs/notes/MultiDesk/Services/SettingsStore.cs.md
    /// </summary>
    public sealed class SettingsStore
    {
        public event EventHandler Changed;
        public event EventHandler ThicknessChangedLive;

        public MultiDeskSettings Current { get; private set; }

        private readonly string _dir;
        private readonly string _file;

        // Retries a write that failed, until one lands. Every retry writes the settings as they are
        // at that moment.
        private DispatcherTimer _retry;
        private int _failedWrites;

        public SettingsStore()
        {
            _dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MultiDesk");
            _file = Path.Combine(_dir, "settings.json");
        }

        public void Load()
        {
            try
            {
                Directory.CreateDirectory(_dir);
                if (File.Exists(_file))
                {
                    using (var fs = File.OpenRead(_file))
                    {
                        var ser = new DataContractJsonSerializer(typeof(MultiDeskSettings));
                        Current = (MultiDeskSettings)ser.ReadObject(fs);
                    }
                }
                if (Current == null) Current = MultiDeskSettings.CreateDefault();
                Current.Normalize();
            }
            catch (Exception ex)
            {
                Log.Error("settings load failed, using defaults", ex);
                Current = MultiDeskSettings.CreateDefault();
                Current.Normalize();
            }
            bool migrated = false;
            if (Current.SchemaVersion < 3)
            {
                // Nudge configs created with old defaults to the slim layout, once, without
                // touching a dock edge the user may have chosen.
                if (Current.IconSizePx == 32 || Current.IconSizePx == 28) Current.IconSizePx = 24;
                if (Current.BarThicknessPx == 92 || Current.BarThicknessPx == 96 || Current.BarThicknessPx == 100) Current.BarThicknessPx = 110;
                migrated = true;
            }
            if (Current.SchemaVersion < 4)
            {
                if (Current.DesktopCount == 4) Current.DesktopCount = 6; // new default count
                migrated = true;
            }
            if (Current.SchemaVersion < 5)
            {
                Current.AltTabAllDesktops = true; // new default: Alt+Tab spans all desktops
                migrated = true;
            }
            if (Current.SchemaVersion < 6)
            {
                Current.PersistPreviews = true; // new default: previews are kept
                migrated = true;
            }
            if (Current.SchemaVersion < 7)
            {
                // Picks through Alt+Tab, the taskbar, the tray and Start switch desktops regardless of
                // this setting since schema 7, which leaves it deciding only windows grabbing focus.
                Current.AutoSwitchOnForeground = false;
                migrated = true;
            }
            if (migrated) Current.SchemaVersion = 7;
            if (!File.Exists(_file) || migrated) Save();
        }

        /// <summary>Write the settings and raise Changed, for a setting the UI shows.</summary>
        public void Save()
        {
            Persist();
            try { Changed?.Invoke(this, EventArgs.Empty); }
            catch (Exception ex) { Log.Info("settings Changed handler threw " + ex.GetType().Name + ": " + ex.Message); }
        }

        /// <summary>Write the settings without raising Changed, for data no part of the UI shows,
        /// such as remembered window placements.</summary>
        public void Persist()
        {
            Current.Normalize();
            if (TryWrite()) return;
            if (_retry == null)
            {
                _retry = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
                _retry.Tick += (s, e) => { if (TryWrite()) _retry.Stop(); };
            }
            if (!_retry.IsEnabled) _retry.Start();
        }

        // One write of settings.json. The replace is tried 3 times, 30 ms apart, before the write counts
        // as failed. A failed streak is logged at its first failure and at the write that ends it.
        private bool TryWrite()
        {
            try
            {
                Directory.CreateDirectory(_dir);
                var tmp = _file + ".tmp";
                File.WriteAllBytes(tmp, Serialize());
                for (int attempt = 1; ; attempt++)
                {
                    try
                    {
                        if (File.Exists(_file)) File.Replace(tmp, _file, null);
                        else File.Move(tmp, _file);
                        break;
                    }
                    catch (IOException) when (attempt < 3) { Thread.Sleep(30); }
                }
                if (_failedWrites > 0)
                    Log.Info("settings saved after " + _failedWrites + " failed writes");
                _failedWrites = 0;
                return true;
            }
            catch (Exception ex)
            {
                if (_failedWrites == 0)
                    Log.Info("settings save failed, retrying every 2s :: " + ex.GetType().Name + ": " + ex.Message);
                _failedWrites++;
                return false;
            }
        }

        private byte[] Serialize()
        {
            using (var ms = new MemoryStream())
            {
                var ser = new DataContractJsonSerializer(typeof(MultiDeskSettings));
                ser.WriteObject(ms, Current);
                return Indent(ms.ToArray());
            }
        }

        /// <summary>Update the bar thickness live during a drag without writing to disk on each pixel.
        /// The final value is persisted with a single Save on drag release.</summary>
        public void NotifyThicknessLive(int px)
        {
            if (px < 56) px = 56;
            if (px > 400) px = 400;
            Current.BarThicknessPx = px;
            ThicknessChangedLive?.Invoke(this, EventArgs.Empty);
        }

        /// <summary>Write the current settings to a chosen file as readable JSON.</summary>
        public void ExportTo(string path)
        {
            Current.Normalize();
            File.WriteAllBytes(path, Serialize());
        }

        /// <summary>Load settings from a chosen JSON file and make them current.</summary>
        public bool ImportFrom(string path)
        {
            try
            {
                MultiDeskSettings loaded;
                using (var fs = File.OpenRead(path))
                {
                    var ser = new DataContractJsonSerializer(typeof(MultiDeskSettings));
                    loaded = (MultiDeskSettings)ser.ReadObject(fs);
                }
                if (loaded == null) return false;
                Current = loaded;
                Current.Normalize();
                Save();
                return true;
            }
            catch (Exception ex) { Log.Error("settings import failed", ex); return false; }
        }

        // DataContractJsonSerializer emits compact JSON. A light indent keeps the file hand-editable.
        private static byte[] Indent(byte[] compact)
        {
            try
            {
                var s = Encoding.UTF8.GetString(compact);
                var sb = new StringBuilder();
                int depth = 0;
                bool inStr = false;
                for (int i = 0; i < s.Length; i++)
                {
                    char c = s[i];
                    if (c == '"' && (i == 0 || s[i - 1] != '\\')) inStr = !inStr;
                    if (inStr) { sb.Append(c); continue; }
                    switch (c)
                    {
                        case '{':
                        case '[':
                            sb.Append(c).Append('\n').Append(new string(' ', ++depth * 2));
                            break;
                        case '}':
                        case ']':
                            sb.Append('\n').Append(new string(' ', --depth * 2)).Append(c);
                            break;
                        case ',':
                            sb.Append(c).Append('\n').Append(new string(' ', depth * 2));
                            break;
                        case ':':
                            sb.Append(": ");
                            break;
                        default:
                            sb.Append(c);
                            break;
                    }
                }
                return Encoding.UTF8.GetBytes(sb.ToString());
            }
            catch
            {
                return compact;
            }
        }
    }
}
