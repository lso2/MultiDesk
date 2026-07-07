; MultiDesk one-click installer for Inno Setup (https://jrsoftware.org/isinfo.php).
;
; Produces a single MultiDeskSetup.exe. Because MultiDesk is a user-mode app that only writes HKCU
; keys, the installer runs without administrator rights (PrivilegesRequired=lowest). It installs the
; executable, adds a startup entry, and launches the app. Uninstall preserves settings under
; %APPDATA%\MultiDesk.
;
; To build: open MultiDesk.sln in Visual Studio and build it (Debug|x64 or Release|x64) so MultiDesk.exe
; exists at the path in [Files], then open this file in Inno Setup and press Compile.

#define MyAppName "MultiDesk"
#define MyAppVersion "2.8.1"
#define MyAppPublisher "PlexPixel"
#define MyAppExe "MultiDesk.exe"

[Setup]
AppId={{C9F2A4B6-3D5E-4F70-9A1B-2C4E6F8A0B1D}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
DefaultDirName={localappdata}\{#MyAppName}
DisableProgramGroupPage=yes
DisableDirPage=yes
PrivilegesRequired=lowest
OutputBaseFilename=MultiDeskSetup
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
ArchitecturesInstallIn64BitMode=x64
ArchitecturesAllowed=x64
CloseApplications=yes
RestartApplications=no

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Files]
; Path matches the configuration you built. You built Debug|x64; change Debug to Release here if you build Release|x64.
Source: "MultiDesk\bin\x64\Debug\{#MyAppExe}"; DestDir: "{app}"; Flags: ignoreversion

[Registry]
; Start with Windows.
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "MultiDesk"; ValueData: """{app}\{#MyAppExe}"""; Flags: uninsdeletevalue

[Dirs]
Name: "{userappdata}\MultiDesk"

[Run]
Filename: "{app}\{#MyAppExe}"; Description: "Launch MultiDesk"; Flags: nowait postinstall skipifsilent

[UninstallRun]
; Stop the app before files are removed so all managed windows are shown again cleanly.
Filename: "{cmd}"; Parameters: "/c taskkill /im {#MyAppExe} /f"; Flags: runhidden; RunOnceId: "StopMultiDesk"
