import AppKit
import ServiceManagement

enum LoginItem {
    static var isEnabled: Bool { SMAppService.mainApp.status == .enabled }

    static func setEnabled(_ enabled: Bool) throws {
        if enabled {
            try SMAppService.mainApp.register()
        } else {
            try SMAppService.mainApp.unregister()
        }
    }
}

/// Until 1.2, Notch used the identifier of its first builds, called SD Backup, which had no icon. macOS
/// kept showing that identifier's blank icon in notifications, so Notch now has its own identifier, and
/// this copies the settings saved under the old one (projects folder, camera names, history, …) once.
enum DefaultsMigration {
    static let previousIdentifier = "com.sdbackup.SDBackup"
    private static let doneKey = "migratedFromSDBackup"
    /// Not copied: the new identifier sets up its own login item.
    private static let skippedKeys: Set<String> = ["loginItemConfigured", "loginItemConfigured.notch"]

    static func run(defaults: UserDefaults = .standard) {
        guard let identifier = Bundle.main.bundleIdentifier, identifier != previousIdentifier,
              !defaults.bool(forKey: doneKey)
        else { return }
        let existing = defaults.persistentDomain(forName: identifier) ?? [:]
        let previous = defaults.persistentDomain(forName: previousIdentifier) ?? [:]
        for (key, value) in previous where existing[key] == nil && !skippedKeys.contains(key) {
            defaults.set(value, forKey: key)
        }
        defaults.set(true, forKey: doneKey)
    }
}

@MainActor
enum AppActivation {
    /// Menu bar apps are never frontmost on their own; call before showing a window or panel.
    static func bringToFront() {
        if #available(macOS 14, *) {
            NSApp.activate()
        } else {
            NSApp.activate(ignoringOtherApps: true)
        }
    }
}

/// Appends a plain-text record of every backup to ~/Library/Logs/SD Backup/backups.log (the folder
/// keeps the app's earlier name, like the Application Support folder).
final class BackupLog {
    let url: URL
    private let queue = DispatchQueue(label: "SDBackup.log")
    private let formatter: DateFormatter = {
        let formatter = DateFormatter()
        formatter.locale = Locale(identifier: "en_US_POSIX")
        formatter.dateFormat = "yyyy-MM-dd HH:mm:ss"
        return formatter
    }()

    init() {
        let directory = FileManager.default.homeDirectoryForCurrentUser
            .appendingPathComponent("Library/Logs/SD Backup", isDirectory: true)
        try? FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true)
        url = directory.appendingPathComponent("backups.log")
    }

    func write(_ lines: String...) {
        write(lines)
    }

    func write(_ lines: [String]) {
        let stamp = formatter.string(from: Date())
        let data = Data(lines.map { "[\(stamp)] \($0)\n" }.joined().utf8)
        let url = self.url
        queue.async {
            if let handle = try? FileHandle(forWritingTo: url) {
                defer { try? handle.close() }
                _ = try? handle.seekToEnd()
                try? handle.write(contentsOf: data)
            } else {
                try? data.write(to: url)
            }
        }
    }
}
