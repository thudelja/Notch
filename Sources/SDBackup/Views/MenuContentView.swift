import SDBackupCore
import SwiftUI

/// The panel that drops down from the menu bar icon, styled after Control Center.
struct MenuContentView: View {
    @EnvironmentObject private var model: AppModel
    @EnvironmentObject private var settings: AppSettings

    var body: some View {
        VStack(spacing: ControlCenterMetrics.spacing) {
            HStack(alignment: .top, spacing: ControlCenterMetrics.spacing) {
                toggles
                VStack(spacing: ControlCenterMetrics.spacing) {
                    folderTile
                    settingsTile
                }
                .frame(width: ControlCenterMetrics.columnWidth)
            }
            .fixedSize(horizontal: false, vertical: true)

            Module {
                if let active = model.active {
                    ActiveBackupContent(backup: active)
                } else {
                    IdleStatusContent(autoBackup: settings.autoBackupEnabled)
                }
            }

            cards

            if !model.history.isEmpty {
                recent
            }

            footer
        }
        .padding(ControlCenterMetrics.panelPadding)
        .frame(width: ControlCenterMetrics.panelWidth)
        .onAppear { model.refreshConnectedVolumes() }
    }

    // MARK: - Top row

    private var toggles: some View {
        Module(fillsHeight: true) {
            VStack(alignment: .leading, spacing: 10) {
                Toggle("Auto Backup", isOn: $settings.autoBackupEnabled)
                    .toggleStyle(ControlCenterToggleStyle(symbol: "arrow.triangle.2.circlepath"))
                    .help("Back up cards automatically when they’re inserted")
                Toggle("Verify", isOn: $settings.verifyCopies)
                    .toggleStyle(ControlCenterToggleStyle(symbol: "checkmark.shield.fill"))
                    .help("Check every copy against the original (SHA-256)")
                Toggle("Auto Eject", isOn: $settings.ejectWhenDone)
                    .toggleStyle(ControlCenterToggleStyle(symbol: "eject.fill"))
                    .help("Eject the card when its backup finishes")
            }
        }
        .frame(width: ControlCenterMetrics.columnWidth)
    }

    private var folderTile: some View {
        Button {
            model.revealDestination()
        } label: {
            TileLabel(symbol: "folder.fill", title: "Backups", subtitle: settings.destinationURL.lastPathComponent)
        }
        .buttonStyle(ModuleButtonStyle())
        .help("Open \(settings.abbreviatedDestination)")
    }

    private var settingsTile: some View {
        Button {
            model.openSettings()
        } label: {
            TileLabel(symbol: "gearshape.fill", title: "Settings")
        }
        .buttonStyle(ModuleButtonStyle())
        .keyboardShortcut(",")
    }

    // MARK: - Cards and history

    private var cards: some View {
        Module {
            VStack(alignment: .leading, spacing: 8) {
                ModuleTitle("Cards")
                if model.connectedVolumes.isEmpty {
                    HStack(spacing: 8) {
                        CircleIcon(symbol: "sdcard")
                        Text("No cards inserted")
                            .font(.moduleSubtitle)
                            .foregroundStyle(.secondary)
                    }
                } else {
                    ForEach(model.connectedVolumes) { volume in
                        CardRow(volume: volume)
                    }
                }
            }
        }
    }

    private var recent: some View {
        Module {
            VStack(alignment: .leading, spacing: 8) {
                ModuleTitle("Recent")
                ForEach(Array(model.history.prefix(3))) { entry in
                    Button {
                        model.reveal(entry)
                    } label: {
                        HistoryRow(entry: entry)
                    }
                    .buttonStyle(.plain)
                    .help("Show in Finder")
                }
            }
        }
    }

    private var footer: some View {
        HStack {
            Button("Open Log") { model.openLog() }
            Spacer()
            Button("Quit SD Backup") { model.quit() }
                .keyboardShortcut("q")
        }
        .buttonStyle(.plain)
        .font(.moduleSubtitle)
        .foregroundStyle(.secondary)
        .padding(.horizontal, 4)
    }
}

// MARK: - Status module

private struct IdleStatusContent: View {
    let autoBackup: Bool

    var body: some View {
        HStack(spacing: 10) {
            CircleIcon(
                symbol: autoBackup ? "checkmark" : "pause.fill",
                isOn: true,
                tint: autoBackup ? .green : .orange,
                size: 30
            )
            VStack(alignment: .leading, spacing: 1) {
                Text(autoBackup ? "Ready" : "Auto Backup Is Off")
                    .font(.moduleTitle)
                Text(autoBackup ? "Insert an SD card to back it up." : "Cards are only backed up when you click Back Up.")
                    .font(.moduleSubtitle)
                    .foregroundStyle(.secondary)
                    .fixedSize(horizontal: false, vertical: true)
            }
            Spacer(minLength: 0)
        }
    }
}

private struct ActiveBackupContent: View {
    @EnvironmentObject private var model: AppModel
    let backup: ActiveBackup

    private var progress: BackupProgress { backup.progress }
    private var isCopying: Bool { progress.phase == .copying }

