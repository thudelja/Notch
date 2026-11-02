import Foundation

/// A regular file found on the card.
public struct SourceFile: Sendable, Equatable {
    public let url: URL
    /// Path relative to the card root with "/" separators, e.g. "DCIM/100CANON/IMG_0001.JPG".
    public let relativePath: String
    public let size: Int64
    public let modificationDate: Date
    public let creationDate: Date?

    public init(url: URL, relativePath: String, size: Int64, modificationDate: Date, creationDate: Date?) {
        self.url = url
        self.relativePath = relativePath
        self.size = size
        self.modificationDate = modificationDate
        self.creationDate = creationDate
    }
}

public struct BackupOptions: Sendable {
    /// Re-read every copied file from the destination and compare its SHA-256 with the source.
    public var verifyCopies: Bool
    /// Free space, in bytes, that must remain on the destination after the backup.
    public var reservedFreeSpace: Int64

    public init(verifyCopies: Bool = true, reservedFreeSpace: Int64 = 200 * 1024 * 1024) {
        self.verifyCopies = verifyCopies
        self.reservedFreeSpace = reservedFreeSpace
    }
}

public struct BackupProgress: Sendable, Equatable {
    public enum Phase: Sendable, Equatable {
        /// Listing the card and working out which files are new.
        case scanning
        case copying
    }

    public var phase: Phase
    public var filesToCopy: Int
    /// Files finished so far (copied or failed).
    public var filesProcessed: Int
    public var bytesToCopy: Int64
    public var bytesProcessed: Int64
    public var filesAlreadyBackedUp: Int
    public var currentFile: String?

    public init(
        phase: Phase,
        filesToCopy: Int = 0,
        filesProcessed: Int = 0,
        bytesToCopy: Int64 = 0,
        bytesProcessed: Int64 = 0,
        filesAlreadyBackedUp: Int = 0,
        currentFile: String? = nil
    ) {
        self.phase = phase
        self.filesToCopy = filesToCopy
        self.filesProcessed = filesProcessed
        self.bytesToCopy = bytesToCopy
        self.bytesProcessed = bytesProcessed
        self.filesAlreadyBackedUp = filesAlreadyBackedUp
        self.currentFile = currentFile
    }

    public var fractionCompleted: Double {
        guard phase == .copying else { return 0 }
        guard bytesToCopy > 0 else { return filesToCopy == 0 ? 1 : Double(filesProcessed) / Double(filesToCopy) }
        return min(1, Double(bytesProcessed) / Double(bytesToCopy))
    }
}

public struct BackupFailure: Sendable, Codable, Equatable {
    public let relativePath: String
    public let message: String

    public init(relativePath: String, message: String) {
        self.relativePath = relativePath
        self.message = message
    }
}

/// One file to copy, and the folder it should end up in (keeping its file name).
public struct CopyItem: Sendable {
    public let file: SourceFile
    public let targetDirectory: URL

    public init(file: SourceFile, targetDirectory: URL) {
        self.file = file
        self.targetDirectory = targetDirectory
    }
}

/// A file that is now safely in the destination.
public struct ProcessedFile: Sendable {
    public let source: SourceFile
    public let destination: URL
    /// `false` when an identical copy was already there and nothing had to be copied.
    public let wasCopied: Bool
}

public struct BackupResult: Sendable {
    /// The folder the files were copied into (the card folder, or the destination root for imports).
    public let destination: URL
    public let filesCopied: Int
    public let bytesCopied: Int64
    public let filesAlreadyBackedUp: Int
    public let failures: [BackupFailure]
    public let processedFiles: [ProcessedFile]
    public let startedAt: Date
    public let finishedAt: Date

    public var isComplete: Bool { failures.isEmpty }
}

public enum BackupError: LocalizedError, Equatable {
    case accessDenied(path: String)
    case sourceUnreadable(path: String, reason: String)
    case sourceDisappeared
    case destinationUnavailable(path: String)
    case destinationInsideSource
    case insufficientSpace(needed: Int64, available: Int64)
    case sizeMismatch(expected: Int64, actual: Int64)
    case verificationFailed
    case tooManyNameCollisions

    public var errorDescription: String? {
        switch self {
        case .accessDenied:
            return "macOS denied access to the card. Allow access to removable volumes in System Settings › Privacy & Security › Files and Folders."
        case let .sourceUnreadable(path, reason):
            return "Couldn’t read \(path): \(reason)"
        case .sourceDisappeared:
            return "The card was removed before the backup finished."
        case let .destinationUnavailable(path):
            return "The backup folder \(path) isn’t available. If it’s on an external drive, connect the drive and try again."
        case .destinationInsideSource:
            return "The backup folder is on the card being backed up. Choose a folder on another drive."
        case let .insufficientSpace(needed, available):
            return "Not enough free space in the backup folder: \(Self.format(needed)) needed, \(Self.format(available)) available."
        case let .sizeMismatch(expected, actual):
            return "The file changed while it was being copied (expected \(expected) bytes, read \(actual))."
        case .verificationFailed:
            return "The copy didn’t match the original when it was verified."
        case .tooManyNameCollisions:
            return "Too many different files with this name already exist in the backup."
        }
    }

    private static func format(_ bytes: Int64) -> String {
        ByteCountFormatter.string(fromByteCount: bytes, countStyle: .file)
    }
}
