using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.Serialization;
using System.Windows.Media;

namespace MultiDesk.Models
{
    /// <summary>Persisted configuration, serialized to %APPDATA%\MultiDesk\settings.json.</summary>
    [DataContract]
    public sealed class MultiDeskSettings
    {
        // "left" | "right" | "top" | "bottom" | "float". Docked right by default.
        [DataMember(Name = "dockEdge", Order = 0)]
        public string DockEdge { get; set; } = "right";

        // Width of the bar when docked vertically, height when docked horizontally.
        [DataMember(Name = "barThicknessPx", Order = 1)]
        public int BarThicknessPx { get; set; } = 110;

        [DataMember(Name = "iconSizePx", Order = 2)]
        public int IconSizePx { get; set; } = 24;

        [DataMember(Name = "showTitles", Order = 3)]
        public bool ShowTitles { get; set; } = true;

        // "system" | "dark" | "light"
        [DataMember(Name = "theme", Order = 4)]
        public string Theme { get; set; } = "system";

        [DataMember(Name = "autoHide", Order = 5)]
        public bool AutoHide { get; set; } = true;

        [DataMember(Name = "trimHiddenMemory", Order = 6)]
        public bool TrimHiddenMemory { get; set; } = true;

        [DataMember(Name = "trimDelayMs", Order = 7)]
        public int TrimDelayMs { get; set; } = 5000;

        [DataMember(Name = "desktopCount", Order = 8)]
        public int DesktopCount { get; set; } = 6;

        [DataMember(Name = "desktopNames", Order = 9, EmitDefaultValue = false)]
        public List<string> DesktopNames { get; set; }

        [DataMember(Name = "startWithWindows", Order = 10)]
        public bool StartWithWindows { get; set; }

        [DataMember(Name = "switcherHotkeyEnabled", Order = 11)]
        public bool SwitcherHotkeyEnabled { get; set; }

        [DataMember(Name = "autoSwitchOnForeground", Order = 12)]
        public bool AutoSwitchOnForeground { get; set; } = true;

        [DataMember(Name = "alwaysOnTop", Order = 19)]
        public bool AlwaysOnTop { get; set; } = true;

        // When docked (not auto-hiding), reserve the screen strip as an AppBar. Off = overlay content.
        [DataMember(Name = "reserveSpace", Order = 20)]
        public bool ReserveSpace { get; set; } = true;

        // "left" | "center" | "right"
        [DataMember(Name = "titleAlignment", Order = 21)]
        public string TitleAlignment { get; set; } = "center";

        [DataMember(Name = "showCoffee", Order = 22)]
        public bool ShowCoffee { get; set; } = true;

        [DataMember(Name = "rememberPlacement", Order = 23)]
        public bool RememberPlacement { get; set; } = true;

        // When on, every desktop's windows appear in the system Alt+Tab (revealed only while you hold
        // Alt+Tab); picking one switches to its desktop. Off: Alt+Tab shows only the current desktop.
        [DataMember(Name = "altTabAllDesktops", Order = 25)]
        public bool AltTabAllDesktops { get; set; } = true;

        // When on, a snapshot of each window is cached so hovering one on another desktop shows its last
        // look instead of just the icon. Off (default) shows the icon and uses less memory.
        [DataMember(Name = "persistPreviews", Order = 26)]
        public bool PersistPreviews { get; set; } = true;

        // Best-effort memory of which desktop each window sat on and its order, matched by executable,
        // browser profile, and title on the next launch (window handles are not stable, so it is heuristic).
        [DataMember(Name = "placements", Order = 24, EmitDefaultValue = false)]
        public List<PlacementEntry> Placements { get; set; }

        [DataMember(Name = "floatLeft", Order = 13, EmitDefaultValue = false)]
        public double FloatLeft { get; set; } = 80;

        [DataMember(Name = "floatTop", Order = 14, EmitDefaultValue = false)]
        public double FloatTop { get; set; } = 80;

        // Bumped when layout defaults change, so an older config migrates to the new look once.
        [DataMember(Name = "schemaVersion", Order = 15)]
        public int SchemaVersion { get; set; } = 6;

        // Rows of icons shown per desktop before that desktop's grid scrolls. Keeps every desktop a
        // fixed, even height instead of expanding to fit its windows.
        [DataMember(Name = "rowsPerDesktop", Order = 16)]
        public int RowsPerDesktop { get; set; } = 3;

        // Optional per-desktop row overrides, aligned with the desktop list. 0 means use the default.
        [DataMember(Name = "desktopRows", Order = 17, EmitDefaultValue = false)]
        public List<int> DesktopRows { get; set; }

        // App-to-desktop rules: a window of this executable opens on its pinned desktop. Persisted, so
        // pinned apps return to their desktop across reboots.
        [DataMember(Name = "appPins", Order = 18, EmitDefaultValue = false)]
        public List<AppPin> AppPins { get; set; }

        // GetUninitializedObject during deserialization skips field initializers, so restore the
        // non-false/non-zero defaults before members load.
        [OnDeserializing]
        private void OnDeserializing(StreamingContext c)
        {
            DockEdge = "right";
            BarThicknessPx = 110;
            IconSizePx = 24;
            ShowTitles = true;
            Theme = "system";
            AutoHide = true;
            TrimHiddenMemory = true;
            TrimDelayMs = 5000;
            DesktopCount = 6;
            RowsPerDesktop = 3;
            AutoSwitchOnForeground = true;
            AlwaysOnTop = true;
            ReserveSpace = true;
            TitleAlignment = "center";
            ShowCoffee = true;
            RememberPlacement = true;
            AltTabAllDesktops = true;
            PersistPreviews = true;
            FloatLeft = 80;
            FloatTop = 80;
            SchemaVersion = 0; // old files lack this, so they read as 0 and migrate
        }

