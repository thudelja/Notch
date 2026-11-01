import AppKit
import SDBackupCore

/// Owns the app's state: connected cards, the backup queue, the running backup and the history.
@MainActor
final class AppModel: ObservableObject {
    let settings: AppSettings
    let log: BackupLog

    @Published private(set) var connectedVolumes: [VolumeInfo] = []
    @Published private(set) var queue: [VolumeInfo] = []
    @Published private(set) var active: ActiveBackup? = nil
    @Published private(set) var history: [HistoryEntry] = []
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

    var isBusy: Bool { active != nil }

    private let notifier = Notifier()
    private let settingsWindow: SettingsWindowController
    private var observers: [NSObjectProtocol] = []
    private var backupTask: Task<Void, Never>?
    private var stopReason: String?

    private static let historyKey = "backupHistory"
    private static let historyLimit = 25
    private static let loginItemConfiguredKey = "loginItemConfigured"

    init() {
        settings = AppSettings()
        log = BackupLog()
        settingsWindow = SettingsWindowController()
        launchAtLogin = LoginItem.isEnabled
        history = Self.loadHistory()

        configureLoginItemOnFirstLaunch()
        notifier.setUp()
        observeVolumes()
        refreshConnectedVolumes()

        // Cards that were already inserted when the app started.
        if settings.autoBackupEnabled {
            for volume in connectedVolumes {
                enqueue(volume)
            }
        }
    }

    // MARK: - Volumes

    func refreshConnectedVolumes() {
        connectedVolumes = VolumeInspector.mountedVolumes().filter { isEligible($0) }
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
        refreshConnectedVolumes()
        guard settings.autoBackupEnabled,
              let volume = connectedVolumes.first(where: { $0.id == url.path })
        else { return }
        enqueue(volume)
    }

    /// The user ejected the card in Finder: stop copying so the eject can go through.
    private func volumeWillUnmount(_ url: URL) {
        queue.removeAll { $0.id == url.path }
        if active?.volume.id == url.path {
            stopReason = "Stopped because the card was ejected. Insert it again to finish the backup."
            backupTask?.cancel()
        }
    }

    private func volumeDidUnmount(_ url: URL) {
        connectedVolumes.removeAll { $0.id == url.path }
        queue.removeAll { $0.id == url.path }
        if active?.volume.id == url.path {
            stopReason = stopReason ?? "The card was removed before the backup finished. Insert it again to finish."
            backupTask?.cancel()
        }
    }

    // MARK: - Backups

    /// Queues a backup of `volume`. Used both automatically and from "Back Up" in the menu.
    func enqueue(_ volume: VolumeInfo) {
        guard active?.volume.id != volume.id, !queue.contains(where: { $0.id == volume.id }) else { return }
        queue.append(volume)
        startNextBackup()
    }

    func cancelBackup() {
        stopReason = "Cancelled. Files copied so far were kept."
        backupTask?.cancel()
    }

    private func startNextBackup() {
        while active == nil, !queue.isEmpty {
            let volume = queue.removeFirst()
            if FileManager.default.fileExists(atPath: volume.url.path) {
                start(volume)
            }
        }
    }