    var body: some View {
        VStack(alignment: .leading, spacing: 8) {
            HStack(spacing: 8) {
                VStack(alignment: .leading, spacing: 1) {
                    Text("Backing Up")
                        .font(.moduleTitle)
                    Text(backup.volume.name)
                        .font(.moduleSubtitle)
                        .foregroundStyle(.secondary)
                        .lineLimit(1)
                }
                Spacer(minLength: 8)
                Button {
                    model.cancelBackup()
                } label: {
                    CircleIcon(symbol: "xmark", size: 22)
                }
                .buttonStyle(.plain)
                .help("Cancel backup")
                .accessibilityLabel("Cancel backup")
            }

            CapsuleProgress(fraction: isCopying ? progress.fractionCompleted : nil, symbol: "sdcard.fill")

            if !isCopying {
                caption("Checking what’s new on the card…")
            } else if progress.filesToCopy == 0 {
                caption("Everything is already backed up.")
            } else {
                HStack {
                    caption("\(Format.bytes(progress.bytesProcessed)) of \(Format.bytes(progress.bytesToCopy))")
                    Spacer()
                    caption(backup.estimatedSecondsRemaining.map { "\(Format.duration($0)) left" } ?? "")
                }
                HStack {
                    caption("\(progress.filesProcessed.formatted()) of \(Format.count(progress.filesToCopy, "file"))")
                    Spacer()
                    caption("\(Int((progress.fractionCompleted * 100).rounded()))%")
                }
                if let file = progress.currentFile {
                    Text(file)
                        .font(.system(size: 10))
                        .foregroundStyle(.tertiary)
                        .lineLimit(1)
                        .truncationMode(.middle)
                }
            }

            if !model.queue.isEmpty {
                caption("Up next: " + model.queue.map(\.name).joined(separator: ", "))
                    .lineLimit(1)
            }
        }
    }

    private func caption(_ text: String) -> some View {
        Text(text)
            .font(.moduleSubtitle)
            .monospacedDigit()
            .foregroundStyle(.secondary)
    }
}

// MARK: - Rows

private struct CardRow: View {
    @EnvironmentObject private var model: AppModel
    let volume: VolumeInfo

    private var isActive: Bool { model.active?.volume.id == volume.id }
    private var isQueued: Bool { model.queue.contains { $0.id == volume.id } }

    var body: some View {
        HStack(spacing: 8) {
            CircleIcon(symbol: "sdcard.fill", isOn: isActive)
            VStack(alignment: .leading, spacing: 0) {
                Text(volume.name)
                    .font(.moduleTitle)
                    .lineLimit(1)
                Text(detail)
                    .font(.moduleSubtitle)
                    .foregroundStyle(.secondary)
                    .lineLimit(1)
            }
            Spacer(minLength: 4)
            if isActive || isQueued {
                Text(isActive ? "Backing up" : "Queued")
                    .font(.moduleSubtitle)
                    .foregroundStyle(.secondary)
            } else {
                Button("Back Up") { model.enqueue(volume) }
                    .buttonStyle(PillButtonStyle())
            }
            Button {
                model.eject(volume)
            } label: {
                CircleIcon(symbol: "eject.fill", size: 22)
            }
            .buttonStyle(.plain)
            .disabled(isActive)
            .opacity(isActive ? 0.4 : 1)
            .help("Eject “\(volume.name)”")
            .accessibilityLabel("Eject \(volume.name)")
        }
    }

    private var detail: String {
        if let last = model.lastBackup(of: volume) {
            return "Backed up \(last.date.formatted(.relative(presentation: .named)))"
        }
        let capacity = volume.capacity.map { Format.bytes($0) + " · " } ?? ""
        return capacity + "Not backed up yet"
    }
}

private struct HistoryRow: View {
    let entry: HistoryEntry

    var body: some View {
        HStack(spacing: 8) {
            Image(systemName: icon)
                .font(.system(size: 15))
                .foregroundStyle(color)
                .frame(width: 26)
            VStack(alignment: .leading, spacing: 0) {
                HStack {
                    Text(entry.cardName)
                        .font(.moduleTitle)
                        .lineLimit(1)
                    Spacer()
                    Text(entry.date.formatted(.relative(presentation: .named)))
                        .font(.moduleSubtitle)
                        .foregroundStyle(.secondary)
                }
                Text(entry.summary)
                    .font(.moduleSubtitle)
                    .foregroundStyle(.secondary)
                    .lineLimit(2)
                    .fixedSize(horizontal: false, vertical: true)
            }
        }
        .contentShape(Rectangle())
    }

    private var icon: String {
        switch entry.outcome {
        case .completed: return "checkmark.circle.fill"
        case .completedWithErrors: return "exclamationmark.triangle.fill"
        case .failed: return "xmark.octagon.fill"
        case .cancelled: return "stop.circle.fill"
        }
    }

    private var color: Color {
        switch entry.outcome {
        case .completed: return .green
        case .completedWithErrors: return .orange
        case .failed: return .red
        case .cancelled: return .secondary
        }
    }
}
