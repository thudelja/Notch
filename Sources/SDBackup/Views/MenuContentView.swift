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

            if let draft = model.drafts.first {
                ImportDraftView(draft: draft, queued: model.drafts.count - 1)
                    .id(draft.id)
            }

            if let active = model.active {
                Module { ActiveImportContent(backup: active) }
            }
            if let volume = model.analyzing {
                Module { ReadingCardContent(volume: volume, progress: model.analysisProgress) }
            }
            if let migration = model.migration {
                Module { MovingYearContent(migration: migration) }
            }
            if model.active == nil, model.analyzing == nil, model.migration == nil, model.drafts.isEmpty {
                Module { IdleStatusContent(autoImport: settings.autoBackupEnabled) }
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
                Toggle("Auto Import", isOn: $settings.autoBackupEnabled)
                    .toggleStyle(ControlCenterToggleStyle(symbol: "arrow.triangle.2.circlepath"))
                    .help("Read cards automatically when they’re inserted and ask which project they belong to")
                Toggle("Verify", isOn: $settings.verifyCopies)
                    .toggleStyle(ControlCenterToggleStyle(symbol: "checkmark.shield.fill"))
                    .help("Check every copy against the original (SHA-256)")
                Toggle("Auto Eject", isOn: $settings.ejectWhenDone)
                    .toggleStyle(ControlCenterToggleStyle(symbol: "eject.fill"))
                    .help("Eject the card when its import finishes")
            }
        }
        .frame(width: ControlCenterMetrics.columnWidth)
    }

    private var folderTile: some View {
        Button {
            model.revealDestination()
        } label: {
            TileLabel(symbol: "folder.fill", title: String(localized: "Projects"), subtitle: settings.destinationURL.lastPathComponent)
        }
        .buttonStyle(ModuleButtonStyle())
        .help("Open \(settings.abbreviatedDestination)")
    }

    private var settingsTile: some View {
        Button {
            model.openSettings()
        } label: {
            TileLabel(symbol: "gearshape.fill", title: String(localized: "Settings"))
        }
        .buttonStyle(ModuleButtonStyle())
        .keyboardShortcut(",")
    }

    // MARK: - Cards and history

    private var cards: some View {
        Module {
            VStack(alignment: .leading, spacing: 8) {
                ModuleTitle(String(localized: "Cards"))
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
                ModuleTitle(String(localized: "Recent"))
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
            Button("Quit Notch") { model.quit() }
                .keyboardShortcut("q")
        }
        .buttonStyle(.plain)
        .font(.moduleSubtitle)
        .foregroundStyle(.secondary)
        .padding(.horizontal, 4)
    }
}

// MARK: - Import: pick a project for each shoot day

private struct ImportDraftView: View {
    @EnvironmentObject private var model: AppModel
    @ObservedObject var draft: ImportDraft
    /// Other cards waiting after this one.
    let queued: Int

    var body: some View {
        Module {
            VStack(alignment: .leading, spacing: 12) {
                HStack(alignment: .top, spacing: 8) {
                    CircleIcon(symbol: "sdcard.fill", isOn: true)
                    VStack(alignment: .leading, spacing: 1) {
                        Text("Import “\(draft.volume.name)”")
                            .font(.moduleTitle)
                            .lineLimit(1)
                        Text(summary)
                            .font(.moduleSubtitle)
                            .foregroundStyle(.secondary)
                            .lineLimit(1)
                    }
                    Spacer(minLength: 4)
                    if queued > 0 {
                        Text(verbatim: Plural.text(queued, "+{0} waiting", "+{0} waiting"))
                            .font(.moduleSubtitle)
                            .foregroundStyle(.secondary)
                    }
                }

                if draft.days.count > 3 {
                    ScrollView {
                        dayRows.padding(.trailing, 8)
                    }
                    .frame(height: 260)
                } else {
                    dayRows
                }

                if !draft.cameraKeys.isEmpty {
                    cameraFolders
                }

                if let reason = draft.blockingReason {
                    Text(reason)
                        .font(.moduleSubtitle)
                        .foregroundStyle(.secondary)
                        .fixedSize(horizontal: false, vertical: true)
                }

                HStack {
                    Button("Not Now") { model.dismiss(draft) }
                        .buttonStyle(PillButtonStyle())
                        .help("Close without importing. Use Import on the card later.")
                    Spacer()
                    Button("Import") { model.startImport(draft) }
                        .buttonStyle(.borderedProminent)
                        .controlSize(.small)
                        .keyboardShortcut(.defaultAction)
                        .disabled(!draft.isReady)
                }
            }
        }
    }

