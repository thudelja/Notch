import AppKit
import SDBackupCore

/// A confirmed import waiting to copy or copying.
struct ImportJob: Identifiable {
    let id = UUID()
    let volume: VolumeInfo
    let plan: ImportPlan
    let projects: [ProjectInfo]
    /// The folder shown in Finder from the history.
    let primaryFolder: URL?

    var title: String {
        switch projects.count {
        case 0: return volume.name
        case 1: return projects[0].name
        default: return "\(projects[0].name) + \(projects.count - 1) more"
        }
    }
}

/// Owns the app's state and the import flow:
///
/// 1. A card is inserted → it is read in the background (shoot days, cameras, what's new).
/// 2. The user is notified and picks a project for each shoot day in the menu bar panel.
/// 3. The files are copied into the project folders, verified, and recorded so they are not
///    offered again.
@MainActor
final class AppModel: ObservableObject {
    struct AnalysisProgress: Equatable {
        var done: Int
        var total: Int
    }

    enum CardStatus: Equatable {
        case idle
        case reading
        case needsProject
        case queued
        case importing
    }

    let settings: AppSettings
    let library: LibraryStore
    let log: BackupLog

    @Published private(set) var connectedVolumes: [VolumeInfo] = []
    @Published private(set) var analyzing: VolumeInfo? = nil
    @Published private(set) var analysisProgress: AnalysisProgress? = nil
    @Published private(set) var drafts: [ImportDraft] = []
    @Published private(set) var active: ActiveBackup? = nil
    @Published private(set) var importQueue: [ImportJob] = []
    @Published private(set) var history: [HistoryEntry] = []
    @Published private(set) var migration: ActiveMigration? = nil
    @Published private var analysisQueue: [VolumeInfo] = []
    @Published var launchAtLogin: Bool {
        didSet {
            guard launchAtLogin != LoginItem.isEnabled else { return }
            do {
                try LoginItem.setEnabled(launchAtLogin)
            } catch {
                log.write("Couldn’t change the login item: \(error.localizedDescription)")
                launchAtLogin = LoginItem.isEnabled
            }
        }
    }

    var isBusy: Bool { active != nil || analyzing != nil || migration != nil }

    private let notifier = Notifier()
    private let settingsWindow: SettingsWindowController
    private var observers: [NSObjectProtocol] = []
    private var analysisTask: Task<Void, Never>?
    private var copyTask: Task<Void, Never>?
    private var stopReason: String?
    private var migrationTask: Task<Void, Never>?
    private var migrationStopReason: String?

    private static let historyKey = "importHistory"
    private static let historyLimit = 25
    /// Renamed when the app became Notch (a new app bundle), so the renamed app adds its own login item.
    private static let loginItemConfiguredKey = "loginItemConfigured.notch"

    init() {
        DefaultsMigration.run()
        settings = AppSettings()
        library = LibraryStore()
        log = BackupLog()
        settingsWindow = SettingsWindowController()
        launchAtLogin = LoginItem.isEnabled
        history = Self.loadHistory()

        configureLoginItemOnFirstLaunch()
        notifier.setUp()
        library.library(at: settings.destinationURL)
        observeVolumes()
        refreshConnectedVolumes()

        // Cards that were already inserted when the app started.
        if settings.autoBackupEnabled {
            for volume in connectedVolumes {
                prepareImport(volume)
            }
        }
    }

    // MARK: - Volumes

    func refreshConnectedVolumes() {
        connectedVolumes = VolumeInspector.mountedVolumes().filter { isEligible($0) }
    }

    func status(of volume: VolumeInfo) -> CardStatus {
        if active?.volume.id == volume.id { return .importing }
        if importQueue.contains(where: { $0.volume.id == volume.id }) { return .queued }
        if drafts.contains(where: { $0.volume.id == volume.id }) { return .needsProject }
        if analyzing?.id == volume.id || analysisQueue.contains(where: { $0.id == volume.id }) { return .reading }
        return .idle
    }

    private func isEligible(_ volume: VolumeInfo) -> Bool {
        guard volume.isRemovableMedia, !destinationIsOn(volume) else { return false }
        switch settings.detection {
        case .memoryCards: return volume.looksLikeMemoryCard
        case .anyRemovable: return true
        }
    }

