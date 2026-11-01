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

/// Appends a plain-text record of every backup to ~/Library/Logs/SD Backup/backups.log.
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