    private var summary: String {
        var parts = [
            Format.count(draft.days.count, "shoot day"),
            Format.count(draft.analysis.items.count, "new file"),
            Format.bytes(draft.totalBytes),
        ]
        if draft.analysis.alreadyImported > 0 {
            parts.append(Plural.text(draft.analysis.alreadyImported, "{0:N0} imported before", "{0:N0} imported before"))
        }
        return parts.joined(separator: " · ")
    }

    private var dayRows: some View {
        VStack(alignment: .leading, spacing: 12) {
            ForEach(draft.days) { summary in
                DayRow(draft: draft, summary: summary)
            }
        }
    }

    private var cameraFolders: some View {
        VStack(alignment: .leading, spacing: 6) {
            Text("Camera folders")
                .font(.moduleSubtitle.weight(.semibold))
                .foregroundStyle(.secondary)
            ForEach(draft.cameraKeys, id: \.self) { key in
                HStack(spacing: 8) {
                    VStack(alignment: .leading, spacing: 0) {
                        Text(draft.cameraLabel(key))
                            .font(.moduleSubtitle)
                            .lineLimit(1)
                        Text(Format.count(draft.cameraFileCounts[key] ?? 0, "file"))
                            .font(.system(size: 10))
                            .foregroundStyle(.tertiary)
                    }
                    Spacer(minLength: 6)
                    TextField("Folder name", text: cameraBinding(key))
                        .textFieldStyle(.roundedBorder)
                        .font(.moduleSubtitle)
                        .frame(width: 110)
                }
            }
        }
    }

    private func cameraBinding(_ key: String) -> Binding<String> {
        Binding(
            get: { draft.cameraNames[key] ?? "" },
            set: { draft.cameraNames[key] = $0 }
        )
    }
}

private struct DayRow: View {
    @EnvironmentObject private var model: AppModel
    @ObservedObject var draft: ImportDraft
    let summary: DaySummary

    var body: some View {
        VStack(alignment: .leading, spacing: 5) {
            HStack(alignment: .firstTextBaseline, spacing: 6) {
                Text(summary.day.folderDate)
                    .font(.moduleTitle)
                    .monospacedDigit()
                Text(summary.day.date().formatted(.dateTime.weekday(.abbreviated)))
                    .font(.moduleSubtitle)
                    .foregroundStyle(.secondary)
                Spacer(minLength: 4)
                Text(counts)
                    .font(.moduleSubtitle)
                    .foregroundStyle(.secondary)
                    .lineLimit(1)
            }

            Picker("Project for \(summary.day.folderDate)", selection: choice) {
                ForEach(draft.options(for: summary.day)) { option in
                    Text(verbatim: option.title).tag(option.choice)
                }
            }
            .labelsHidden()
            .pickerStyle(.menu)

            switch draft.choice(for: summary.day) {
            case .newShoot:
                TextField("Multi-day shoot, e.g. AMD Koledar 2026", text: shootName)
                    .textFieldStyle(.roundedBorder)
                TextField("Name of this day’s project, e.g. the model", text: name)
                    .textFieldStyle(.roundedBorder)
            case .newInShoot(let id):
                TextField("New project name", text: name)
                    .textFieldStyle(.roundedBorder)
                if let shoot = draft.shoot(id) {
                    let doneTitle = String(localized: "“\(shoot.name)” is finished after this import")
                    if draft.finishedShoots.contains(id) {
                        Label(doneTitle, systemImage: "checkmark.circle.fill")
                            .font(.caption)
                            .foregroundStyle(.secondary)
                    } else {
                        HoldToFinishButton(title: String(localized: "Hold to finish “\(shoot.name)”"), doneTitle: doneTitle) {
                            model.finishShoot(id)
                            draft.finishedShoots.insert(id)
                        }
                        .id(id)
                    }
                }
            case .newProject:
                TextField("New project name", text: name)
                    .textFieldStyle(.roundedBorder)
                if draft.hasPhotos { placeRow(.photo) }
                if draft.hasVideos { placeRow(.video) }
            default:
                EmptyView()
            }
        }
    }