        public static MultiDeskSettings CreateDefault() { return new MultiDeskSettings(); }

        public void Normalize()
        {
            var e = (DockEdge ?? "left").Trim().ToLowerInvariant();
            if (e != "left" && e != "right" && e != "top" && e != "bottom" && e != "float") e = "left";
            DockEdge = e;
            if (BarThicknessPx < 56) BarThicknessPx = 56;
            if (BarThicknessPx > 400) BarThicknessPx = 400;
            if (IconSizePx < 16) IconSizePx = 16;
            if (IconSizePx > 64) IconSizePx = 64;
            var t = (Theme ?? "system").Trim().ToLowerInvariant();
            if (t != "system" && t != "dark" && t != "light") t = "system";
            Theme = t;
            var ta = (TitleAlignment ?? "center").Trim().ToLowerInvariant();
            if (ta != "left" && ta != "center" && ta != "right") ta = "center";
            TitleAlignment = ta;
            if (DesktopCount < 1) DesktopCount = 1;
            if (DesktopCount > 32) DesktopCount = 32;
            if (RowsPerDesktop < 1) RowsPerDesktop = 1;
            if (RowsPerDesktop > 12) RowsPerDesktop = 12;
            if (TrimDelayMs < 500) TrimDelayMs = 500;
            if (TrimDelayMs > 120000) TrimDelayMs = 120000;
        }

        public bool IsVerticalEdge { get { return DockEdge == "left" || DockEdge == "right" || DockEdge == "float"; } }
    }

    /// <summary>An app-to-desktop rule. Windows of this executable open on the chosen desktop.</summary>
    [DataContract]
    public sealed class AppPin
    {
        [DataMember(Name = "exe", Order = 0)]
        public string Exe { get; set; }   // executable file name, lower case

        [DataMember(Name = "profile", Order = 1, EmitDefaultValue = false)]
        public string Profile { get; set; }   // browser profile, null for ordinary apps

        [DataMember(Name = "desktop", Order = 2)]
        public int DesktopIndex { get; set; }
    }

    /// <summary>Remembered placement of one window, matched on the next launch.</summary>
    [DataContract]
    public sealed class PlacementEntry
    {
        [DataMember(Name = "exe", Order = 0)] public string Exe { get; set; }
        [DataMember(Name = "profile", Order = 1, EmitDefaultValue = false)] public string Profile { get; set; }
        [DataMember(Name = "title", Order = 2, EmitDefaultValue = false)] public string Title { get; set; }
        [DataMember(Name = "desktop", Order = 3)] public int Desktop { get; set; }
        [DataMember(Name = "order", Order = 4)] public int Order { get; set; }
    }

    /// <summary>A single managed desktop and the windows assigned to it. Runtime only, not serialized.</summary>
    public sealed class DesktopModel : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler PropertyChanged;

        public ObservableCollection<WindowModel> Windows { get; } = new ObservableCollection<WindowModel>();

        // Per-desktop row override for grid height. 0 means use the global RowsPerDesktop.
        public int Rows { get; set; }

        // Runtime only: the row count this desktop currently shows (its locked value, or the auto-filled
        // height it last rendered). Lets a divider drag start from the true displayed size, with no jump.
        public int DisplayRows { get; set; }

        private int _index;
        public int Index
        {
            get { return _index; }
            set { if (_index != value) { _index = value; Raise("Index"); } }
        }

        private string _name;
        public string Name
        {
            get { return _name; }
            set { if (_name != value) { _name = value; Raise("Name"); } }
        }

        private bool _isActive;
        public bool IsActive
        {
            get { return _isActive; }
            set { if (_isActive != value) { _isActive = value; Raise("IsActive"); } }
        }

        private void Raise(string p)
        {
            var h = PropertyChanged;
            if (h != null) h(this, new PropertyChangedEventArgs(p));
        }
    }

    /// <summary>A tracked top-level window. Runtime only, not serialized.</summary>
    public sealed class WindowModel : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler PropertyChanged;

        public IntPtr Hwnd { get; set; }
        public uint Pid { get; set; }
        public string ExePath { get; set; }
        public string ProfileArg { get; set; } // browser profile, so profiles pin independently

        public int DesktopIndex { get; set; }

        // Runtime only: true after an Alt+Tab reveal parked this window at the bottom of the z-order,
        // so the next show lifts it back to the top instead of leaving it buried.
        public bool ZTrashed { get; set; }

        private string _title;
        public string Title
        {
            get { return _title; }
            set { if (_title != value) { _title = value; Raise("Title"); } }
        }

        private ImageSource _icon;
        public ImageSource Icon
        {
            get { return _icon; }
            set { if (!ReferenceEquals(_icon, value)) { _icon = value; Raise("Icon"); } }
        }

        private bool _isActive;
        public bool IsActive
        {
            get { return _isActive; }
            set { if (_isActive != value) { _isActive = value; Raise("IsActive"); } }
        }

        private bool _isAlwaysOnTop;
        public bool IsAlwaysOnTop
        {
            get { return _isAlwaysOnTop; }
            set { if (_isAlwaysOnTop != value) { _isAlwaysOnTop = value; Raise("IsAlwaysOnTop"); } }
        }

        private void Raise(string p)
        {
            var h = PropertyChanged;
            if (h != null) h(this, new PropertyChangedEventArgs(p));
        }
    }
}
