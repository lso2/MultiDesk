using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Threading;

namespace MultiDesk.Services
{
    /// <summary>
    /// The update check. Defines UpdateService, which asks the GitHub releases API for the newest
    /// published release and compares it against the running assembly's version, and GithubRelease,
    /// the shape of that answer. The request runs off the interface thread and the result is raised
    /// back on it. A repository with no published release answers 404, which means no update.
    /// Doc: docs/notes/MultiDesk/Services/UpdateService.cs.md
    /// </summary>
    public sealed class UpdateService : IDisposable
    {
        /// <summary>The page the update button opens.</summary>
        public const string ReleasesPage = "https://github.com/lso2/MultiDesk/releases";

        private const string LatestApi = "https://api.github.com/repos/lso2/MultiDesk/releases/latest";
        private const int StartupDelayMs = 12000;
        private const int DailyMs = 24 * 60 * 60 * 1000;
        // Settings are saved often, the placement timer alone writing every 45 seconds, and each save
        // raises Changed. This floor is what keeps the check a daily one under all of them.
        private const int MinimumGapMs = 60 * 60 * 1000;

        private readonly SettingsStore _settings;
        private DispatcherTimer _first;
        private DispatcherTimer _daily;
        private bool _running;
        private bool _autoWanted;
        private int _lastCheckTick;
        private bool _everChecked;

        public event EventHandler Changed;

        /// <summary>True once a published release carries a higher version than the running one.</summary>
        public bool UpdateAvailable { get; private set; }

        /// <summary>The newest published version, or null while none is known.</summary>
        public string LatestVersion { get; private set; }

        public UpdateService(SettingsStore settings) { _settings = settings; }

        public void Start()
        {
            DiagChannels.Arm(DiagChannels.Upd);
            Diag.Write(DiagChannels.Upd, "running-version", AppVersion.Display
                + " assembly=" + AppVersion.Current
                + " auto-check=" + (_settings.Current.AutoCheckUpdates ? "on" : "off"));

            _first = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(StartupDelayMs) };
            _first.Tick += (s, e) => { _first.Stop(); Scheduled(); };
            _first.Start();

            _daily = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(DailyMs) };
            _daily.Tick += (s, e) => Scheduled();
            _daily.Start();