    private func destinationIsOn(_ volume: VolumeInfo) -> Bool {
        let destination = settings.destinationURL.standardizedFileURL.path
        let root = volume.url.standardizedFileURL.path
        return destination == root || destination.hasPrefix(root.hasSuffix("/") ? root : root + "/")
    }

    private func observeVolumes() {
        let center = NSWorkspace.shared.notificationCenter
        observers.append(center.addObserver(forName: NSWorkspace.didMountNotification, object: nil, queue: .main) { [weak self] note in
            guard let self, let url = note.userInfo?[NSWorkspace.volumeURLUserInfoKey] as? URL else { return }
            Task { @MainActor in self.volumeDidMount(url) }
        })
        observers.append(center.addObserver(forName: NSWorkspace.willUnmountNotification, object: nil, queue: .main) { [weak self] note in
            guard let self, let url = note.userInfo?[NSWorkspace.volumeURLUserInfoKey] as? URL else { return }
            Task { @MainActor in self.volumeWillUnmount(url) }
        })
        observers.append(center.addObserver(forName: NSWorkspace.didUnmountNotification, object: nil, queue: .main) { [weak self] note in
            guard let self, let url = note.userInfo?[NSWorkspace.volumeURLUserInfoKey] as? URL else { return }
            Task { @MainActor in self.volumeDidUnmount(url) }
        })
    }

    private func volumeDidMount(_ url: URL) {
        library.refreshLocations() // A drive with moved projects may have been connected.
        refreshConnectedVolumes()
        guard settings.autoBackupEnabled,
              let volume = connectedVolumes.first(where: { $0.id == url.path })
        else { return }
        prepareImport(volume)
    }

    /// The user ejected the card in Finder: stop reading or copying so the eject can go through.
    private func volumeWillUnmount(_ url: URL) {
        stopWork(on: url.path, reason: "Stopped because the card was ejected. Insert it again to finish the import.")
    }

    private func volumeDidUnmount(_ url: URL) {
        connectedVolumes.removeAll { $0.id == url.path }
        library.refreshLocations()
        stopWork(on: url.path, reason: "The card was removed before the import finished. Insert it again to finish.")
    }

    private func stopWork(on volumeID: String, reason: String) {
        analysisQueue.removeAll { $0.id == volumeID }
        importQueue.removeAll { $0.volume.id == volumeID }
        drafts.removeAll { $0.volume.id == volumeID }
        if analyzing?.id == volumeID {
            analysisTask?.cancel()
        }
        if active?.volume.id == volumeID {
            stopReason = stopReason ?? reason
            copyTask?.cancel()
        }
    }

    // MARK: - Step 1: read the card

    /// Reads the card and then asks which project its files belong to. Called automatically when
    /// a card is inserted (if Auto Import is on) and from "Import" in the menu.
    func prepareImport(_ volume: VolumeInfo) {
        guard status(of: volume) == .idle else { return }
        analysisQueue.append(volume)
        startNextAnalysis()
    }

    func cancelAnalysis() {
        analysisTask?.cancel()
    }

    private func startNextAnalysis() {
        guard analyzing == nil else { return }
        while !analysisQueue.isEmpty {
            let volume = analysisQueue.removeFirst()
            if FileManager.default.fileExists(atPath: volume.url.path) {
                analyze(volume)
                return
            }
        }
    }

    private func analyze(_ volume: VolumeInfo) {
        analyzing = volume
        analysisProgress = nil
        let ledger = library.ledger(forCard: volume.cardID)
        let source = volume.url
        let volumeID = volume.id

        analysisTask = Task.detached(priority: .userInitiated) { [self] in
            let outcome: Result<CardAnalysis, Error>
            do {
                let analysis = try await CardAnalyzer().analyze(
                    source: source,
                    isAlreadyImported: { ledger.contains($0) },
                    progress: { done, total in
                        guard done % 20 == 0 || done == total else { return }
                        Task { @MainActor in self.updateAnalysisProgress(done: done, total: total, volumeID: volumeID) }
                    }
                )
                outcome = .success(analysis)
            } catch {
                outcome = .failure(error)
            }
            await self.analysisFinished(volume, outcome: outcome)
        }
    }

