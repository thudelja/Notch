<img src="../branding/notch.png" width="96" height="96" alt="Notch icon">

# Notch for Windows

The Windows 10 and 11 version of Notch. It does exactly what the Mac version does (see the
[main README](../README.md)) and shares its folder layout and project files, so a projects
folder on an exFAT SSD works on both.

- Lives in the **notification area** (bottom right, next to Wi-Fi and the clock). Click the Notch
  icon (an orange SD card with a check) to open a panel styled like Windows 11 Quick Settings. Right-click for a menu.
- When a card goes in, it reads the shoot dates and cameras, shows a notification, and the panel
  asks which project each shoot date belongs to (a new project, one of your last two, or a project in any
  folder you choose).
- Copies into `Projects\2026\Photo\11 November\02.11.2026 Name\<Camera>\` and
  `Projects\2026\Video\11 November\02.11.2026 Name\Media\<Camera>\` (plus `Project`, `Assets`,
  `Export`), or into a structure of your own, verifies every file with SHA-256, never overwrites, and
  only offers new files when a card comes back.
- **Moves a whole year** to an external drive from Settings, deleting each file from the PC only
  after its copy is verified.
- Follows Windows' light/dark mode and accent colour.

## Requirements

- Windows 11, or Windows 10 version 1809 or later
- x64 or ARM64 (Snapdragon) PCs

## Install

### Option A: download the installer

1. Download [Notch-Setup.exe](https://github.com/thudelja/Notch/releases/latest/download/Notch-Setup.exe)
   (all versions are on the [Releases](https://github.com/thudelja/Notch/releases) page).
2. Run it. The app isn't code-signed (that needs a paid certificate), so Windows may say
   *"Windows protected your PC"*: click **More info**, then **Run anyway**.
3. Click **Install**, then **Finish**. Notch starts.

It installs for your user only, without administrator rights, into
`%LOCALAPPDATA%\Programs\Notch`, with a Start menu entry. The same installer works on normal (x64)
and ARM (Snapdragon) PCs. To update, run the installer of the new version; your settings and
projects stay.

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

This runs the tests, builds `Notch.exe`, installs it to `%LOCALAPPDATA%\Programs\Notch`,
adds **Notch** to the Start menu, sets it to start with Windows and starts it. Apps you build
yourself don't trigger SmartScreen.

### First launch

- **Find the icon.** Windows 11 hides new notification-area icons behind the **^** arrow. Drag the SD
  card icon onto the taskbar, or turn it on in **Settings › Personalization › Taskbar › Other system
  tray icons**.
- **Turn off Windows' own prompt (optional).** Windows shows its own AutoPlay prompt when a card goes in.
  To stop it, set **Memory card** to **Take no action** in **Settings › Bluetooth & devices ›
  AutoPlay**.
- **Projects folder.** The default is `Pictures\Projects`. Change it in **Settings**, where you can also
  choose a separate folder for videos (for example on an SSD). Videos of new projects then go there in the
  same folder structure. The version is shown at the bottom of Settings.

## Using it

The panel shows, from top to bottom:

- **Quick settings tiles:** Auto import, Verify and Auto eject. Below them, buttons that open the
  projects folder, Settings and the log.
- **Import:** one row per shoot date with a project picker (New project, New project in a folder…, Same
  as another new project, your last two projects, or Skip for now), and the camera folder names. **New
  project in a folder…** lets you choose any folder, for example `2026\AMD Koledar 2026` for a calendar
  shot over several days; Notch makes the project's dated folder inside it. Choosing the folder of a
  project Notch already knows adds the date to that project.
- **Progress:** reading a card, an import, or a year being moved, each with a cancel button.
- **Cards:** connected cards, when each was last imported, and **Import** and **Eject** buttons.
- **Recent:** the last imports. Click one to open its folder.

**Settings › Folder structure…** changes how new projects are laid out below the year folder: Photo /
Video (renamed or left out), the month (number, name or both, English or Slovenian names), folders with
a fixed name, the project folder's date format and order, an optional **Unedited / Exported** pair
(imports go into the first; an empty second folder with the same path is made for exports), camera
folders on or off, and the folders in each video project. The structure is saved in the projects folder,
so a drive shared with the Mac builds the same folders. Existing projects keep their folders.

In **Settings › After importing** you can have Notch open the new photos of every import in
**Lightroom Classic** or **Lightroom**. The app opens its own import window with them (videos aren't
sent). In Lightroom Classic, choose **Add** to keep the photos where Notch put them; Lightroom copies
them into its own library and syncs them to Adobe's cloud.

### Sharing a drive with the Mac

Format the SSD as **exFAT**. Windows can't read APFS, and macOS can't write NTFS. Each projects
folder contains a hidden `.sdbackup` folder with its projects and an ID. When you connect a drive,
Notch looks for projects folders at its root and one level down, and adds their projects to
your recent list. This works even if the drive gets a different letter than last time.

### Where things are

| What | Where |
| --- | --- |
| The app | `%LOCALAPPDATA%\Programs\Notch\Notch.exe` |
| Settings, saved projects, import records | `%APPDATA%\Notch` |
| Log | `%LOCALAPPDATA%\Notch\Logs\backups.log` |

### Uninstall

Open **Settings › Apps › Installed apps**, find **Notch** and choose **Uninstall**. If you installed
it with `build.ps1` instead, run:

```powershell
powershell -ExecutionPolicy Bypass -File .\build.ps1 -Uninstall
```

Either way, the app, its shortcut and its **Start with Windows** entry are removed. Your projects
are never touched. Settings and import records stay in `%APPDATA%\Notch`; delete that folder to remove
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
├── installer/Notch.iss    The installer (Inno Setup); CI builds dist\Notch-Setup.exe from it.
└── build.ps1              Test, build, install, uninstall.
```

```powershell
dotnet test tests/SDBackup.Core.Tests                     # core tests (any OS)
dotnet run --project src/SDBackup.App                     # run the app
dotnet run --project src/SDBackup.App -- --self-test out  # render every window with sample data into .\out
```

- **Icons:** `Assets/` holds files generated by `branding/make_icons.py` at the repository root, which
  draws the logo for both apps.
- **Self-test:** `--self-test` opens the panel and the settings window in light and dark mode with
  sample data, writes screenshots, and fails on any XAML or data-binding error. CI runs it on every
  push.
- **Compile-checking on macOS or Linux:** the core and `SDBackup.Windows` build anywhere:
  `dotnet build src/SDBackup.Windows -p:ImportWindowsDesktopTargets=false`. The XAML app itself
  needs the Windows Desktop SDK, which ships with the .NET SDK on Windows.
- **The specification** both versions follow is [PORTING.md](../PORTING.md).