            _autoWanted = _settings.Current.AutoCheckUpdates;
            _settings.Changed += OnSettingsChanged;
        }

        // Every save raises Changed, so only an actual move of this one setting is acted on. Turning
        // the check on asks for an answer now. Turning it off clears the button, since an offer the
        // user switched off may not keep sitting on the bar.
        private void OnSettingsChanged(object sender, EventArgs e)
        {
            bool wanted = _settings.Current.AutoCheckUpdates;
            if (wanted == _autoWanted) return;
            _autoWanted = wanted;
            if (wanted) { Scheduled(); return; }
            if (!UpdateAvailable) return;
            UpdateAvailable = false;
            Raise();
        }

        // The scheduled checks are what the automatic setting governs, and they never run closer
        // together than the floor above. Check now is a request the user made by hand: it runs
        // whatever the setting says and ignores the floor.
        private void Scheduled()
        {
            if (!_settings.Current.AutoCheckUpdates) return;
            if (_everChecked && Environment.TickCount - _lastCheckTick < MinimumGapMs)
            {
                Diag.Detail(DiagChannels.Upd, "check-skipped", "last check was "
                    + DiagFormat.Ms(_lastCheckTick) + " ago, under the one-hour floor");
                return;
            }
            CheckNow();
        }

        /// <summary>Ask now, off the interface thread.</summary>
        public void CheckNow()
        {
            if (_running) return;
            _running = true;
            _everChecked = true;
            _lastCheckTick = Environment.TickCount;
            var dispatcher = Dispatcher.CurrentDispatcher;
            Task.Factory.StartNew(() =>
            {
                string tag = null;
                string failure = null;
                try { tag = FetchLatestTag(); }
                catch (WebException ex) { failure = Describe(ex); }
                catch (Exception ex) { failure = ex.GetType().Name + ": " + ex.Message; }
                var carried = tag;
                var why = failure;
                try { dispatcher.BeginInvoke(new Action(() => Apply(carried, why))); }
                catch { _running = false; }
            });
        }

        private void Apply(string tag, string failure)
        {
            _running = false;
            if (failure != null)
            {
                Diag.Write(DiagChannels.Upd, "check-failed", failure);
                return;
            }
            var latest = Parse(tag);
            if (latest == null)
            {
                Diag.Write(DiagChannels.Upd, "no-release", "tag=" + (tag ?? "none"));
                return;
            }
            LatestVersion = Text(latest);
            bool newer = latest > AppVersion.Current;
            Diag.Write(DiagChannels.Upd, "checked", "running=" + AppVersion.Display
                + " latest=" + LatestVersion + " newer=" + (newer ? "yes" : "no"));
            if (newer == UpdateAvailable) return;
            UpdateAvailable = newer;
            Raise();
        }

        private void Raise()
        {
            var h = Changed;
            if (h != null) h(this, EventArgs.Empty);
        }

        private static string FetchLatestTag()
        {
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
            var req = (HttpWebRequest)WebRequest.Create(LatestApi);
            req.Method = "GET";
            req.Timeout = 15000;
            req.ReadWriteTimeout = 15000;
            req.UserAgent = "MultiDesk/" + AppVersion.Display;       // the API rejects a request without one
            req.Accept = "application/vnd.github+json";
            using (var res = (HttpWebResponse)req.GetResponse())
            using (var body = res.GetResponseStream())
            {
                if (body == null) return null;
                using (var buffer = new MemoryStream())
                {
                    body.CopyTo(buffer);
                    buffer.Position = 0;
                    var ser = new DataContractJsonSerializer(typeof(GithubRelease));
                    var release = ser.ReadObject(buffer) as GithubRelease;
                    return release != null ? release.Tag : null;
                }
            }
        }

        // A 404 from this endpoint means the repository has published no release yet, which is an
        // answer rather than an error.
        private static string Describe(WebException ex)
        {
            var http = ex.Response as HttpWebResponse;
            if (http != null && http.StatusCode == HttpStatusCode.NotFound) return null;
            return http != null
                ? "http " + (int)http.StatusCode + " " + http.StatusCode
                : ex.Status + ": " + ex.Message;
        }

        // Release tags on this project are written in several shapes: 2.9.17, v2.9.17, and the
        // installer's own MultiDeskSetup-2.9.11-release. The version is taken as the first run of
        // digits that carries a dot, falling back to the last run of digits when none does, which
        // keeps a name containing its own number (MultiDesk2-1.0.3) from being read as the version.
        private static Version Parse(string tag)
        {
            if (string.IsNullOrWhiteSpace(tag)) return null;
            var runs = new List<string>();
            var run = new StringBuilder();
            foreach (var c in tag.Trim())
            {
                bool part = char.IsDigit(c) || (c == '.' && run.Length > 0 && run[run.Length - 1] != '.');
                if (part) { run.Append(c); continue; }
                if (run.Length > 0) { runs.Add(run.ToString().Trim('.')); run.Clear(); }
            }
            if (run.Length > 0) runs.Add(run.ToString().Trim('.'));
            if (runs.Count == 0) return null;

            string text = null;
            foreach (var r in runs) if (r.IndexOf('.') >= 0) { text = r; break; }
            if (text == null) text = runs[runs.Count - 1];
            if (text.Length == 0) return null;
            if (text.IndexOf('.') < 0) text += ".0";

            // Version accepts at most four parts, and a tag carrying more is not a version number.
            var parts = text.Split('.');
            if (parts.Length > 4) text = string.Join(".", parts, 0, 4);
            Version v;
            return Version.TryParse(text, out v) ? v : null;
        }

        private static string Text(Version v)
        {
            return v.Major.ToString(CultureInfo.InvariantCulture) + "."
                + v.Minor.ToString(CultureInfo.InvariantCulture) + "."
                + Math.Max(0, v.Build).ToString(CultureInfo.InvariantCulture);
        }

        public void Dispose()
        {
            try { _settings.Changed -= OnSettingsChanged; } catch { }
            if (_first != null) { _first.Stop(); _first = null; }
            if (_daily != null) { _daily.Stop(); _daily = null; }
        }
    }

    /// <summary>The one field of the releases API answer this app reads.</summary>
    [DataContract]
    public sealed class GithubRelease
    {
        [DataMember(Name = "tag_name")]
        public string Tag { get; set; }
    }
}