    private func updateAnalysisProgress(done: Int, total: Int, volumeID: String) {
        guard analyzing?.id == volumeID else { return }
        analysisProgress = AnalysisProgress(done: done, total: total)
    }

    private func analysisFinished(_ volume: VolumeInfo, outcome: Result<CardAnalysis, Error>) {
        guard analyzing?.id == volume.id else { return }
        analyzing = nil
        analysisProgress = nil
        analysisTask = nil

        switch outcome {
        case .success(let analysis) where analysis.isEmpty:
            let detail = analysis.alreadyImported > 0
                ? "All \(Format.count(analysis.alreadyImported, "file")) on it were imported before."
                : "It has no photos, videos or audio."
            log.write("“\(volume.name)”: nothing new. \(detail)")
            notify("Nothing new on “\(volume.name)”", detail)
            if settings.ejectWhenDone, analysis.alreadyImported > 0 {
                eject(volume, announce: true)
            }
        case .success(let analysis):
            let draft = makeDraft(for: volume, analysis: analysis)
            drafts.append(draft)
            log.write("Read “\(volume.name)”: \(analysis.items.count) new files on \(draft.days.count) shoot day(s), \(analysis.alreadyImported) imported before")
            notify(
                "“\(volume.name)” is ready to import",
                "\(Format.count(draft.days.count, "shoot day")), \(Format.count(analysis.items.count, "new file")). Click the SD card icon in the menu bar to choose the project."
            )
        case .failure(let error):
            guard !(error is CancellationError) else { break }
            let message = error.localizedDescription
            log.write("Couldn’t read “\(volume.name)”: \(message)")
            record(.failed, title: volume.name, volume: volume, result: nil, folder: nil, message: message)
            notify("Couldn’t read “\(volume.name)”", message)
        }
        startNextAnalysis()
    }

    private func makeDraft(for volume: VolumeInfo, analysis: CardAnalysis) -> ImportDraft {
        library.refreshLocations()
        var names: [String: String] = [:]
        for model in analysis.cameraModels {
            names[model] = library.cameraName(forModel: model, make: analysis.make(forModel: model))
        }
        if analysis.hasUnknownCamera {
            names[""] = library.cameraName(forCard: volume.cardID)
                ?? (analysis.cameraModels.count == 1 ? names[analysis.cameraModels[0]] : nil)
                ?? "Camera"
        }
        return ImportDraft(
            volume: volume,
            analysis: analysis,
            recentProjects: library.recentProjects(limit: 3),
            cameraNames: names
        )
    }

    // MARK: - Step 2: the user picks projects

    /// "Not Now": forget the answers; the card can be imported later from the menu.
    func dismiss(_ draft: ImportDraft) {
        drafts.removeAll { $0.id == draft.id }
    }

    func startImport(_ draft: ImportDraft) {
        guard draft.isReady else { return }
        library.refreshLocations()
        guard let projects = draft.resolvedProjects(store: library, root: settings.destinationURL) else {
            showAlert(
                "The projects folder isn’t available",
                "\(settings.abbreviatedDestination) can’t be used. If it’s on an external drive, connect it, or choose another folder in Settings."
            )
            return
        }
        if let offline = projects.values.first(where: { $0.rootPath.isEmpty }) {
            showAlert("Connect the drive", "“\(offline.name)” is on a drive that isn’t connected. Connect it and click Import again.")
            return
        }
        let plan = ProjectLayout.plan(items: draft.analysis.items, projects: projects) { draft.cameraName(for: $0) }
        library.rememberCameraNames(draft.cameraNames, cardID: draft.volume.cardID)
        drafts.removeAll { $0.id == draft.id }
        guard !plan.items.isEmpty else { return }

        let used = Array(Set(projects.values)).sorted { $0.startDay < $1.startDay }
        var primaryFolder: URL?
        if let first = used.first {
            let hasPhotos = draft.analysis.items.contains { $0.kind == .photo && projects[$0.day]?.id == first.id }
            primaryFolder = hasPhotos ? ProjectLayout.photoFolder(for: first) : ProjectLayout.videoFolder(for: first)
        }

        importQueue.append(ImportJob(volume: draft.volume, plan: plan, projects: used, primaryFolder: primaryFolder))
        startNextImport()
    }

