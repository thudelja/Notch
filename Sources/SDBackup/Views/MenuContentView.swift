import SDBackupCore
import SwiftUI

/// The panel shown when clicking the menu bar icon.
struct MenuContentView: View {
    @EnvironmentObject private var model: AppModel
    @EnvironmentObject private var settings: AppSettings

    private var idleCards: [VolumeInfo] {
        model.connectedVolumes.filter { $0.id != model.active?.volume.id }
    }

    var body: some View {
        VStack(alignment: .leading, spacing: 12) {
            header
            Divider()
            status
            if !idleCards.isEmpty {
                cards
            }
            if !model.history.isEmpty {
                recent
            }
            Divider()
            footer
        }
        .padding(14)
        .frame(width: 340)
        .onAppear { model.refreshConnectedVolumes() }
    }

    // MARK: - Sections

    private var header: some View {
        HStack {
            Text("SD Backup")
                .font(.headline)
            Spacer()
            Toggle(isOn: $settings.autoBackupEnabled) {
                Text("Auto")
                    .font(.caption)
                    .foregroundStyle(.secondary)
            }
            .toggleStyle(.switch)
            .controlSize(.mini)
            .help("Back up cards automatically when they’re inserted")
        }
    }

    @ViewBuilder
    private var status: some View {
        if let active = model.active {
            ActiveBackupView(backup: active)
        } else {
            HStack(spacing: 10) {
                Image(systemName: settings.autoBackupEnabled ? "checkmark.circle.fill" : "pause.circle.fill")
                    .font(.title2)
                    .foregroundStyle(settings.autoBackupEnabled ? Color.green : Color.orange)
                VStack(alignment: .leading, spacing: 2) {
                    Text(settings.autoBackupEnabled ? "Ready" : "Automatic backup is off")
                        .font(.subheadline.weight(.semibold))
                    Text(settings.autoBackupEnabled
                        ? "Insert an SD card and it will be backed up."
                        : "Use “Back Up” below to copy a card.")
                        .font(.caption)
                        .foregroundStyle(.secondary)
                }
            }
        }
        if !model.queue.isEmpty {
            Text("Up next: " + model.queue.map(\.name).joined(separator: ", "))
                .font(.caption)
                .foregroundStyle(.secondary)
        }
    }

    private var cards: some View {
        VStack(alignment: .leading, spacing: 6) {
            SectionTitle("Connected cards")
            ForEach(idleCards) { volume in
                HStack(spacing: 8) {
                    Image(systemName: "sdcard")
                        .foregroundStyle(.secondary)
                    VStack(alignment: .leading, spacing: 1) {
                        Text(volume.name)
                            .lineLimit(1)
                        if let capacity = volume.capacity {
                            Text(Format.bytes(capacity))
                                .font(.caption)
                                .foregroundStyle(.secondary)
                        }
                    }
                    Spacer()
                    Button("Back Up") { model.enqueue(volume) }
                        .disabled(model.queue.contains(volume))
                    Button {
                        model.eject(volume)
                    } label: {
                        Image(systemName: "eject")
                    }
                    .help("Eject “\(volume.name)”")
                }
                .controlSize(.small)
            }
        }
    }

    private var recent: some View {
        VStack(alignment: .leading, spacing: 6) {
            SectionTitle("Recent backups")
            ForEach(Array(model.history.prefix(4))) { entry in
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

    private var footer: some View {
        VStack(alignment: .leading, spacing: 10) {
            Button {
                model.revealDestination()
            } label: {
                Label {
                    Text(settings.abbreviatedDestination)
                        .lineLimit(1)
                        .truncationMode(.middle)
                } icon: {
                    Image(systemName: "folder")
                }
                .font(.caption)
                .foregroundStyle(.secondary)
            }
            .buttonStyle(.plain)
            .help("Open the backup folder")

            HStack {
                Button("Settings…") { model.openSettings() }
                    .keyboardShortcut(",")
                Spacer()
                Button("Quit") { model.quit() }
                    .keyboardShortcut("q")
            }
            .controlSize(.small)
        }
    }
}

// MARK: - Components

private struct SectionTitle: View {
    let title: String

    init(_ title: String) {
        self.title = title
    }

    var body: some View {
        Text(title)
            .font(.caption.weight(.semibold))
            .foregroundStyle(.secondary)
    }
}

private struct ActiveBackupView: View {
    @EnvironmentObject private var model: AppModel
    let backup: ActiveBackup

    var body: some View {
        VStack(alignment: .leading, spacing: 6) {
            HStack {
                Text("Backing up “\(backup.volume.name)”")
                    .font(.subheadline.weight(.semibold))
                    .lineLimit(1)
                Spacer()
                Button("Cancel") { model.cancelBackup() }
                    .controlSize(.small)
            }

            switch backup.progress.phase {
            case .scanning:
                ProgressView()
                    .progressViewStyle(.linear)
                Text("Checking what’s new on the card…")
                    .font(.caption)
                    .foregroundStyle(.secondary)
            case .copying:
                ProgressView(value: backup.progress.fractionCompleted)
                Text(detail)
                    .font(.caption)
                    .monospacedDigit()
                    .foregroundStyle(.secondary)
                if let file = backup.progress.currentFile {
                    Text(file)
                        .font(.caption2)
                        .foregroundStyle(.tertiary)
                        .lineLimit(1)
                        .truncationMode(.middle)
                }
            }
        }
    }

    private var detail: String {
        let progress = backup.progress
        if progress.filesToCopy == 0 {
            return "Everything is already backed up."
        }
        var parts = [
            "\(progress.filesProcessed.formatted()) of \(Format.count(progress.filesToCopy, "file"))",
            "\(Format.bytes(progress.bytesProcessed)) of \(Format.bytes(progress.bytesToCopy))",
        ]
        if let remaining = backup.estimatedSecondsRemaining {
            parts.append("\(Format.duration(remaining)) left")
        }
        return parts.joined(separator: " · ")
    }
}

private struct HistoryRow: View {
    let entry: HistoryEntry

    var body: some View {
        HStack(alignment: .firstTextBaseline, spacing: 8) {
            Image(systemName: icon)
                .foregroundStyle(color)
            VStack(alignment: .leading, spacing: 1) {
                HStack {
                    Text(entry.cardName)
                        .lineLimit(1)
                    Spacer()
                    Text(entry.date.formatted(.relative(presentation: .named)))
                        .font(.caption)
                        .foregroundStyle(.secondary)
                }
                Text(entry.summary)
                    .font(.caption)
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
