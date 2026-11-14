; MultiDesk one-click installer for Inno Setup (https://jrsoftware.org/isinfo.php).
;
; Produces Output\MultiDesk-<version>-release.exe. Because MultiDesk is a user-mode app that only
; writes HKCU keys, the installer runs without administrator rights (PrivilegesRequired=lowest). It
; installs the executable, adds a startup entry, and launches the app. Uninstall preserves settings
; under %APPDATA%\MultiDesk.
;
; To build: build MultiDesk.sln for x64 in Visual Studio, then open this file in Inno Setup and press
; Compile. The version in the file name, in Add or remove programs, and in the setup executable's own
; properties is read off the executable that was built. Nothing here carries a copy of the number,
; which is set in MultiDesk\Properties\AssemblyInfo.cs.

#define MyAppName "MultiDesk"
#define MyAppPublisher "PlexPixel"
#define MyAppExe "MultiDesk.exe"

; Release is packaged when it is there and Debug otherwise, so whichever configuration was built last
; compiles without an edit here. Either path can be pointed elsewhere with ISCC /DReleaseExe=... for a
; build whose output lands somewhere else.
#ifndef ReleaseExe
  #define ReleaseExe "MultiDesk\bin\x64\Release\MultiDesk.exe"
#endif
#ifndef DebugExe
  #define DebugExe "MultiDesk\bin\x64\Debug\MultiDesk.exe"
#endif
#if FileExists(ReleaseExe)
  #define MyAppSource ReleaseExe
  #define MyAppConfig "Release"
#elif FileExists(DebugExe)
  #define MyAppSource DebugExe
  #define MyAppConfig "Debug"
#else
  #error Neither bin\x64\Release nor bin\x64\Debug holds MultiDesk.exe. Build MultiDesk.sln for x64 first.
#endif

; Four parts for the version resources Windows reads, three for everything a person reads.
#define VerMajor
#define VerMinor
#define VerRev
#define VerBuild
#expr GetVersionComponents(MyAppSource, VerMajor, VerMinor, VerRev, VerBuild)
#define MyAppVersion Str(VerMajor) + "." + Str(VerMinor) + "." + Str(VerRev)
#define MyAppVersionFull GetVersionNumbersString(MyAppSource)
#if MyAppVersion == "0.0.0"
  #error The executable carries no version number. Check AssemblyVersion in MultiDesk\Properties\AssemblyInfo.cs.
#endif
#pragma message "Packaging MultiDesk " + MyAppVersion + " (" + MyAppVersionFull + ") from the " + MyAppConfig + " build"

[Setup]
AppId={{C9F2A4B6-3D5E-4F70-9A1B-2C4E6F8A0B1D}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
DefaultDirName={localappdata}\{#MyAppName}
DisableProgramGroupPage=yes
DisableDirPage=yes
PrivilegesRequired=lowest
OutputBaseFilename={#MyAppName}-{#MyAppVersion}-release
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
ArchitecturesInstallIn64BitMode=x64compatible
ArchitecturesAllowed=x64compatible
CloseApplications=yes
RestartApplications=no

; Inno leaves the setup executable's own File version blank unless it is set here, which is why the
; properties of every setup built before this line showed nothing.
VersionInfoVersion={#MyAppVersionFull}
VersionInfoProductVersion={#MyAppVersionFull}
VersionInfoProductName={#MyAppName}
VersionInfoCompany={#MyAppPublisher}
VersionInfoDescription={#MyAppName} {#MyAppVersion} setup

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Files]
Source: "{#MyAppSource}"; DestDir: "{app}"; Flags: ignoreversion

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