    // MARK: - Step 3: copy

    func cancelImport() {
        stopReason = "Cancelled. Files copied so far were kept."
        copyTask?.cancel()
    }

    private func startNextImport() {
        // Imports wait while a year is being moved, in case they belong to that year.
        while active == nil, migration == nil, !importQueue.isEmpty {
            let job = importQueue.removeFirst()
            if FileManager.default.fileExists(atPath: job.volume.url.path) {
                run(job)
            } else {
                notify("Couldn’t import “\(job.volume.name)”", "The card was removed. Insert it again to import.")
            }
        }
    }

    private func run(_ job: ImportJob) {
        let backup = ActiveBackup(volume: job.volume, title: job.title)
        let backupID = backup.id
        // Projects that were moved to another drive are imported there. If that drive isn't
        // connected, the copy stops straight away with "connect the drive".
        let roots = Set(job.projects.map(\.rootPath))
        let destination = roots.count == 1
            ? URL(fileURLWithPath: roots.first!, isDirectory: true)
            : settings.destinationURL
        let options = BackupOptions(verifyCopies: settings.verifyCopies)

        active = backup
        stopReason = nil
        log.write("Importing “\(job.volume.name)” into \(job.title): \(Format.count(job.plan.fileCount, "file")), \(Format.bytes(job.plan.byteCount))")

        copyTask = Task.detached(priority: .userInitiated) { [self] in
            let activity = ProcessInfo.processInfo.beginActivity(
                options: [.userInitiated, .idleSystemSleepDisabled],
                reason: "Importing an SD card"
            )
            defer { ProcessInfo.processInfo.endActivity(activity) }

            let outcome: Result<BackupResult, Error>
            do {
                let result = try BackupEngine(options: options).copy(
                    job.plan.items,
                    from: job.volume.url,
                    to: destination,
                    createFolders: job.plan.foldersToCreate,
                    progress: { progress in
                        Task { @MainActor in self.update(progress, backupID: backupID) }
                    }
                )
                outcome = .success(result)
            } catch {
                outcome = .failure(error)
            }
            await self.importFinished(job, backupID: backupID, outcome: outcome)
        }
    }

    private func update(_ progress: BackupProgress, backupID: UUID) {
        guard var backup = active, backup.id == backupID else { return }
        if progress.phase == .copying, backup.copyStartedAt == nil {
            backup.copyStartedAt = Date()
        }
        backup.progress = progress
        active = backup
    }

    private func importFinished(_ job: ImportJob, backupID: UUID, outcome: Result<BackupResult, Error>) {
        guard active?.id == backupID else { return }
        active = nil
        copyTask = nil

        switch outcome {
        case .success(let result):
            var ledger = library.ledger(forCard: job.volume.cardID)
            for file in result.processedFiles {
                ledger.record(file.source, destination: file.destination)
            }
            library.saveLedger(ledger, forCard: job.volume.cardID)
            library.markUsed(job.projects)
            importSucceeded(job, result: result)
        case .failure(let error):
            importFailed(job, error: error)
        }
        stopReason = nil
        startNextImport()
    }

    private func importSucceeded(_ job: ImportJob, result: BackupResult) {
        let copied = "\(Format.count(result.filesCopied, "file")) (\(Format.bytes(result.bytesCopied)))"
        var lines = [
            "Imported “\(job.volume.name)” into \(job.title): \(copied) copied, "
                + "\(result.filesAlreadyBackedUp) already there, \(result.failures.count) failed",
        ]
        lines += job.projects.map { "    \($0.name): \(ProjectLayout.photoFolder(for: $0).path) | \(ProjectLayout.videoFolder(for: $0).path)" }
        lines += result.failures.prefix(500).map { "    FAILED \($0.relativePath): \($0.message)" }
        log.write(lines)
        handOffPhotos(result)

        if result.isComplete {
            record(.completed, title: job.title, volume: job.volume, result: result, folder: job.primaryFolder)
            notify("Import complete", "Copied \(copied) from “\(job.volume.name)” into \(job.title).")
            let moreForThisCard = importQueue.contains { $0.volume.id == job.volume.id }
                || drafts.contains { $0.volume.id == job.volume.id }
            if settings.ejectWhenDone, !moreForThisCard {
                eject(job.volume, announce: true)
            }
        } else {
            record(.completedWithErrors, title: job.title, volume: job.volume, result: result, folder: job.primaryFolder)
            notify(
                "Import finished with problems",
                "Copied \(copied) into \(job.title), but \(Format.count(result.failures.count, "file")) couldn’t be copied. Open the log for details."
            )
        }
    }