    /// "Photos → Projects  [Change…]": where a new project's photos or videos go.
    private func placeRow(_ branch: FolderStructure.Branch) -> some View {
        let chosen = draft.folder(for: branch, day: summary.day)
        let place = chosen?.lastPathComponent ?? model.defaultRoot(branch).lastPathComponent
        return HStack(spacing: 6) {
            Image(systemName: branch == .photo ? "photo" : "video")
                .foregroundStyle(.secondary)
                .frame(width: 16)
            Text(branch == .photo ? String(localized: "Photos → \(place)") : String(localized: "Videos → \(place)"))
                .font(.moduleSubtitle)
                .foregroundStyle(chosen == nil ? .secondary : .primary)
                .lineLimit(1)
                .truncationMode(.middle)
            Spacer(minLength: 4)
            if chosen != nil {
                Button {
                    draft.setFolder(nil, for: branch, day: summary.day)
                } label: {
                    Image(systemName: "arrow.uturn.backward")
                }
                .buttonStyle(.borderless)
                .help(String(localized: "Back to the default folder"))
            }
            Button("Change…") {
                // After the menu has closed, so the folder panel can take focus.
                let day = summary.day
                DispatchQueue.main.async { [model, draft] in model.chooseFolder(for: branch, day: day, in: draft) }
            }
            .controlSize(.small)
        }
    }

    private var counts: String {
        var parts: [String] = []
        if summary.photos > 0 { parts.append(Format.count(summary.photos, "photo")) }
        if summary.videos > 0 { parts.append(Format.count(summary.videos, "video")) }
        if summary.audio > 0 { parts.append(Format.count(summary.audio, "audio", "audio")) }
        parts.append(Format.bytes(summary.bytes))
        return parts.joined(separator: " · ")
    }

    private var choice: Binding<DayChoice> {
        Binding(
            get: { draft.choice(for: summary.day) },
            set: { draft.setChoice($0, for: summary.day) }
        )
    }

    private var shootName: Binding<String> {
        Binding(
            get: { draft.shootNames[summary.day] ?? "" },
            set: { draft.shootNames[summary.day] = $0 }
        )
    }

    private var name: Binding<String> {
        Binding(
            get: { draft.projectNames[summary.day] ?? "" },
            set: { draft.projectNames[summary.day] = $0 }
        )
    }
}

// MARK: - Status

private struct IdleStatusContent: View {
    let autoImport: Bool

    var body: some View {
        HStack(spacing: 10) {
            CircleIcon(
                symbol: autoImport ? "checkmark" : "pause.fill",
                isOn: true,
                tint: autoImport ? .green : .orange,
                size: 30
            )
            VStack(alignment: .leading, spacing: 1) {
                Text(autoImport ? String(localized: "Ready") : String(localized: "Auto Import Is Off"))
                    .font(.moduleTitle)
                Text(autoImport ? String(localized: "Insert an SD card to import it into a project.") : String(localized: "Click Import on a card below to import it."))
                    .font(.moduleSubtitle)
                    .foregroundStyle(.secondary)
                    .fixedSize(horizontal: false, vertical: true)
            }
            Spacer(minLength: 0)
        }
    }
}

private struct ReadingCardContent: View {
    @EnvironmentObject private var model: AppModel
    let volume: VolumeInfo
    let progress: AppModel.AnalysisProgress?

    var body: some View {
        VStack(alignment: .leading, spacing: 8) {
            StatusHeader(title: String(localized: "Reading Card"), subtitle: volume.name) { model.cancelAnalysis() }
            CapsuleProgress(fraction: fraction, symbol: "sdcard.fill")
            Text(caption)
                .font(.moduleSubtitle)
                .monospacedDigit()
                .foregroundStyle(.secondary)
        }
    }

    private var fraction: Double? {
        guard let progress, progress.total > 0 else { return nil }
        return Double(progress.done) / Double(progress.total)
    }

    private var caption: String {
        guard let progress, progress.total > 0 else { return String(localized: "Looking for photos and videos…") }
        return String(localized: "Finding shoot dates and cameras") + " · "
            + Plural.text(progress.total, "{1:N0} of {0:N0} file", "{1:N0} of {0:N0} files", progress.done.formatted())
    }
}

private struct ActiveImportContent: View {
    @EnvironmentObject private var model: AppModel
    let backup: ActiveBackup

    private var progress: BackupProgress { backup.progress }
    private var isCopying: Bool { progress.phase == .copying }

