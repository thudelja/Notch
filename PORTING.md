# Porting Notch to Windows

This document is the specification for the Windows version of Notch (formerly SD Backup). It describes **what the
app does**, independent of Swift and macOS, so a Windows implementation behaves the same and can
share projects folders (for example on an external SSD) with the Mac version.

The Mac source is the reference implementation. Where this document and the code disagree, the
code in `Sources/SDBackupCore` wins; please update this file.

**Status:** the Windows version exists in [`windows/`](windows/) (C#/.NET, WPF). It implements
this specification. `windows/src/SDBackup.Core` mirrors `Sources/SDBackupCore` file for file, and
its 62 tests include the ported Mac tests plus tests that read JSON exactly as the Mac writes it.
Windows-specific behaviour is described in [windows/README.md](windows/README.md#differences-from-the-mac-version).

**Contents**

1. [Architecture](#1-architecture)
2. [Folder layout](#2-folder-layout)
3. [Reading a card](#3-reading-a-card)
4. [Importing](#4-importing)
5. [The copy engine](#5-the-copy-engine)
6. [Moving a year to another drive](#6-moving-a-year-to-another-drive)
7. [Data files](#7-data-files)
8. [Windows equivalents](#8-windows-equivalents)
9. [Windows-specific rules](#9-windows-specific-rules)
10. [Tests to port](#10-tests-to-port)

---

## 1. Architecture

| Layer | Mac | Responsibility |
| --- | --- | --- |
| Core | `Sources/SDBackupCore` | Scanning, classification, metadata, camera names, folder layout, copy engine, year moves, data formats. No UI. Fully unit tested. |
| App | `Sources/SDBackup` | Detecting cards, the import flow, saved state, menu bar UI, notifications, settings. |

Port the core first, with its tests (section 10), then build the Windows app around it.

## 2. Folder layout

Everything goes into a **projects folder** chosen by the user (Mac default:
`~/Pictures/Projects`; suggested Windows default: `%USERPROFILE%\Pictures\Projects`).

```
<projects folder>/
├── .sdbackup/library.json                     ← identity and project list (section 7.1)
└── 2026/
    ├── Photo/
    │   └── 11 November/
    │       └── 02.11.2026 Ana Wedding/
    │           └── <Camera>/                  ← photos and their sidecars
    └── Video/
        └── 11 November/
            └── 02.11.2026 Ana Wedding/
                ├── Media/<Camera>/            ← videos, audio and their sidecars
                ├── Project/                   ← created empty
                ├── Assets/                    ← created empty
                └── Export/                    ← created empty
```

| Part | Rule | Example |
| --- | --- | --- |
| Year | Four digits, from the project's **start day** | `2026` |
| Branch | `Photo` for photos; `Video` for videos **and audio** | `Video` |
| Month | Two-digit month, space, English month name (fixed, not localised) | `11 November`, `01 January` |
| Project folder | Start day as `DD.MM.YYYY`, space, sanitised project name | `02.11.2026 Ana Wedding` |
| Camera folder | Sanitised camera name, fallback `Camera` | `R5`, `A7 IV`, `Zoom H5` |
| Video subfolders | Exactly `Media`, `Project`, `Assets`, `Export`, created when a video project receives its first file | |

- A project's folders are always derived from its **name and start day**, never stored.
- A project keeps the folder of its **first** shoot day. Later days go into the same folder.
- A camera folder is created **even when only one camera was used**, so files never move later.
- **File names are never changed**, except that a different file with a name that is already taken
  gets ` (1)`, ` (2)`, … before the extension: `IMG_0001 (1).JPG`, `README (1)`.

**Cleaning up names** (`BackupEngine.sanitizedFolderName`), identical on every OS so shared
drives get the same folder names:

1. Replace each of `/ \ : * ? " < > |` and every control character with `-`.
2. Trim whitespace. Remove leading dots, then trailing dots and spaces.
3. If nothing is left, use the fallback: `Untitled` for projects, `Camera` for cameras.
4. If the part before the first dot is a Windows device name (`CON`, `PRN`, `AUX`, `NUL`,
   `COM1`–`COM9`, `LPT1`–`LPT9`, any case), prefix `_`.

Examples: `What? Yes*` → `What- Yes-`, `CON` → `_CON`, `Trailing dots. . ` → `Trailing dots`.

## 3. Reading a card

### 3.1 Which volumes are cards

A volume is a candidate when it is removable media: removable, local, not the system volume and
not a disk image. External hard drives and SSDs are excluded (they are not removable media).

In the default mode ("SD and camera cards") a candidate must also either:

- sit in an SD card reader (Mac: protocol `Secure Digital`, or a device, vendor or media name
  containing one of the words `sd sdhc sdxc microsd card cardreader reader mmc crw multicard
  cfexpress`), or
- contain one of the folders `DCIM`, `PRIVATE`, `AVCHD`, `MP_ROOT` at its root.

In "any removable drive" mode every candidate counts. A volume that contains the projects folder is
never a card.

**Card ID:** the volume's UUID (Mac). If there is none, `<volume name>-<capacity in bytes>`.
Reformatting a card changes its ID, so a reformatted card starts with a fresh import record.

### 3.2 Listing files

- Walk the whole card recursively. Skip symbolic links.
- Skip these names everywhere: `.Spotlight-V100`, `.fseventsd`, `.Trashes`, `.TemporaryItems`,
  `.DocumentRevisions-V100`, `.DS_Store`, `.apdisk`, `System Volume Information`, `$RECYCLE.BIN`,
  `RECYCLER`, and anything starting with `._`.
- Other hidden files are **not** skipped.

### 3.3 Classification (by extension, case-insensitive)

| Kind | Extensions |
| --- | --- |
| Photo | jpg jpeg heic heif hif png tif tiff webp avif dng cr2 cr3 crw nef nrw arw srf sr2 raf orf rw2 pef srw x3f 3fr fff iiq rwl gpr insp |
| Video | mp4 mov m4v mxf mts m2ts avi mkv crm braw r3d insv 3gp mpg mpeg wmv |
| Audio | wav bwf mp3 m4a aac aif aiff flac |
| Sidecar | xml xmp thm lrf lrv srt aae |
| Other | everything else: **not imported** |

**Sidecar pairing:** a sidecar belongs to the photo, video or audio file in the **same folder**
whose base name (name without extension, upper-cased) equals its own. Otherwise it belongs to the
file with the **longest** base name that its own base name starts with. Examples: `C0001M01.XML` →
`C0001.MP4`, `GX010123.THM` → `GX010123.MP4`, `IMG_0001.XMP` → `IMG_0001.CR3`. Sidecars that match
nothing are not imported. Sidecars always go into the same folder as their file.

### 3.4 Shoot day

The shoot day is the **calendar date on the camera's clock**, a local wall-clock date. Time-zone
offsets in metadata are ignored on purpose.

| Kind | Source, in order |
| --- | --- |
| Photo | EXIF `DateTimeOriginal`, then TIFF `DateTime`, then file creation date, then modification date |
| Video | Clip XML `<CreationDate value="…"/>` (Sony and some Canon), then file creation date, then modification date |
| Audio | File creation date, then modification date |

Date text is parsed by splitting on `: - T + .` and space, then taking year, month, day (and time).
Years ≤ 1980 or invalid months and days mean "no date" (cameras with an unset clock write
`0000:00:00`).

### 3.5 Camera

| Kind | Source, in order |
| --- | --- |
| Photo | EXIF/TIFF `Make` and `Model` |
| Video | Clip XML `<Device manufacturer="…" modelName="…"/>`. Then the container's model metadata, read from at most 3 clips per folder and reused for the rest of that folder. Then, if all photos on the card come from one camera, that camera. |
| Audio | None ("No camera info") |

**Short names** (`CameraNames.shortName`), applied to the model:

1. Remove a leading make: `Canon EOS `, `Canon `, `NIKON `, `Nikon `, `FUJIFILM `, `Fujifilm `,
   `OLYMPUS `, `Olympus `, `PENTAX `, `Panasonic `, `SONY `, `Sony `, `LEICA `, `Leica `, `RICOH `,
   `DJI `, `GoPro `, or otherwise the `Make` value followed by a space.
2. Sony: `ILME-FX3` → `FX3`; `ILCE-7M4` → `A7 IV`, `ILCE-7SM3` → `A7S III`, `ILCE-6400` → `A6400`
   (an `M<digit>` suffix with digit 2–6 becomes a Roman numeral).
3. Panasonic: remove `DC-` (`DC-GH6` → `GH6`).
4. Nikon: if the name starts with `Z `, change `_2` → ` II` and `_3` → ` III`, and remove the space
   after `Z` (`NIKON Z 6_2` → `Z6 II`, `Z f` → `Zf`).

The user can rename every camera folder in the import panel. Names are remembered per model, and
per card for files without camera info.

## 4. Importing

1. **Card inserted** (if Auto Import is on), or **Import** clicked: read the card in the background
   (section 3), leaving out files recorded as imported before (section 7.3).
2. If nothing is new, notify "Nothing new on …" and stop.
3. Otherwise notify the user and show the **Import panel**: one row per shoot day (oldest first)
   with photo, video and audio counts and size. For each day the user picks:
   - **New project** with a name. By default the first day is a new project and every later day
     is "same as" it.
   - **Same as** another day's new project on this card.
   - One of the **3 most recently used projects**.
   - **Skip for now**: not imported, offered again next time.

   Below the days, the user can edit the **camera folder names**.
4. **Resolve projects.** A new project starts on the **earliest** day assigned to it and is
   created in the current projects folder. If a project with the same name (case-insensitive),
   start day and projects folder already exists, it is reused.
5. **Plan:** each file goes to `targetDirectory(kind, project, camera)` (section 2); sidecars go
   with their file. Video projects receiving files get their four subfolders.
6. **Copy** with the copy engine (section 5) into the projects' folder. Projects moved to another
   drive are imported there, and the import is refused if that drive isn't connected.
7. **Record:** every file that is now safely in place (copied, or an identical copy was already
   there) is added to the card's import record. Projects are marked as used (recent list).
8. **Hand off** (optional, Settings › After importing: *Lightroom Classic* or *Lightroom*; default
   none). If the import copied at least one new **photo** (complete or with problems), open the
   distinct folders those photos went to in the chosen app (`PhotoHandoff.folders`), which shows its
   own import window with them. Videos, audio, sidecars and files that were already there are left
   out. Mac: `NSWorkspace.open(_:withApplicationAt:)`; an app that isn't running is launched first
   and given time to finish (Lightroom Classic 15 ignores files received while launching). Windows:
   the app's `Lightroom.exe` (found under `Program Files\Adobe\Adobe Lightroom Classic*` or
   `…\Adobe Lightroom CC`) started with one folder per argument. The hand-off never changes the
   import's outcome: it is logged, and the user is notified only when the app isn't installed.

Only one import copies at a time; others queue. Imports wait while a year is being moved.

## 5. The copy engine

Input: a list of (source file, target folder) pairs, a destination root (which holds the staging
folder) and options. Guarantees:

1. **The source is only read.**
2. **Never overwrite.** For each file try `name.ext`, then `name (1).ext`, … `name (9999).ext`:
   - free → copy there (and reserve the name, case-insensitively, so two files in one run can't
     claim it);
   - a file is there that **is the same file** → nothing to copy;
   - otherwise → try the next name.

   "Same file" means: same size, and modification times within **2 seconds** (FAT precision) or,
   if the times differ, **identical bytes**. FAT cards store local time, so a time-zone change shifts
   every date; the byte comparison prevents duplicates. In *strict* mode (used for year moves) the
   bytes are always compared.
3. **Free space:** refuse to start unless the files to copy plus 200 MB fit on the destination.
   Running out of space mid-way stops the run.
4. **Staged writes:** each file is written to `<destination root>/.sdbackup-staging/<random>`, then
   flushed to disk (`fsync`). Then, with verification on, it is read back from disk, bypassing the
   cache, and its SHA-256 is compared with the SHA-256 computed while reading the source. Then the
   source's modification and creation dates (and, on Mac, extended attributes such as Finder tags)
   are applied, and the file is **moved** into place. The move fails rather than overwrite. The
   staging folder is emptied before and removed after each run.
5. **Errors:** a failing file is reported and the run continues, except: card removed → stop
   ("card removed"); destination gone → stop; out of space → stop.
6. **Cancellation** is checked between 4 MB chunks. A cancelled run leaves no partial files.
7. Callbacks: progress (at most about 10 per second), and "file completed" as soon as each file is
   safely in place.

## 6. Moving a year to another drive

Moves `<projects folder>/<year>` to `<chosen folder>/<year>` (`YearMigrator`):

1. Refuse if the destination is the same folder, or if the chosen drive folder doesn't exist.
2. Recreate **every folder**, including empty ones.
3. Copy every file with the copy engine in **strict** mode with verification **always on**.
4. As soon as each file is safely in place, **delete the original**. A file that couldn't be
   copied, checked or deleted stays and is reported.
5. Afterwards delete `.DS_Store` and `._*` files and empty folders in the old year folder, deepest
   first, and the year folder itself if empty. Report how many items remain (for example symlinks).
6. Projects of that year in the old projects folder now belong to the new one (their `libraryID`
   changes, section 7.1). New projects are still created in the main projects folder.

Interrupted moves are safe to repeat: identical files already at the destination are recognised
byte for byte and the originals deleted.

## 7. Data files

All JSON is UTF-8 and pretty-printed with sorted keys. Dates are ISO 8601 in UTC
(`2026-11-02T14:08:00Z`), UUIDs are upper-case strings, and a shoot day is
`{"year": 2026, "month": 11, "day": 2}`. Readers must ignore unknown keys.

### 7.1 Shared: `<projects folder>/.sdbackup/library.json`

This file makes a projects folder portable between computers and operating systems. It contains
**no absolute paths**.

```json
{
  "formatVersion" : 1,
  "libraryID" : "7F1C2E0A-3B9D-4C51-9E2A-6D8B1F4A2C33",
  "projects" : [
    {
      "id" : "C3A15B7E-0D2F-4E8A-9B61-2F7C4D9E8A10",
      "lastUsed" : "2026-11-02T14:08:00Z",
      "libraryID" : "7F1C2E0A-3B9D-4C51-9E2A-6D8B1F4A2C33",
      "name" : "Ana Wedding",
      "startDay" : { "day" : 2, "month" : 11, "year" : 2026 }
    }
  ]
}
```

- `libraryID` identifies the projects folder. A folder without the file gets one with a new ID the
  first time it is used.
- A project's folders are derived: `relativePhotoFolder` = `["2026", "Photo", "11 November",
  "02.11.2026 Ana Wedding"]`, joined with the OS separator under the projects folder.
- When a folder is opened, its projects are merged into the local list. A project already known
  locally is replaced only if the file's `lastUsed` is newer. Write the file only when its content
  would change.
- On Windows, set the Hidden attribute on `.sdbackup`.

### 7.2 Local to each computer

These hold paths or IDs that only make sense on one computer. They are not shared.

| What | Mac location | Content |
| --- | --- | --- |
| Where projects folders are | `~/Library/Application Support/SD Backup/libraries.json` | `[{libraryID, lastKnownPath, pathOnVolume, volumeName, markerPending?}]` |
| Project cache | `…/SD Backup/projects.json` | `{"formatVersion": 2, "projects": [...]}`, same project objects as 7.1 |
| Import records | `…/SD Backup/Imports/<card ID>.json` | section 7.3 |
| Settings, camera names, history | UserDefaults (`destinationPath`, `autoBackupEnabled`, `cardDetection`, `verifyCopies`, `ejectWhenDone`, `notificationsEnabled`, `photoApp`, `lastMigrationPath`, `cameraModelAliases`, `cameraCardAliases`, `importHistory`) | |
| Log | `~/Library/Logs/SD Backup/backups.log` | plain text |

The Mac folders keep the app's earlier name, SD Backup, so existing data carries over. The Windows version uses `%APPDATA%\Notch\` for the files and `%LOCALAPPDATA%\Notch\Logs\` for the log.

**Finding a projects folder again:** try `lastKnownPath`; it counts only if its `library.json` has
the expected `libraryID`. Otherwise try `<root of each connected drive>/<pathOnVolume>`. On Mac the
drive roots are `/` and `/Volumes/*`; on Windows, every drive letter. `pathOnVolume` always uses
`/` separators. A drive mounted as `/Volumes/SSD 1` or `F:\` instead of `E:\` is therefore still
found. If the folder isn't found, its projects show "drive not connected" and can't be imported
into.

### 7.3 Import records (per card, local)

```json
{ "entries" : { "dcim/100canon/img_0001.cr3|25165824|895000000" : "/path/of/the/copy" } }
```

The key is `<path on card, lower-case, "/" separators>|<size in bytes>|<floor(modification time in
Unix seconds / 2)>`. A media file whose key is present is "imported before" and not offered again.
The value is informational only.

## 8. Windows equivalents

| Concern | Mac | Windows |
| --- | --- | --- |
| Language / UI | Swift, SwiftUI | C#, WinUI 3 or WPF |
| Menu bar panel | `MenuBarExtra` (window style) | Notification-area (tray) icon with a flyout, e.g. the H.NotifyIcon library |
| Card inserted / removed | `NSWorkspace` mount notifications | `WM_DEVICECHANGE` (`DBT_DEVICEARRIVAL`) or WMI `Win32_VolumeChangeEvent` |
| Removable media, reader info | DiskArbitration | `DriveInfo.DriveType == Removable`, `Win32_DiskDrive` (model, interface type) |
| Card ID | Volume UUID | Volume serial number (`GetVolumeInformation`) |
| EXIF / video metadata | ImageIO, AVFoundation | MetadataExtractor (NuGet): EXIF, most RAW formats, QuickTime/MP4 |
| SHA-256 | CryptoKit | `System.Security.Cryptography.SHA256` |
| Bypass cache, flush | `F_NOCACHE`, `fsync` | `FILE_FLAG_NO_BUFFERING` or `FILE_FLAG_WRITE_THROUGH`, `FlushFileBuffers` |
| Copy dates | `setAttributes` | `File.SetCreationTimeUtc`, `File.SetLastWriteTimeUtc` |
| Extended attributes | `copyfile(COPYFILE_XATTR)` | Not needed (optionally copy alternate data streams) |
| Move without overwriting | `FileManager.moveItem` | `File.Move(src, dst, overwrite: false)` |
| Notifications | UserNotifications | Windows App SDK app notifications (toast) |
| Start at login | `SMAppService` | MSIX `StartupTask`, or `HKCU\…\Run` |
| Eject | `NSWorkspace.unmountAndEjectDevice` | `CM_Request_Device_Eject` |
| Prevent sleep | `ProcessInfo.beginActivity` | `SetThreadExecutionState(ES_CONTINUOUS \| ES_SYSTEM_REQUIRED)` |
| Folder picker / alerts | `NSOpenPanel`, `NSAlert` | `FolderPicker`, `ContentDialog` |
| Packaging | `.app`, ad-hoc signing | MSIX or installer, code-signing certificate (avoids SmartScreen warnings) |

## 9. Windows-specific rules

- **Names:** apply the name rules of section 2 exactly. The Mac already uses them, so the same
  project gets the same folder name on both systems.
- **Long paths:** project paths can exceed 260 characters. Enable long-path awareness in the app
  manifest, or use `\\?\` paths.
- **Case:** treat names case-insensitively, as on Mac.
- **Shared drives:** format SSDs as **exFAT** to use them on both systems. Windows can't read APFS,
  and macOS can't write NTFS. Expect `.DS_Store` and `._*` files on drives used by a Mac; they are
  metadata and already skipped.
- **Time precision:** NTFS stores 100 ns, exFAT 10 ms, FAT 2 s. The 2-second tolerance covers all
  of them.
- **AutoPlay:** Windows shows its own prompt when a card is inserted. Register the app as an
  AutoPlay handler ("Import with Notch") rather than competing with it.
- **Drive letters change:** never store them. Identify cards by volume serial and projects folders
  by `library.json` (section 7.2).

## 10. Tests to port

The core tests describe the required behaviour. Port them first, then make them pass.

| File | Tests | Covers |
| --- | --- | --- |
| `BackupEngineTests.swift` | 15 | Structure and dates, incremental runs, never overwriting, time-zone shifts, same size with different bytes, numbered names, skipped metadata, empty files, progress, refusing unsafe destinations, cancellation, name rules |
| `ProjectImportTests.swift` | 11 | Classification, sidecar pairing, camera short names, date parsing, Sony XML, folder names, import records, card analysis, end-to-end import into the project layout |
| `YearMigratorTests.swift` | 7 | Moving a year, resuming, strict duplicate checks, leftovers, refusing the same folder, disconnected drives, listing years |
| `ProjectLibraryTests.swift` | 7 | `library.json` without absolute paths, stable IDs, finding a folder at a new mount point, rejecting look-alikes, relative project folders |