    /// Opens the folders that received new photos in the app chosen in Settings, which then shows
    /// its own import window. Never affects the import itself.
    private func handOffPhotos(_ result: BackupResult) {
        let app = settings.photoApp
        let folders = PhotoHandoff.folders(from: result)
        guard app != .none, !folders.isEmpty else { return }
        let photos = Format.count(PhotoHandoff.photoCount(in: result), "new photo")
        Task { @MainActor in
            switch await PhotoAppLauncher.open(folders, in: app) {
            case .opened:
                log.write("Opened \(photos) in \(app.title): " + folders.map(\.path).joined(separator: ", "))
            case .notInstalled:
                log.write("\(app.title) isn’t installed, so the \(photos) weren’t opened in it.")
                notify("\(app.title) not found", "Notch couldn’t find \(app.title) on this Mac, so the new photos weren’t opened in it. You can choose another app in Settings.")
            case .failed(let message):
                log.write("Couldn’t open the \(photos) in \(app.title): \(message)")
            }
        }
    }

    private func importFailed(_ job: ImportJob, error: Error) {
        let outcome: HistoryEntry.Outcome
        let message: String
        if error is CancellationError {
            outcome = .cancelled
            message = stopReason ?? "Cancelled. Files copied so far were kept."
        } else if (error as? BackupError) == .sourceDisappeared {
            outcome = .failed
            message = "The card was removed before the import finished. Insert it again to finish."
        } else {
            outcome = .failed
            message = error.localizedDescription
        }

        log.write("Import of “\(job.volume.name)” into \(job.title) stopped: \(message)")
        record(outcome, title: job.title, volume: job.volume, result: nil, folder: job.primaryFolder, message: message)
        notify(outcome == .cancelled ? "Import stopped" : "Import failed", message)
    }

    // MARK: - History

    private func record(
        _ outcome: HistoryEntry.Outcome,
        title: String,
        volume: VolumeInfo,
        result: BackupResult?,
        folder: URL?,
        message: String? = nil
    ) {
        let entry = HistoryEntry(
            title: title,
            cardName: volume.name,
            cardID: volume.cardID,
            date: Date(),
            outcome: outcome,
            filesCopied: result?.filesCopied ?? 0,
            bytesCopied: result?.bytesCopied ?? 0,
            filesAlreadyBackedUp: result?.filesAlreadyBackedUp ?? 0,
            failedFiles: result?.failures.count ?? 0,
            message: message,
            folderPath: folder?.path,
            kind: .cardImport
        )
        appendHistory(entry)
    }

    private func appendHistory(_ entry: HistoryEntry) {
        history.insert(entry, at: 0)
        if history.count > Self.historyLimit {
            history.removeLast(history.count - Self.historyLimit)
        }
        if let data = try? JSONEncoder().encode(history) {
            UserDefaults.standard.set(data, forKey: Self.historyKey)
        }
    }

    private static func loadHistory() -> [HistoryEntry] {
        guard let data = UserDefaults.standard.data(forKey: historyKey),
              let entries = try? JSONDecoder().decode([HistoryEntry].self, from: data)
        else { return [] }
        return entries
    }

    /// The most recent successful import from this card.
    func lastImport(of volume: VolumeInfo) -> HistoryEntry? {
        history.first { entry in
            entry.cardID == volume.cardID && (entry.outcome == .completed || entry.outcome == .completedWithErrors)
        }
    }

    func clearHistory() {
        history = []
        UserDefaults.standard.removeObject(forKey: Self.historyKey)
    }

    // MARK: - Actions

