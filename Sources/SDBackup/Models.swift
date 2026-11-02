import Foundation
import SDBackupCore

/// The import that is currently copying.
struct ActiveBackup: Identifiable {
    let id = UUID()
    let volume: VolumeInfo
    /// The project name(s) being imported into.
    let title: String
    let startedAt = Date()
    var progress = BackupProgress(phase: .scanning)
    var copyStartedAt: Date?

    var estimatedSecondsRemaining: TimeInterval? {
        Format.remaining(progress, since: copyStartedAt)
    }
}

/// A year folder being moved to another drive.
struct ActiveMigration: Identifiable {
    let id = UUID()
    let year: Int
    let destinationRoot: URL
    let driveName: String
    var progress = BackupProgress(phase: .scanning)
    var copyStartedAt: Date?

    var yearText: String { String(year) }

    var estimatedSecondsRemaining: TimeInterval? {
        Format.remaining(progress, since: copyStartedAt)
    }
}

/// A finished import, shown in the menu and persisted between launches.
struct HistoryEntry: Identifiable, Codable, Equatable {
    enum Outcome: String, Codable {
        case completed
        case completedWithErrors
        case failed
        case cancelled
    }

    var id = UUID()
    /// Project name(s), or the card name when nothing was imported.
    let title: String
    let cardName: String
    let cardID: String
    let date: Date
    let outcome: Outcome
    let filesCopied: Int
    let bytesCopied: Int64
    let filesAlreadyBackedUp: Int
    let failedFiles: Int
    let message: String?
    /// The project folder to reveal in Finder.
    let folderPath: String?
    /// `nil` for entries saved before moving years existed; those are card imports.
    var kind: Kind? = nil

    enum Kind: String, Codable {
        case cardImport
        case migration
    }

    var summary: String {
        if kind == .migration {
            switch outcome {
            case .completed:
                return "\(Format.count(filesCopied, "file")) · \(Format.bytes(bytesCopied)) · to \(cardName)"
            case .completedWithErrors:
                return "\(Format.count(filesCopied, "file")) moved to \(cardName) · \(failedFiles) stayed on this Mac"
            case .failed, .cancelled:
                return message ?? "Move stopped"
            }
        }
        switch outcome {
        case .completed:
            return filesCopied == 0
                ? "Already there · from \(cardName)"
                : "\(Format.count(filesCopied, "file")) · \(Format.bytes(bytesCopied)) · from \(cardName)"
        case .completedWithErrors:
            return "\(Format.count(filesCopied, "file")) copied · \(failedFiles) couldn’t be copied"
        case .failed, .cancelled:
            return message ?? "Import stopped"
        }
    }
}

enum Format {
    static func bytes(_ count: Int64) -> String {
        ByteCountFormatter.string(fromByteCount: count, countStyle: .file)
    }

    static func count(_ number: Int, _ singular: String, _ plural: String? = nil) -> String {
        "\(number.formatted()) \(number == 1 ? singular : (plural ?? singular + "s"))"
    }

    /// Time left, from the copy speed so far.
    static func remaining(_ progress: BackupProgress, since start: Date?) -> TimeInterval? {
        guard let start, progress.bytesProcessed > 0 else { return nil }
        let elapsed = Date().timeIntervalSince(start)
        guard elapsed > 3 else { return nil }
        let bytesPerSecond = Double(progress.bytesProcessed) / elapsed
        return Double(max(0, progress.bytesToCopy - progress.bytesProcessed)) / bytesPerSecond
    }

    static func duration(_ seconds: TimeInterval) -> String {
        let formatter = DateComponentsFormatter()
        formatter.allowedUnits = seconds >= 3600 ? [.hour, .minute] : [.minute, .second]
        formatter.unitsStyle = .abbreviated
        formatter.maximumUnitCount = 2
        return formatter.string(from: max(1, seconds)) ?? ""
    }
}
