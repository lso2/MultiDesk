using System;
using System.Globalization;
using System.Reflection;

namespace MultiDesk.Services
{
    /// <summary>
    /// The running version. Defines AppVersion, which reads the number off the assembly that is
    /// executing, so the settings window, the trace header and the update check all report the same
    /// number as the file on disk carries. Properties\AssemblyInfo.cs is the one place it is written.
    /// Doc: docs/notes/MultiDesk/Services/AppVersion.cs.md
    /// </summary>
    internal static class AppVersion
    {
        private static readonly Version Parsed = Read();

        /// <summary>The version as a comparable value, for the update check.</summary>
        public static Version Current { get { return Parsed; } }

        /// <summary>The version as people write it: three parts, with the build number dropped when
        /// the release carries none.</summary>
        public static string Display
        {
            get
            {
                if (Parsed.Build > 0 || Parsed.Minor > 0 || Parsed.Major > 0)
                    return Parsed.Major.ToString(CultureInfo.InvariantCulture) + "."
                        + Parsed.Minor.ToString(CultureInfo.InvariantCulture) + "."
                        + Math.Max(0, Parsed.Build).ToString(CultureInfo.InvariantCulture);
                return "0.0.0";
            }
        }

        private static Version Read()
        {
            try
            {
                var name = Assembly.GetExecutingAssembly().GetName();
                if (name != null && name.Version != null) return name.Version;
            }
            catch (Exception ex) { Log.Error("read assembly version", ex); }
            return new Version(0, 0, 0, 0);
        }
    }
}