    func eject(_ volume: VolumeInfo, announce: Bool = false) {
        Task { @MainActor in
            // Spotlight or Finder can briefly keep a freshly used card busy, so retry a few times.
            for attempt in 1...3 {
                do {
                    try NSWorkspace.shared.unmountAndEjectDevice(at: volume.url)
                    if announce {
                        self.notify("“\(volume.name)” ejected", "It’s safe to remove the card.")
                    }
                    return
                } catch {
                    if attempt == 3 {
                        self.notify("Couldn’t eject “\(volume.name)”", error.localizedDescription)
                        return
                    }
                    try? await Task.sleep(nanoseconds: 2_000_000_000)
                }
            }
        }
    }

    func revealDestination() {
        let url = settings.destinationURL
        _ = NSWorkspace.shared.open(FileManager.default.fileExists(atPath: url.path) ? url : url.deletingLastPathComponent())
    }

    func reveal(_ entry: HistoryEntry) {
        if let path = entry.folderPath, FileManager.default.fileExists(atPath: path) {
            NSWorkspace.shared.activateFileViewerSelecting([URL(fileURLWithPath: path, isDirectory: true)])
        } else {
            revealDestination()
        }
    }

    func openLog() {
        if !FileManager.default.fileExists(atPath: log.url.path) {
            try? Data().write(to: log.url)
        }
        _ = NSWorkspace.shared.open(log.url)
    }

    func openSettings() {
        settingsWindow.show(model: self)
    }

    func chooseDestination() {
        let panel = NSOpenPanel()
        panel.title = "Choose Projects Folder"
        panel.message = "Year folders (2026, 2027, …) with your projects inside are created here."
        panel.prompt = "Choose"
        panel.canChooseFiles = false
        panel.canChooseDirectories = true
        panel.canCreateDirectories = true
        panel.allowsMultipleSelection = false
        panel.directoryURL = settings.destinationURL
        AppActivation.bringToFront()
        guard panel.runModal() == .OK, let url = panel.url else { return }
        settings.destinationPath = url.path
        library.library(at: url)
        refreshConnectedVolumes()
    }

    func forgetImportedFiles() {
        let alert = NSAlert()
        alert.messageText = "Forget which files were imported?"
        alert.informativeText = "Every file on every card will be offered for import again. Files that are already in a project folder are still skipped when you pick the same project."
        alert.addButton(withTitle: "Cancel")
        alert.addButton(withTitle: "Forget")
        AppActivation.bringToFront()
        guard alert.runModal() == .alertSecondButtonReturn else { return }
        library.forgetImports()
    }

    func forgetCameraNames() {
        library.forgetCameraNames()
    }

    func quit() {
        if isBusy {
            let alert = NSAlert()
            if migration != nil {
                alert.messageText = "A year is being moved"
                alert.informativeText = "If you quit now, files already moved stay on the drive and the rest stay on this Mac. Move the year again to finish."
            } else {
                alert.messageText = active != nil ? "An import is in progress" : "A card is being read"
                alert.informativeText = "If you quit now, files copied so far are kept, and you can import the rest the next time the card is inserted."
            }
            alert.addButton(withTitle: "Keep Going")
            alert.addButton(withTitle: "Quit")
            AppActivation.bringToFront()
            guard alert.runModal() == .alertSecondButtonReturn else { return }
            copyTask?.cancel()
            analysisTask?.cancel()
            migrationTask?.cancel()
        }
        NSApp.terminate(nil)
    }

    // MARK: - Moving a year to another drive

    /// Year folders in the projects folder that can be moved.
    func availableYears() -> [Int] {
        YearMigrator.years(in: settings.destinationURL)
    }

    // MARK: - Folder structure

    /// The folder structure new projects get in the projects folder.
    func folderStructure() -> FolderStructure {
        library.structure(at: settings.destinationURL)
    }

    /// Saves the folder structure for new projects. Shows an alert and returns false when the
    /// projects folder can't be written.
    @discardableResult
    func saveFolderStructure(_ structure: FolderStructure) -> Bool {
        guard library.setStructure(structure, at: settings.destinationURL) else {
            showAlert(
                "The folder structure wasn’t saved",
                "\(settings.abbreviatedDestination) can’t be written. If it’s on an external drive, connect it and try again."
            )
            return false
        }
        log.write(["Folder structure for new projects in \(settings.destinationURL.path):"] + structure.summary.map { "    " + $0 })
        return true
    }