    private func start(_ volume: VolumeInfo) {
        let backup = ActiveBackup(volume: volume)
        let backupID = backup.id
        let source = volume.url
        let destination = settings.destinationURL
        let folderName = volume.backupFolderName
        let options = BackupOptions(verifyCopies: settings.verifyCopies)

        active = backup
        stopReason = nil
        log.write("Started backing up “\(volume.name)” to \(destination.path)")
        notify("Backing up “\(volume.name)”", "Keep the card inserted until the backup is finished.")

        backupTask = Task.detached(priority: .userInitiated) { [self] in
            let activity = ProcessInfo.processInfo.beginActivity(
                options: [.userInitiated, .idleSystemSleepDisabled],
                reason: "Backing up an SD card"
            )
            defer { ProcessInfo.processInfo.endActivity(activity) }

            let outcome: Result<BackupResult, Error>
            do {
                let result = try BackupEngine(options: options).run(
                    source: source,
                    destinationRoot: destination,
                    cardFolderName: folderName
                ) { progress in
                    Task { @MainActor in self.update(progress, backupID: backupID) }
                }
                outcome = .success(result)
            } catch {
                outcome = .failure(error)
            }
            await self.finish(backupID: backupID, volume: volume, outcome: outcome)
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

    private func finish(backupID: UUID, volume: VolumeInfo, outcome: Result<BackupResult, Error>) {
        guard active?.id == backupID else { return }
        active = nil
        backupTask = nil

        switch outcome {
        case .success(let result):
            backupSucceeded(result, volume: volume)
        case .failure(let error):
            backupFailed(error, volume: volume)
        }
        stopReason = nil
        startNextBackup()
    }

    private func backupSucceeded(_ result: BackupResult, volume: VolumeInfo) {
        let copied = "\(Format.count(result.filesCopied, "new file")) (\(Format.bytes(result.bytesCopied)))"
        var logLines = [
            "Finished “\(volume.name)”: \(copied) copied, \(result.filesAlreadyBackedUp) already backed up, "
                + "\(result.failures.count) failed → \(result.destination.path)",
        ]
        logLines += result.failures.prefix(500).map { "    FAILED \($0.relativePath): \($0.message)" }
        log.write(logLines)

        if result.isComplete {
            record(.completed, volume: volume, result: result)
            notify(
                "Backup complete",
                result.filesCopied == 0
                    ? "Everything on “\(volume.name)” was already backed up."
                    : "Copied \(copied) from “\(volume.name)”."
            )
            if settings.ejectWhenDone {
                eject(volume, announce: true)
            }
        } else {
            record(.completedWithErrors, volume: volume, result: result)
            notify(
                "Backup finished with problems",
                "Copied \(copied) from “\(volume.name)”, but \(Format.count(result.failures.count, "file")) couldn’t be copied. Open the log for details."
            )
        }
    }

    private func backupFailed(_ error: Error, volume: VolumeInfo) {
        let outcome: HistoryEntry.Outcome
        let message: String
        if error is CancellationError {
            outcome = .cancelled
            message = stopReason ?? "Cancelled. Files copied so far were kept."
        } else if (error as? BackupError) == .sourceDisappeared {
            outcome = .failed
            message = "The card was removed before the backup finished. Insert it again to finish."
        } else {
            outcome = .failed
            message = error.localizedDescription
        }

        log.write("Backup of “\(volume.name)” stopped: \(message)")
        record(outcome, volume: volume, result: nil, message: message)
        notify(outcome == .cancelled ? "Backup stopped" : "Backup failed", message)
    }

    // MARK: - History

    private func record(_ outcome: HistoryEntry.Outcome, volume: VolumeInfo, result: BackupResult?, message: String? = nil) {
        let folder = result?.destination
            ?? settings.destinationURL.appendingPathComponent(BackupEngine.sanitizedFolderName(volume.backupFolderName))
        let entry = HistoryEntry(
            cardName: volume.name,
            date: Date(),
            outcome: outcome,
            filesCopied: result?.filesCopied ?? 0,
            bytesCopied: result?.bytesCopied ?? 0,
            filesAlreadyBackedUp: result?.filesAlreadyBackedUp ?? 0,
            failedFiles: result?.failures.count ?? 0,
            message: message,
            folderPath: folder.path
        )
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
        let fileManager = FileManager.default
        _ = NSWorkspace.shared.open(fileManager.fileExists(atPath: url.path) ? url : url.deletingLastPathComponent())
    }

    func reveal(_ entry: HistoryEntry) {
        let url = URL(fileURLWithPath: entry.folderPath, isDirectory: true)
        if FileManager.default.fileExists(atPath: url.path) {
            NSWorkspace.shared.activateFileViewerSelecting([url])
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
        panel.title = "Choose Backup Folder"
        panel.prompt = "Choose"
        panel.canChooseFiles = false
        panel.canChooseDirectories = true
        panel.canCreateDirectories = true
        panel.allowsMultipleSelection = false
        panel.directoryURL = settings.destinationURL
        AppActivation.bringToFront()
        guard panel.runModal() == .OK, let url = panel.url else { return }
        settings.destinationPath = url.path
        refreshConnectedVolumes()
    }

    func quit() {
        if isBusy {
            let alert = NSAlert()
            alert.messageText = "A backup is in progress"
            alert.informativeText = "If you quit now, files copied so far are kept and the rest are copied the next time the card is inserted."
            alert.addButton(withTitle: "Keep Backing Up")
            alert.addButton(withTitle: "Quit")
            AppActivation.bringToFront()
            guard alert.runModal() == .alertSecondButtonReturn else { return }
            backupTask?.cancel()
        }
        NSApp.terminate(nil)
    }

    // MARK: - Helpers

    private func notify(_ title: String, _ body: String) {
        guard settings.notificationsEnabled else { return }
        notifier.post(title: title, body: body)
    }

    /// An auto-backup app only works while it's running, so start at login by default once it's
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