    var body: some View {
        VStack(alignment: .leading, spacing: 8) {
            StatusHeader(title: String(localized: "Importing"), subtitle: "\(backup.volume.name) → \(backup.title)") { model.cancelImport() }

            CapsuleProgress(fraction: isCopying ? progress.fractionCompleted : nil, symbol: "sdcard.fill")

            if !isCopying {
                caption(String(localized: "Checking the project folders…"))
            } else if progress.filesToCopy == 0 {
                caption(String(localized: "Everything is already in the project."))
            } else {
                HStack {
                    caption(String(localized: "\(Format.bytes(progress.bytesProcessed)) of \(Format.bytes(progress.bytesToCopy))"))
                    Spacer()
                    caption(backup.estimatedSecondsRemaining.map { String(localized: "\(Format.duration($0)) left") } ?? "")
                }
                HStack {
                    caption(Plural.text(progress.filesToCopy, "{1:N0} of {0:N0} file", "{1:N0} of {0:N0} files", progress.filesProcessed.formatted()))
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

            if !model.importQueue.isEmpty {
                caption(String(localized: "Up next: \(model.importQueue.map(\.title).joined(separator: ", "))"))
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

private struct MovingYearContent: View {
    @EnvironmentObject private var model: AppModel
    let migration: ActiveMigration

    private var progress: BackupProgress { migration.progress }

    var body: some View {
        VStack(alignment: .leading, spacing: 8) {
            StatusHeader(title: String(localized: "Moving \(migration.yearText)"), subtitle: String(localized: "To “\(migration.driveName)”")) { model.cancelMigration() }
            CapsuleProgress(
                fraction: progress.phase == .copying ? progress.fractionCompleted : nil,
                symbol: "externaldrive.fill"
            )
            if progress.phase == .scanning {
                caption(String(localized: "Checking what’s already on the drive…"))
            } else {
                HStack {
                    caption(String(localized: "\(Format.bytes(progress.bytesProcessed)) of \(Format.bytes(progress.bytesToCopy))"))
                    Spacer()
                    caption(migration.estimatedSecondsRemaining.map { String(localized: "\(Format.duration($0)) left") } ?? "")
                }
                caption(String(localized: "Each file is deleted from this Mac once its copy is verified."))
            }
        }
    }

    private func caption(_ text: String) -> some View {
        Text(text)
            .font(.moduleSubtitle)
            .monospacedDigit()
            .foregroundStyle(.secondary)
            .fixedSize(horizontal: false, vertical: true)
    }
}

/// Title, subtitle and a round cancel button.
private struct StatusHeader: View {
    let title: String
    let subtitle: String
    let cancel: () -> Void

    var body: some View {
        HStack(spacing: 8) {
            VStack(alignment: .leading, spacing: 1) {
                Text(title)
                    .font(.moduleTitle)
                Text(subtitle)
                    .font(.moduleSubtitle)
                    .foregroundStyle(.secondary)
                    .lineLimit(1)
            }
            Spacer(minLength: 8)
            Button(action: cancel) {
                CircleIcon(symbol: "xmark", size: 22)
            }
            .buttonStyle(.plain)
            .help("Cancel")
            .accessibilityLabel("Cancel")
        }
    }
}

// MARK: - Rows

private struct CardRow: View {
    @EnvironmentObject private var model: AppModel
    let volume: VolumeInfo

    var body: some View {
        let status = model.status(of: volume)
        HStack(spacing: 8) {
            CircleIcon(symbol: "sdcard.fill", isOn: status != .idle)
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
            switch status {
            case .idle:
                Button("Import") { model.prepareImport(volume) }
                    .buttonStyle(PillButtonStyle())
            case .reading:
                statusText(String(localized: "Reading…"))
            case .needsProject:
                statusText(String(localized: "Choose project"))
            case .queued:
                statusText(String(localized: "Queued"))
            case .importing:
                statusText(String(localized: "Importing"))
            }
            Button {
                model.eject(volume)
            } label: {
                CircleIcon(symbol: "eject.fill", size: 22)
            }
            .buttonStyle(.plain)
            .disabled(status == .importing)
            .opacity(status == .importing ? 0.4 : 1)
            .help("Eject “\(volume.name)”")
            .accessibilityLabel("Eject \(volume.name)")
        }
    }

    private func statusText(_ text: String) -> some View {
        Text(text)
            .font(.moduleSubtitle)
            .foregroundStyle(.secondary)
    }

    private var detail: String {
        if let last = model.lastImport(of: volume) {
            return String(localized: "Imported \(last.date.formatted(.relative(presentation: .named)))")
        }
        let capacity = volume.capacity.map { Format.bytes($0) + " · " } ?? ""
        return capacity + String(localized: "Not imported yet")
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
                    Text(entry.title)
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
        case .completed: return entry.kind == .migration ? "externaldrive.fill.badge.checkmark" : "checkmark.circle.fill"
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
