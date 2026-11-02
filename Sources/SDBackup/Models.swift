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
        guard let copyStartedAt, progress.bytesProcessed > 0 else { return nil }
        let elapsed = Date().timeIntervalSince(copyStartedAt)
        guard elapsed > 3 else { return nil }
        let bytesPerSecond = Double(progress.bytesProcessed) / elapsed
        return Double(max(0, progress.bytesToCopy - progress.bytesProcessed)) / bytesPerSecond
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

    var summary: String {
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

    static func duration(_ seconds: TimeInterval) -> String {
        let formatter = DateComponentsFormatter()
        formatter.allowedUnits = seconds >= 3600 ? [.hour, .minute] : [.minute, .second]
        formatter.unitsStyle = .abbreviated
        formatter.maximumUnitCount = 2
        return formatter.string(from: max(1, seconds)) ?? ""
    }
}
