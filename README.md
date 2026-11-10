<img src="branding/notch.png" width="96" height="96" alt="Notch icon">

# Notch

A macOS menu bar app that imports SD cards straight into organised project folders.

Insert a card. Notch reads it, works out the shoot dates and which cameras were used, and asks
which project each shoot date belongs to: a new project, a day of a multi-day shoot, or one of your
last two projects. It then copies photos and videos into the right year, month, project and camera
folders, each to its own place, and verifies every copy.

Notch speaks **English** and **Slovenian** (*slovensko*: [README.sl.md](README.sl.md)).

**Also on Windows:** a Windows 10/11 version with the same features lives in [`windows/`](windows/).
It shares the folder layout and the project files, so a projects folder on an exFAT SSD works on
both.

## Download

| | |
| --- | --- |
| **Mac** (macOS 13 or later, Apple silicon or Intel) | [Notch.dmg](https://github.com/thudelja/Notch/releases/latest/download/Notch.dmg) |
| **Windows** (Windows 10 1809 or later, or 11) | [Notch-Setup.exe](https://github.com/thudelja/Notch/releases/latest/download/Notch-Setup.exe) |

All versions are on the [Releases](https://github.com/thudelja/Notch/releases) page. How to
install: [Mac](#install-on-a-mac) · [Windows](windows/README.md#install).

## The folder structure

Everything goes into one **projects folder** (default `~/Pictures/Projects`, changeable in
Settings, can be an external drive):

```
Projects/
└── 2026/
    ├── Photo/
    │   └── 11 November/
    │       └── 02.11.2026 Ana Wedding/
    │           ├── R5/            ← IMG_0001.CR3, IMG_0001.JPG …
    │           └── R6/
    └── Video/
        └── 11 November/
            └── 02.11.2026 Ana Wedding/
                ├── Media/
                │   ├── R5/        ← C0001.MP4, C0001M01.XML …
                │   └── R6/
                ├── Project/       ← for your editing project
                ├── Assets/
                └── Export/
```

- **Year, month and date** come from the **shoot date** stored in each file (EXIF for photos, clip
  metadata for video), falling back to the file's date. Importing a week later still files
  everything under the day it was shot.
- **Photo or Video** is decided by file type. A card with both is split between the two branches,
  under the same project name. Photos and videos each have their own **destination**: a projects
  folder and a structure (see below). When both go to the same folder, the `Photo` and `Video`
  folders keep them apart; with a separate folder for videos, those folders are left out.
- **A project folder** is named after its first shoot day. Adding a later day to an existing
  project puts the files in that same folder.
- **Camera folders** are always created, even when only one camera was used, so files never move
  later and editing projects never lose their media links.
- **Original file names** are kept. RAW and JPEG stay side by side. If two different files share
  a name, the newer one is saved as `IMG_0001 (1).JPG`; nothing is ever overwritten.
- **Sidecar files** (Sony/Canon clip `.XML`, GoPro `.THM`, DJI `.LRF`/`.SRT`, `.XMP`) are copied
  next to the file they belong to. **Audio** (`.WAV` etc.) goes to the video project's
  `Media/<device>/`. Camera system and database files are skipped.

### Your own structure

**Settings › Folder Structure…** changes how new projects are laid out, with a **Photos** and a
**Videos** tab: photos and videos each have their own structure. Below the year folder, which always
comes first, you build the levels from blocks: the **Photo** or **Video** folder (rename it or leave it
out), the **month** (`11 November`, `11` or `November`, English or Slovenian names), **folders with a
fixed name** such as `Clients`, the **project folder** (`02.11.2026`, `2026-11-02` or `20261102`,
before or after the name) and an optional **Unedited / Exported** pair. You can also turn camera
folders off and, on the Videos tab, choose the folders made in each video project. The editor shows
example paths as you go.

With Unedited / Exported, imports go into the first folder, and an empty Exported folder with the same
path is made for your exports. For example, photos with `2026 › Photo › Unedited/Exported › month`
and videos as standard:

```
2026/Photo/Unedited/11 November/02.11.2026 Ana Wedding/R5/IMG_0001.CR3
2026/Photo/Exported/11 November/02.11.2026 Ana Wedding/      ← empty, for exports
2026/Video/11 November/02.11.2026 Ana Wedding/Media/R5/C0001.MP4
```

A new, empty projects folder starts with the standard structure; on a Mac set to Slovenian, its
folders get Slovenian names (`Foto`, `Video`, `11 november`; `Posnetki`, `Projekt`, `Viri`, `Izvoz`).

The structure is saved in the projects folder, so the Mac and Windows versions build the same folders
on a shared drive. **Existing projects never move:** each project keeps the structure it was created
with, and adding a day to it still puts the files in its folder.

## Importing a card

1. **Insert the card.** Notch reads it in the background. Nothing is copied yet.
2. **A notification appears**, and the menu bar icon shows a count of cards waiting.
3. **Click the Notch icon** (an SD card with a check) in the menu bar. The Import panel lists every shoot date on the card with its
   photo, video and audio counts. For each date, choose:
   - **New project** and type its name. Below the name, **Photos → Projects** and
     **Videos → Projects** show where its files go. **Change…** picks another folder for the photos
     or the videos (the other keeps its place): Notch makes the project's folder (`02.11.2026 Ana`)
     inside the folder you choose. If you choose the folder of a project Notch already knows, the
     date is added to that project instead, which is how you reach older projects.
   - **New multi-day shoot…** for a shoot over several days or cards, such as a calendar: type the
     shoot's name (`AMD Koledar 2026`) and the day's project (`Ana`). See
     [Multi-day shoots](#multi-day-shoots).
   - **New in “AMD Koledar 2026”**: the next day of an open multi-day shoot.
   - **Same as** another new project on this card
   - one of your **last two projects**
   - **Skip for now**

   By default, every date goes into one new project. Below the dates, check the **camera folder
   names**. They are read from the files (`Canon EOS R5` → `R5`, `ILCE-7M4` → `A7 IV`) and you
   can change them. Your names are remembered for next time.
4. **Click Import.** Files are copied, checked against the originals, and the card can be ejected
   automatically.

Insert the same card again later and only the **new** files are offered. If there's nothing new,
you just get a notification. Choose **Not Now** to close the panel; you can start again with
**Import** next to the card.

### Camera detection

- **Photos:** always detected, from EXIF.
- **Sony video:** detected from the clip XML.
- **Other video:** detected from file metadata where the camera writes it. If it doesn't, the
  video is assumed to come from the camera that shot the card's photos.
- **Audio, and anything else without camera info:** shown as "No camera info". The name you type
  is remembered for that card, for example "Zoom H5" for a recorder card.

## Multi-day shoots

A multi-day shoot (a calendar, a festival, a series) keeps all its days in one folder. Its folder
takes the month's place in your structures, and each day is a project inside it:

```
2026/Photo/AMD Koledar 2026/02.11.2026 Ana/R5/…
2026/Photo/AMD Koledar 2026/09.11.2026 Maja/R5/…
2026/Video/AMD Koledar 2026/Media/02.11.2026 Ana/R5/…
2026/Video/AMD Koledar 2026/Media/09.11.2026 Maja/R5/…
2026/Video/AMD Koledar 2026/Project/   Assets/   Export/      ← once for the whole shoot
```

Videos are one editing project for the whole shoot: each day's footage goes into its own folder in
`Media`. With Unedited / Exported, photos get both, as usual
(`2026/Photo/Unedited/AMD Koledar 2026/02.11.2026 Ana/…`).

Start one in the Import panel with **New multi-day shoot…**. From then on it's offered above your
recent projects as **New in “AMD Koledar 2026”**. When the shoot is done, **hold** the
**Hold to finish** button for 1.5 seconds (a ring fills while you hold; a short animation shows it's
held, not clicked), in the panel or in **Settings › Multi-day shoots**, where you can also reopen a
shoot. A shoot that isn't used for 100 days leaves the panel by itself.

## Moving a year to an SSD

When a year is finished, or your Mac runs low on space, move the whole year to an external drive.
Open **Settings › Move a year to another drive**, pick the year and click **Move to Drive…**, then
choose a folder on the drive (for example `SSD/Projects`). The year is moved into it with exactly
the same structure:

```
Mac:  ~/Pictures/Projects/2026/…   →   SSD:  /Volumes/SSD/Projects/2026/…
```

- **Safe, file by file:** each file is copied, read back from the SSD and checked (SHA-256), then
  deleted from the Mac. Anything that can't be copied or checked stays on the Mac and is listed
  in the log.
- **Interruptions are fine:** if you stop the move, pull the drive or quit, every file is either
  moved or still on the Mac. Moving the year again finishes the job. Files already on the SSD
  are recognised by comparing their bytes.
- **Everything moves:** photos, videos, audio, editing projects, empty Project/Assets/Export
  folders, file dates and Finder tags.
- **Afterwards:** moved projects stay in your recent list and open from the SSD. Adding a card
  to one of them imports straight onto the SSD. If the SSD isn't connected, the Import panel
  tells you. New projects are still created on the Mac; move them the same way later, and they
  merge into the year folder on the SSD.
- Editing projects that link to moved media (Premiere Pro, DaVinci Resolve, …) may ask you to
  relink once.

Imports wait while a year is being moved. Progress appears in the menu bar panel.

## Install on a Mac

Requires macOS 13 Ventura or later, on Apple silicon or Intel.

### Option A: download it

1. Download [Notch.dmg](https://github.com/thudelja/Notch/releases/latest/download/Notch.dmg)
   and open it.
2. Drag **Notch** onto the **Applications** folder in the window that opens.
3. Open Notch from Applications. macOS blocks it the first time, because the app isn't notarised
   by Apple (that needs a paid Apple developer account). Click **Done**, open
   **System Settings › Privacy & Security**, scroll down to *"Notch was blocked…"* and click
   **Open Anyway**. This is needed only once.

The Notch icon (an SD card with a check) appears in the menu bar. To update, download the new
version and replace the app in Applications; your settings and projects stay.

### Option B: build it yourself

Requires Xcode, or just the Command Line Tools: `xcode-select --install`.

```bash
./scripts/build-app.sh --install
```

This compiles the app, puts `Notch.app` in `/Applications`, and launches it. To build without
installing, omit `--install`; the app is written to `build/Notch.app`. Apps you build yourself
aren't blocked by macOS.

The app was called SD Backup before. If `SD Backup.app` is installed, `--install` replaces it, and
your settings, projects and import records carry over. (With Option A, delete `SD Backup.app` from
Applications yourself.)

### First launch

macOS will ask for two permissions. Allow both:

1. **Notifications**, so you know when a card is ready to import and when it's done.
2. **Access to files on a removable volume**, which appears the first time a card is inserted.
   If you dismissed the prompt, enable it in
   **System Settings › Privacy & Security › Files and Folders › Notch › Removable Volumes**.

When the app runs from `/Applications`, it registers itself to open at login the first time it
starts. You can change this in Settings.

**Updating from 1.0 or 1.1:** Notch 1.2 has its own app identifier (the earlier one came from the
first SD Backup builds, which is why notifications showed a blank icon). Your settings, camera
names and recent imports carry over automatically, but macOS asks once more for notifications and
card access. If **System Settings › General › Login Items** lists Notch twice, remove the entry that
is switched off.

> Notch is signed ad hoc rather than with an Apple Developer ID. macOS ties privacy permissions to
> the signature, so after an update you may be asked for card access again.

## The menu bar panel

The panel is styled like Control Center:

- **Toggles**: Auto Import (read and ask as soon as a card is inserted), Verify (SHA-256 check of
  every copy), Auto Eject.
- **Projects** opens the projects folder. **Settings** opens all options.
- **Import**: the shoot-date and camera choices described above.
- **Status**: reading progress, or import progress with size, file count, time left and a cancel
  button.
- **Cards** lists connected cards, when each was last imported, and **Import** / **Eject** buttons.
- **Recent** shows the last imports. Click one to open its project folder.

Ejecting a card in Finder while it is being read or imported stops that work so the card can be
ejected. If macOS says the disk is still in use, eject it again.

## Settings

- **Projects folder** for photos and, if you like, a **separate folder for videos** (for example on
  an SSD). Videos of new projects then go there, with their own folder structure; existing projects
  stay where they are. Also the **folder structures** for photos and videos (see above).
- **Multi-day shoots:** finish or reopen them (see above).
- **Ask for a project as soon as a card is inserted.**
- **Which cards count:** SD and camera cards, or any removable drive. External hard drives and
  SSDs are never included.
- **Verify** and **eject**.
- **Open new photos in Lightroom Classic or Lightroom** after an import (Settings › After
  importing). The app opens its own import window with the photos from that import; videos
  aren't sent. In Lightroom Classic, choose **Add** to keep the photos where Notch put them.
  Lightroom copies them into its own library and syncs them to Adobe's cloud.
- **Language:** the Mac's, English or Slovenian (*Slovenščina*). Notch switches when it starts
  again; **Restart Now** does that for you. You can also set it in **System Settings › General ›
  Language & Region › Applications**.
- **Open at login** and **notifications**.
- **Move a year to another drive** (see above). With a separate videos folder, choose whether to
  move the year from the photos or the videos folder.
- The **version** is shown at the bottom.
- **Forget Imported Files** offers every file on every card again. **Forget Camera Names**
  resets the folder names you've given cameras.

Each projects folder contains a hidden `.sdbackup` folder that identifies it and lists its
projects, without any absolute paths. That's how moved projects are found again when the SSD
mounts under a different name, and how the Windows version opens the same folders. Don't
delete it.

The rest of the app's memory is local to this Mac: `~/Library/Application Support/SD Backup` holds
where each projects folder was last seen, a copy of the project list and the import records. The
log is at `~/Library/Logs/SD Backup/backups.log`. (These folders keep the app's earlier name, so
nothing is lost when updating.)

## Development

```bash
swift build          # compile
swift test           # run the core tests (requires full Xcode)
swift run SDBackup   # run without an app bundle (notifications and login item are disabled)
```

To work in Xcode, run `open Package.swift`.

| Path | Purpose |
| --- | --- |
| `Sources/SDBackupCore/CardAnalyzer.swift` | Reads a card: shoot days, cameras, what's new. |
| `Sources/SDBackupCore/MediaClassifier.swift` | Photo / video / audio / sidecar detection, and pairing sidecars with their media. |
| `Sources/SDBackupCore/MediaMetadata.swift` | EXIF (ImageIO), video metadata (AVFoundation), Sony clip XML. |
| `Sources/SDBackupCore/CameraNames.swift` | `Canon EOS R5` → `R5`, `ILCE-7M4` → `A7 IV`, … |
| `Sources/SDBackupCore/ProjectLayout.swift` | Folder naming, the copy plan, and the per-card import record. |
| `Sources/SDBackupCore/BackupEngine.swift` | Verified, non-destructive, crash-safe copying. |
| `Sources/SDBackupCore/YearMigrator.swift` | Moves a year folder to another drive, deleting each original after verification. |
| `Sources/SDBackupCore/ProjectLibrary.swift` | The portable `.sdbackup/library.json` file, and finding projects folders again on any drive. |
| `Sources/SDBackup/AppModel.swift` | The read → choose project → import flow. |
| `Sources/SDBackup/ImportDraft.swift` | Your per-date project choices, destinations, multi-day shoots and camera names. |
| `Sources/SDBackup/MultiDayShoots.swift` | The multi-day shoots: open, finished, last used. |
| `Sources/SDBackup/LibraryStore.swift` | Saved projects, camera names and import records. |
| `Sources/SDBackup/VolumeInspector.swift` | Decides whether a mounted volume is an SD/camera card. |
| `Sources/SDBackup/Views/` | Control Center–style panel and the Settings window (SwiftUI). |
| `Sources/SDBackup/MenuBarIcon.swift` | The menu bar icon, drawn in code. |
| `Resources/sl.lproj/` | Slovenian texts, by their English text. `scripts/check-translations.py` (run by CI) checks that every text has one. |
| `scripts/build-app.sh` | Builds, bundles and ad-hoc signs the `.app`; `--universal --dmg` makes the download. |
| `branding/make_icons.py` | Draws the logo and writes the Mac and Windows icon files (`python3 branding/make_icons.py`). |

**Windows:** the Windows version is in [`windows/`](windows/). [PORTING.md](PORTING.md) is its
specification: the rules, the data formats, the Windows equivalents of each macOS API, and the tests.

### Releasing a version

Tag the commit and push the tag:

```bash
git tag v1.0.0
```

```bash
git push origin v1.0.0
```

CI then builds `Notch.dmg` (Apple silicon and Intel) and `Notch-Setup.exe` (x64 and ARM64), with
the tag as the version number, and publishes them on a GitHub release. The download links above
always point to the latest release.

The core module (`SDBackupCore`) has no UI and is covered by `Tests/SDBackupCoreTests`, including
an end-to-end import of a mixed Canon/Sony/recorder card into the project structure.