    /// Asks where to move `year`, confirms, then moves it in the background.
    func moveYear(_ year: Int) {
        guard migration == nil else { return }
        let yearText = String(year)
        guard active == nil, importQueue.isEmpty else {
            showAlert("Wait for the import to finish", "\(yearText) can be moved once the current import is done.")
            return
        }

        let panel = NSOpenPanel()
        panel.title = "Move \(yearText) to Another Drive"
        panel.message = "Choose a folder on the drive, for example a Projects folder on your SSD. \(yearText) is moved into it with the same folder structure."
        panel.prompt = "Move Here"
        panel.canChooseFiles = false
        panel.canChooseDirectories = true
        panel.canCreateDirectories = true
        panel.allowsMultipleSelection = false
        panel.directoryURL = URL(fileURLWithPath: settings.lastMigrationPath ?? "/Volumes", isDirectory: true)
        AppActivation.bringToFront()
        guard panel.runModal() == .OK, let destinationRoot = panel.url else { return }

        let sourceRoot = settings.destinationURL
        if destinationRoot.standardizedFileURL.path == sourceRoot.standardizedFileURL.path {
            showAlert("Choose another folder", "\(yearText) is already in that folder. Pick a folder on the other drive.")
            return
        }

        let driveName = (try? destinationRoot.resourceValues(forKeys: [.volumeLocalizedNameKey]))?.volumeLocalizedName
            ?? destinationRoot.lastPathComponent
        let target = (destinationRoot.appendingPathComponent(yearText).path as NSString).abbreviatingWithTildeInPath
        let confirm = NSAlert()
        confirm.alertStyle = .warning
        confirm.messageText = "Move \(yearText) to “\(driveName)”?"
        confirm.informativeText = """
        Everything in \(yearText), including photos, videos and editing projects, is copied to \(target). \
        Each file is checked against the original and then deleted from this Mac.

        Projects from \(yearText) will open from “\(driveName)” from now on. New projects still go to this Mac. \
        Editing projects that link to these files (Premiere Pro, DaVinci Resolve, …) may ask you to relink them once.
        """
        confirm.addButton(withTitle: "Move")
        confirm.addButton(withTitle: "Cancel")
        AppActivation.bringToFront()
        guard confirm.runModal() == .alertFirstButtonReturn else { return }

        settings.lastMigrationPath = destinationRoot.path
        startMigration(year: year, from: sourceRoot, to: destinationRoot, driveName: driveName)
    }

    func cancelMigration() {
        migrationStopReason = "Stopped. Files already moved are on the drive and the rest are still on this Mac. Move the year again to finish."
        migrationTask?.cancel()
    }

    private func startMigration(year: Int, from sourceRoot: URL, to destinationRoot: URL, driveName: String) {
        let current = ActiveMigration(year: year, destinationRoot: destinationRoot, driveName: driveName)
        let migrationID = current.id
        migration = current
        migrationStopReason = nil
        log.write("Moving \(current.yearText) from \(sourceRoot.path) to \(destinationRoot.path)")
        notify("Moving \(current.yearText) to “\(driveName)”", "Keep the drive connected until it’s finished.")

        migrationTask = Task.detached(priority: .userInitiated) { [self] in
            let activity = ProcessInfo.processInfo.beginActivity(
                options: [.userInitiated, .idleSystemSleepDisabled],
                reason: "Moving a year of projects to another drive"
            )
            defer { ProcessInfo.processInfo.endActivity(activity) }

            let outcome: Result<MigrationResult, Error>
            do {
                let result = try YearMigrator().moveYear(year, from: sourceRoot, to: destinationRoot) { progress in
                    Task { @MainActor in self.updateMigration(progress, migrationID: migrationID) }
                }
                outcome = .success(result)
            } catch {
                outcome = .failure(error)
            }
            await self.migrationFinished(current, sourceRoot: sourceRoot, outcome: outcome)
        }
    }

