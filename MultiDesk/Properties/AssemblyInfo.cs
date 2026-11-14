using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;

[assembly: AssemblyTitle("MultiDesk")]
[assembly: AssemblyDescription("Virtual desktop sidebar and window manager for Windows")]
[assembly: AssemblyConfiguration("")]
[assembly: AssemblyCompany("PlexPixel")]
[assembly: AssemblyProduct("MultiDesk")]
[assembly: AssemblyCopyright("Copyright (C) 2026 PlexPixel")]
[assembly: AssemblyTrademark("")]
[assembly: AssemblyCulture("")]
[assembly: ComVisible(false)]

// WPF theme/location info. Generic + source assembly fallback for ResourceDictionary lookups.
[assembly: ThemeInfo(
    ResourceDictionaryLocation.None,
    ResourceDictionaryLocation.SourceAssembly)]

// The one place the version is written. Services\AppVersion.cs reads it off the running assembly,
// the settings window and the trace header print what it reads, the update check compares against it,
// and Installer.iss takes it from the built executable rather than carrying its own copy.
[assembly: AssemblyVersion("2.11.0.0")]
[assembly: AssemblyFileVersion("2.11.0.0")]
