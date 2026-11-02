# SD Backup

A small macOS menu bar app that automatically backs up SD cards when they are inserted.

Insert a card and SD Backup copies everything new on it into a folder on your Mac, verifies each
copy, and sends a notification when it's done. Insert the same card again later and only the new
photos and videos are copied.

## Features

- **Automatic.** Detects cards in the built-in SD slot or any USB card reader, plus any removable
  drive with a camera `DCIM` folder. It can optionally include all removable drives.
- **Incremental.** Each card has its own backup folder. Files that are already backed up are
  skipped.
- **Non-destructive.** Nothing on the card is ever modified or deleted, and nothing in the backup is
  ever overwritten. If a card is reformatted and the camera reuses a name like `IMG_0001.JPG`, the
  new file is saved as `IMG_0001 (1).JPG`.
- **Verified.** Each copy is read back from disk and compared with the original using SHA-256.
  You can turn this off in Settings.
- **Crash-safe.** Files are written to a hidden staging folder and moved into place only after
  they are complete. Pulling the card or quitting mid-backup never leaves half-written files. The
  next insertion picks up where it stopped.
- **Handles time zones.** FAT-formatted cards store local time. If a card's timestamps shift
  (for example after travel or a daylight-saving change), files with the same size are compared
  byte for byte instead of being copied again.
- Checks free space before copying, prevents idle sleep during a backup, can eject the card when
  finished, opens at login, and keeps a log at `~/Library/Logs/SD Backup/backups.log`.

## Requirements

- macOS 13 Ventura or later (Apple silicon or Intel)
- Xcode, or just the Command Line Tools: `xcode-select --install`

## Build and install

```bash
./scripts/build-app.sh --install
```

This compiles the app, puts `SD Backup.app` in `/Applications`, and launches it. An SD card icon
appears in the menu bar. To build without installing, omit `--install`; the app is written to
`build/SD Backup.app`.

### First launch

macOS will ask for two permissions. Allow both:

1. **Notifications**, so you hear when a backup starts and finishes.
2. **Access to files on a removable volume**, which appears the first time a card is inserted.
   Without it the app can't read the card. If you dismissed the prompt, enable it in
   **System Settings › Privacy & Security › Files and Folders › SD Backup › Removable Volumes**.

When the app runs from `/Applications`, it registers itself to open at login the first time it
starts. You can change this in Settings.

> The build is signed ad hoc for your Mac only. macOS ties privacy permissions to the signature,
> so after rebuilding you may be asked for card access again.

## Where backups go

The default backup folder is `~/Pictures/SD Card Backups`. You can change it in Settings, and it
can be on an external drive. Each card gets its own folder that mirrors the card's structure:

```
SD Card Backups/
├── EOS_DIGITAL (3A1F22B0)/
│   └── DCIM/100CANON/IMG_0001.CR3 …
└── Untitled (9C04E1D7)/
    └── PRIVATE/M4ROOT/CLIP/C0001.MP4 …
```

The short code after the name is the card's volume ID. It keeps cards with the same name apart,
since every Canon card is called `EOS_DIGITAL`. Reformatting a card gives it a new ID, so a
reformatted card starts a new folder.

If you delete files from a backup folder, they are copied again the next time that card is
inserted.

## Using it

The SD card icon sits on the right side of the menu bar, next to Wi-Fi, battery and Control
Center. Clicking it opens a panel styled like Control Center:

- **Toggles**: Auto Backup, Verify and Auto Eject. Each round button fills with your accent
  color when it's on.
- **Backups** opens the backup folder. **Settings** opens all options.
- **Status** shows "Ready", or the current backup with a slider-style progress bar, sizes, file
  count, time left and a cancel button.
- **Cards** lists connected cards with when each was last backed up, plus **Back Up** and
  **Eject** buttons.
- **Recent** shows the last few backups. Click one to show it in Finder.

When **Auto Backup** is off, cards are copied only when you click **Back Up**.

Ejecting a card in Finder during a backup stops the backup so the card can be ejected. If macOS
says the disk is still in use, eject it again.

## Development

```bash
swift build          # compile
swift test           # run the backup engine tests (requires full Xcode)
swift run SDBackup   # run without an app bundle (notifications and login item are disabled)
```

To work in Xcode, run `open Package.swift`.

| Path | Purpose |
| --- | --- |
| `Sources/SDBackupCore/BackupEngine.swift` | Scanning, de-duplication, verified copying. UI-independent and unit tested. |
| `Sources/SDBackup/VolumeInspector.swift` | Decides whether a mounted volume is an SD/camera card (DiskArbitration). |
| `Sources/SDBackup/AppModel.swift` | Watches mounts and unmounts, queues and runs backups, keeps history. |
| `Sources/SDBackup/Views/` | Control Center–style menu bar panel and the Settings window (SwiftUI). |
| `Resources/Info.plist` | Bundle settings: menu-bar-only (`LSUIElement`), permission text. |
| `scripts/build-app.sh` | Builds, bundles and ad-hoc signs the `.app`. |

To use your own bundle identifier, change `CFBundleIdentifier` in `Resources/Info.plist`.