    private func updateMigration(_ progress: BackupProgress, migrationID: UUID) {
        guard var current = migration, current.id == migrationID else { return }
        if progress.phase == .copying, current.copyStartedAt == nil {
            current.copyStartedAt = Date()
        }
        current.progress = progress
        migration = current
    }

    private func migrationFinished(_ finished: ActiveMigration, sourceRoot: URL, outcome: Result<MigrationResult, Error>) {
        guard migration?.id == finished.id else { return }
        migration = nil
        migrationTask = nil
        let year = finished.yearText

        switch outcome {
        case .success(let result):
            let relinked = library.moveProjects(year: finished.year, from: sourceRoot, to: finished.destinationRoot)
            let movedCount = result.filesMoved + result.duplicatesRemoved
            let moved = "\(Format.count(movedCount, "file")) (\(Format.bytes(result.bytesMoved)))"
            var lines = [
                "Moved \(year) to \(result.destination.path): \(result.filesMoved) copied, "
                    + "\(result.duplicatesRemoved) already there, \(result.failures.count) failed, "
                    + "\(result.itemsLeftBehind) left on this Mac; \(relinked) project(s) now open from \(finished.driveName)",
            ]
            lines += result.failures.prefix(500).map { "    FAILED \($0.relativePath): \($0.message)" }
            log.write(lines)
            recordMigration(result.isComplete ? .completed : .completedWithErrors, finished, result: result)
            if result.isComplete {
                notify("\(year) moved to “\(finished.driveName)”", "Moved \(moved). Projects from \(year) now open from the drive.")
            } else {
                let stayed = result.failures.count + result.itemsLeftBehind
                notify(
                    "\(year) moved, with exceptions",
                    "Moved \(moved). \(Format.count(stayed, "item")) stayed on this Mac. Open the log for details."
                )
            }
        case .failure(let error):
            let cancelled = error is CancellationError
            let message = cancelled
                ? migrationStopReason ?? "Stopped. Move the year again to finish."
                : error.localizedDescription
            log.write("Moving \(year) stopped: \(message)")
            recordMigration(cancelled ? .cancelled : .failed, finished, result: nil, message: message)
            notify(cancelled ? "Move stopped" : "Couldn’t move \(year)", message)
        }
        migrationStopReason = nil
        startNextImport()
    }

    private func recordMigration(_ outcome: HistoryEntry.Outcome, _ migration: ActiveMigration, result: MigrationResult?, message: String? = nil) {
        appendHistory(HistoryEntry(
            title: "Moved \(migration.yearText)",
            cardName: migration.driveName,
            cardID: "",
            date: Date(),
            outcome: outcome,
            filesCopied: result.map { $0.filesMoved + $0.duplicatesRemoved } ?? 0,
            bytesCopied: result?.bytesMoved ?? 0,
            filesAlreadyBackedUp: result?.duplicatesRemoved ?? 0,
            failedFiles: result.map { $0.failures.count + $0.itemsLeftBehind } ?? 0,
            message: message,
            folderPath: (result?.destination ?? migration.destinationRoot).path,
            kind: .migration
        ))
    }

    // MARK: - Helpers

    private func showAlert(_ title: String, _ message: String) {
        let alert = NSAlert()
        alert.messageText = title
        alert.informativeText = message
        AppActivation.bringToFront()
        _ = alert.runModal()
    }

    private func notify(_ title: String, _ body: String) {
        guard settings.notificationsEnabled else { return }
        notifier.post(title: title, body: body)
    }

    /// An auto-import app only works while it's running, so start at login by default once it's
    /// installed in /Applications. The user can turn this off in Settings.
    private func configureLoginItemOnFirstLaunch() {
        let defaults = UserDefaults.standard
        guard !defaults.bool(forKey: Self.loginItemConfiguredKey),
              Bundle.main.bundlePath.hasPrefix("/Applications/")
        else { return }
        defaults.set(true, forKey: Self.loginItemConfiguredKey)
        do {
            try LoginItem.setEnabled(true)
        } catch {
            log.write("Couldn’t add the login item: \(error.localizedDescription)")
        }
        launchAtLogin = LoginItem.isEnabled
    }
}
