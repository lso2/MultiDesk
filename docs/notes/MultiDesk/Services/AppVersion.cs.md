# Version number

## One place it is written

`MultiDesk\Properties\AssemblyInfo.cs` carries `AssemblyVersion` and `AssemblyFileVersion`. Nothing
else in the project holds a copy of the number.

Everything that shows or compares a version reads it from there:

- `Services\AppVersion.cs` reads it off the running assembly at startup and exposes `Current` for
  comparison and `Display` for text.
- The settings window prints `Display` under its title.
- The trace banner prints it on every session.
- `Services\UpdateService.cs` compares `Current` against the newest published release.
- `Installer.iss` reads it off the built executable with `GetVersionComponents` and `GetVersionNumbersString`.
- The request to the GitHub API sends it in the user-agent header.

## Format

`Display` prints three parts, `Major.Minor.Build`, and drops the revision. `AssemblyVersion` needs
four parts, so the revision is written as `0` and never shown.

## The installer

`Installer.iss` takes the version from whichever executable it packages, which is Release when that
build is present and Debug otherwise. It uses two spellings of the same number.

| Spelling | Used for |
| --- | --- |
| Three parts, `2.10.0` | `AppVersion`, which is what Add or remove programs shows, and the output file name `MultiDesk-2.10.0-release.exe` |
| Four parts, `2.10.0.0` | `VersionInfoVersion` and `VersionInfoProductVersion`, the version resources Windows reads from the setup executable's properties |

Two failures stop the compile with a message instead of producing a setup with a blank version: no
executable in either build folder, and an executable whose version reads `0.0.0`. A successful
compile prints which build and which version it packaged.

`VersionInfoVersion` had never been set, which is why every setup built before 2.10.0 shows an empty
**File version** in its properties while **Product version** is filled in.

## Raising it

Edit the two attributes in `AssemblyInfo.cs`, build for x64, then update the README badge and add a
`CHANGELOG.md` entry. The installer needs no edit at all.

## History

| Version | Note |
| --- | --- |
| 1.0.0.0 | The assembly carried this from the first commit through 2.9.17 while the installer carried the real number separately, so the running app could not report its own version |
| 2.10.0.0 | The assembly became the one source, read back at runtime (2026-09-14) |

## Verification performed

Compiled on 2026-09-14 with Inno Setup 7.0.1-beta, 0 warnings and 0 errors, in three states.

| State | Result |
| --- | --- |
| Release present | Packaged Release, named `MultiDesk-2.10.0-release.exe`, File version and Product version both `2.10.0.0`, product name `MultiDesk`, company `PlexPixel`, description `MultiDesk 2.10.0 setup` |
| Release absent, Debug present | Packaged Debug, same version and name |
| Neither present | Stopped with `Neither bin\x64\Release nor bin\x64\Debug holds MultiDesk.exe`, exit code 2 |

Installing the result was not executed. Running it writes an HKCU startup entry and a copy under the
local app folder, which is a change to the machine rather than a check of the script.
