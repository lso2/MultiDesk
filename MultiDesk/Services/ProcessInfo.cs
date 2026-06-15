using System;
using System.Collections.Generic;
using System.IO;
using System.Management;

namespace MultiDesk.Services
{
    /// <summary>
    /// Resolves a Chromium browser window's profile from its process command line, so different Brave
    /// or Chrome profiles can be pinned independently rather than pinning every window of the app. The
    /// command line is read by WMI only for known browsers and cached per process, to keep it cheap.
    /// </summary>
    internal static class ProcessInfo
    {
        private static readonly HashSet<string> Browsers = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        { "brave.exe", "chrome.exe", "msedge.exe", "vivaldi.exe", "opera.exe", "chromium.exe", "thorium.exe" };

        private static readonly Dictionary<uint, string> Cache = new Dictionary<uint, string>();

        /// <summary>The --profile-directory for a browser process (for example "Default" or "Profile 1"),
        /// or null for non-browser apps so they pin by executable as before.</summary>
        public static string ProfileArg(uint pid, string exePath)
        {
            if (pid == 0 || string.IsNullOrEmpty(exePath)) return null;
            string exe;
            try { exe = Path.GetFileName(exePath); } catch { return null; }
            if (!Browsers.Contains(exe)) return null;

            string val;
            if (Cache.TryGetValue(pid, out val)) return val;

            val = null;
            try
            {
                using (var searcher = new ManagementObjectSearcher("SELECT CommandLine FROM Win32_Process WHERE ProcessId=" + pid))
                using (var results = searcher.Get())
                {
                    foreach (ManagementObject mo in results)
                    {
                        val = ExtractProfile(mo["CommandLine"] as string);
                        break;
                    }
                }
            }
            catch (Exception ex) { Log.Info("command line read failed: " + ex.Message); val = null; }

            Cache[pid] = val;
            return val;
        }

        private static string ExtractProfile(string cmd)
        {
            if (string.IsNullOrEmpty(cmd)) return "Default";
            const string key = "--profile-directory=";
            int i = cmd.IndexOf(key, StringComparison.OrdinalIgnoreCase);
            if (i < 0) return "Default"; // a browser with no explicit profile arg is the Default profile
            i += key.Length;
            if (i >= cmd.Length) return "Default";
            if (cmd[i] == '"')
            {
                int e = cmd.IndexOf('"', i + 1);
                return e > i ? cmd.Substring(i + 1, e - i - 1) : cmd.Substring(i + 1);
            }
            int sp = cmd.IndexOf(' ', i);
            return sp > i ? cmd.Substring(i, sp - i) : cmd.Substring(i);
        }
    }
}
