# SD Backup for Windows

The Windows 10 and 11 version of SD Backup. It does exactly what the Mac version does (see the
[main README](../README.md)) and shares its folder layout and project files, so a projects
folder on an exFAT SSD works on both.

- Lives in the **notification area** (bottom right, next to Wi-Fi and the clock). Click the SD card
  icon to open a panel styled like Windows 11 Quick Settings. Right-click for a menu.
- When a card goes in, it reads the shoot dates and cameras, shows a notification, and the panel
  asks which project each shoot date belongs to (a new project or one of your last three).
- Copies into `Projects\2026\Photo\11 November\02.11.2026 Name\<Camera>\` and
  `Projects\2026\Video\11 November\02.11.2026 Name\Media\<Camera>\` (plus `Project`, `Assets`,
  `Export`), verifies every file with SHA-256, never overwrites, and only offers new files when a
  card comes back.
- **Moves a whole year** to an external drive from Settings, deleting each file from the PC only
  after its copy is verified.
- Follows Windows' light/dark mode and accent colour.

## Requirements

- Windows 11, or Windows 10 version 1809 or later
- x64 or ARM64 (Snapdragon) PCs

## Install

### Option A: download the ready-made app

1. Open the repository on GitHub, go to **Actions**, open the latest successful **Build** run, and
   download **SD-Backup-Windows** at the bottom of the page.
2. Unzip it. Use `win-x64\SDBackup.exe` for most PCs, or `win-arm64\SDBackup.exe` for ARM PCs.
3. Move `SDBackup.exe` to a permanent place, for example
   `C:\Users\<you>\AppData\Local\Programs\SD Backup\`, and double-click it.

The app isn't code-signed, so Windows SmartScreen may say *"Windows protected your PC"*. Click
**More info**, then **Run anyway**. This happens only the first time.

### Option B: build it yourself

1. Install the .NET 10 SDK. In PowerShell:

   ```powershell
   winget install Microsoft.DotNet.SDK.10
   ```

2. Download the repository (**Code → Download ZIP** on GitHub) and unzip it.
3. Open PowerShell in the `windows` folder and run:

   ```powershell
   powershell -ExecutionPolicy Bypass -File .\build.ps1 -Install
   ```

This runs the tests, builds `SDBackup.exe`, installs it to `%LOCALAPPDATA%\Programs\SD Backup`,
adds **SD Backup** to the Start menu, sets it to start with Windows and starts it. Apps you build
yourself don't trigger SmartScreen.

### First launch

- **Find the icon.** Windows 11 hides new notification-area icons behind the **^** arrow. Drag the SD
  card icon onto the taskbar, or turn it on in **Settings › Personalization › Taskbar › Other system
  tray icons**.
- **Turn off Windows' own prompt (optional).** Windows shows its own AutoPlay prompt when a card goes in.
  To stop it, set **Memory card** to **Take no action** in **Settings › Bluetooth & devices ›
  AutoPlay**.
- **Projects folder.** The default is `Pictures\Projects`. Change it in **Settings**.

## Using it

The panel shows, from top to bottom:

- **Quick settings tiles:** Auto import, Verify and Auto eject. Below them, buttons that open the
  projects folder, Settings and the log.
- **Import:** one row per shoot date with a project picker (New project, Same as another new
  project, your last three projects, or Skip for now), and the camera folder names.
- **Progress:** reading a card, an import, or a year being moved, each with a cancel button.
- **Cards:** connected cards, when each was last imported, and **Import** and **Eject** buttons.
- **Recent:** the last imports. Click one to open its folder.

### Sharing a drive with the Mac

Format the SSD as **exFAT**. Windows can't read APFS, and macOS can't write NTFS. Each projects
folder contains a hidden `.sdbackup` folder with its projects and an ID. When you connect a drive,
SD Backup looks for projects folders at its root and one level down, and adds their projects to
your recent list. This works even if the drive gets a different letter than last time.

### Where things are

| What | Where |
| --- | --- |
| The app | `%LOCALAPPDATA%\Programs\SD Backup\SDBackup.exe` |
| Settings, saved projects, import records | `%APPDATA%\SD Backup` |
| Log | `%LOCALAPPDATA%\SD Backup\Logs\backups.log` |

### Uninstall

```powershell
powershell -ExecutionPolicy Bypass -File .\build.ps1 -Uninstall
```

This removes the app, its shortcut and its **Start with Windows** entry. Your projects are never
touched. Settings and import records stay in `%APPDATA%\SD Backup`; delete that folder to remove
them too.

## Differences from the Mac version

- **Ejecting from File Explorer during an import** is refused by Windows, because the card is in use.
  Cancel the import in the panel first, then eject.
- **Verification:** copies are written with write-through to the drive, then read back for the
  SHA-256 check. On Windows the read-back can be served from the file cache.
- **Card identity** uses the volume serial number Windows shows. It changes when a card is
  reformatted, just as on the Mac.
- **Shared drives:** projects folders on connected drives are found automatically (section above). On
  the Mac, a drive's projects show up once you move a year to it or choose it as the projects folder.

## Development

```
windows/
├── src/SDBackup.Core      Import, copy and project logic. No Windows code; runs on any OS.
├── src/SDBackup.Windows   Drive watching, card detection, eject, settings, view models (no XAML).
├── src/SDBackup.App       The WPF app: tray icon, panel, settings window, theme, self-test.
├── tests/                 xUnit tests for the core (62).
├── tools/make_icons.py    Draws the icons.
└── build.ps1              Test, build, install, uninstall.
```

```powershell
dotnet test tests/SDBackup.Core.Tests                     # core tests (any OS)
dotnet run --project src/SDBackup.App                     # run the app
dotnet run --project src/SDBackup.App -- --self-test out  # render every window with sample data into .\out
```

- **Self-test:** `--self-test` opens the panel and the settings window in light and dark mode with
  sample data, writes screenshots, and fails on any XAML or data-binding error. CI runs it on every
  push.
- **Compile-checking on macOS or Linux:** the core and `SDBackup.Windows` build anywhere:
  `dotnet build src/SDBackup.Windows -p:ImportWindowsDesktopTargets=false`. The XAML app itself
  needs the Windows Desktop SDK, which ships with the .NET SDK on Windows.
- **The specification** both versions follow is [PORTING.md](../PORTING.md).
