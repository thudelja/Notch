import CryptoKit
import Foundation

/// Copies the contents of a memory card into a per-card folder, incrementally and safely.
///
/// Guarantees:
/// - The card is only ever read, never modified.
/// - Files already in the backup are never overwritten or deleted. When a different file with the
///   same name already exists (for example after the card was reformatted and the camera restarted
///   its numbering), the new file is saved as "IMG_0001 (1).JPG".
/// - A file only appears in the backup after it has been completely written and, optionally,
///   verified. An interrupted backup never leaves truncated files behind.
/// - Files that are already backed up are skipped, so inserting the same card again only copies
///   what is new.
///
/// `run` is synchronous and blocking; call it from a background task. It honours Swift task
/// cancellation between chunks.
public struct BackupEngine {
    public typealias ProgressHandler = (BackupProgress) -> Void

    /// Hidden folder inside the destination where files are written before being moved into place.
    public static let stagingFolderName = ".sdbackup-staging"

    static let chunkSize = 4 * 1024 * 1024
    /// FAT and exFAT store modification times with two-second precision.
    static let timestampTolerance: TimeInterval = 2
    static let maxNameVariants = 9_999
    static let scanKeys: Set<URLResourceKey> = [
        .isDirectoryKey, .isRegularFileKey, .isSymbolicLinkKey,
        .fileSizeKey, .contentModificationDateKey, .creationDateKey,
    ]

    public let options: BackupOptions
    private let fileManager: FileManager

    public init(options: BackupOptions = BackupOptions(), fileManager: FileManager = .default) {
        self.options = options
        self.fileManager = fileManager
    }

    // MARK: - Running a backup

    /// Backs up `source` into `destinationRoot/<cardFolderName>`.
    @discardableResult
    public func run(
        source: URL,
        destinationRoot: URL,
        cardFolderName: String,
        progress: @escaping ProgressHandler = { _ in }
    ) throws -> BackupResult {
        let startedAt = Date()
        let source = source.standardizedFileURL
        let destinationRoot = destinationRoot.standardizedFileURL
        try prepare(destinationRoot: destinationRoot, source: source)
        let cardRoot = destinationRoot.appendingPathComponent(Self.sanitizedFolderName(cardFolderName), isDirectory: true)

        var reporter = ProgressReporter(handler: progress)
        var state = BackupProgress(phase: .scanning)
        reporter.send(state, force: true)

        // 1. List every file on the card.
        var failures: [BackupFailure] = []
        var files: [SourceFile] = []
        try scan(source, relativeComponents: [], into: &files, failures: &failures)

        // 2. Work out which files are new and where each one goes.
        var plan: [PlannedCopy] = []
        var reserved = Set<String>()
        var alreadyBackedUp = 0
        for file in files {
            try Task.checkCancellation()
            do {
                if let target = try destination(for: file, in: cardRoot, reserved: &reserved) {
                    plan.append(PlannedCopy(file: file, destination: target))
                } else {
                    alreadyBackedUp += 1
                }
            } catch is CancellationError {
                throw CancellationError()
            } catch {
                try checkVolumesArePresent(source: source, destinationRoot: destinationRoot)
                failures.append(BackupFailure(relativePath: file.relativePath, message: error.localizedDescription))
            }
        }

        // 3. Make sure everything fits.
        let bytesNeeded = plan.reduce(Int64(0)) { $0 + $1.file.size }
        if !plan.isEmpty, let available = availableCapacity(at: destinationRoot),
           bytesNeeded + options.reservedFreeSpace > available {
            throw BackupError.insufficientSpace(needed: bytesNeeded, available: available)
        }

        // 4. Copy.
        state = BackupProgress(
            phase: .copying,
            filesToCopy: plan.count,
            bytesToCopy: bytesNeeded,
            filesAlreadyBackedUp: alreadyBackedUp
        )
        reporter.send(state, force: true)

        var filesCopied = 0
        var bytesCopied: Int64 = 0
        if !plan.isEmpty {
            let staging = destinationRoot.appendingPathComponent(Self.stagingFolderName, isDirectory: true)
            try? fileManager.removeItem(at: staging) // Leftovers from a run that was killed.
            try fileManager.createDirectory(at: staging, withIntermediateDirectories: true)
            defer { try? fileManager.removeItem(at: staging) }

            for item in plan {
                try Task.checkCancellation()
                let bytesBefore = state.bytesProcessed
                state.currentFile = item.file.relativePath
                reporter.send(state)
                do {
                    try copy(item, staging: staging) { delta in
                        state.bytesProcessed += delta
                        reporter.send(state)
                    }
                    filesCopied += 1
                    bytesCopied += item.file.size
                } catch is CancellationError {
                    throw CancellationError()
                } catch {
                    if Self.isOutOfSpace(error) {
                        throw BackupError.insufficientSpace(
                            needed: bytesNeeded - bytesBefore,
                            available: availableCapacity(at: destinationRoot) ?? 0
                        )
                    }
                    try checkVolumesArePresent(source: source, destinationRoot: destinationRoot)
                    failures.append(BackupFailure(relativePath: item.file.relativePath, message: error.localizedDescription))
                }
                state.bytesProcessed = bytesBefore + item.file.size
                state.filesProcessed += 1
            }
        }

        state.currentFile = nil
        reporter.send(state, force: true)

        return BackupResult(
            destination: cardRoot,
            filesCopied: filesCopied,
            bytesCopied: bytesCopied,
            filesAlreadyBackedUp: alreadyBackedUp,
            failures: failures,
            startedAt: startedAt,
            finishedAt: Date()
        )
    }

    /// Turns a volume name into a safe folder name.
    public static func sanitizedFolderName(_ name: String) -> String {
        var result = name
            .replacingOccurrences(of: "/", with: "-")
            .replacingOccurrences(of: ":", with: "-")
            .trimmingCharacters(in: .whitespacesAndNewlines)
        while result.hasPrefix(".") { result.removeFirst() }
        return result.isEmpty ? "Untitled Card" : result
    }

    // MARK: - Destination

    private func prepare(destinationRoot: URL, source: URL) throws {
        let sourcePath = canonicalPath(of: source)
        let destinationPath = canonicalPath(of: destinationRoot)
        let sourcePrefix = sourcePath.hasSuffix("/") ? sourcePath : sourcePath + "/"
        if destinationPath == sourcePath || destinationPath.hasPrefix(sourcePrefix) {
            throw BackupError.destinationInsideSource
        }

        var isDirectory: ObjCBool = false
        if fileManager.fileExists(atPath: destinationRoot.path, isDirectory: &isDirectory) {
            guard isDirectory.boolValue else { throw BackupError.destinationUnavailable(path: destinationRoot.path) }
            return
        }
        // Only create the folder when its parent exists, so an unplugged backup drive is reported
        // as missing instead of the folder being silently recreated somewhere else.
        let parent = destinationRoot.deletingLastPathComponent()
        guard fileManager.fileExists(atPath: parent.path, isDirectory: &isDirectory), isDirectory.boolValue else {
            throw BackupError.destinationUnavailable(path: destinationRoot.path)
        }
        do {
            try fileManager.createDirectory(at: destinationRoot, withIntermediateDirectories: false)
        } catch {
            throw BackupError.destinationUnavailable(path: destinationRoot.path)
        }
    }

    /// Resolves symlinks in the part of the path that exists, then re-appends the rest.
    private func canonicalPath(of url: URL) -> String {
        var existing = url.standardizedFileURL
        var missing: [String] = []
        while !fileManager.fileExists(atPath: existing.path), existing.path != "/" {
            missing.insert(existing.lastPathComponent, at: 0)
            existing.deleteLastPathComponent()
        }
        var resolved = existing.resolvingSymlinksInPath()
        for component in missing {
            resolved.appendPathComponent(component)
        }
        return resolved.path
    }

    private func checkVolumesArePresent(source: URL, destinationRoot: URL) throws {
        guard fileManager.fileExists(atPath: source.path) else { throw BackupError.sourceDisappeared }
        guard fileManager.fileExists(atPath: destinationRoot.path) else {
            throw BackupError.destinationUnavailable(path: destinationRoot.path)
        }
    }

    private func availableCapacity(at url: URL) -> Int64? {
        #if os(macOS)
        if let values = try? url.resourceValues(forKeys: [.volumeAvailableCapacityForImportantUsageKey]),
           let capacity = values.volumeAvailableCapacityForImportantUsage, capacity > 0 {
            return capacity
        }
        #endif
        if let values = try? url.resourceValues(forKeys: [.volumeAvailableCapacityKey]),
           let capacity = values.volumeAvailableCapacity {
            return Int64(capacity)
        }
        return nil
    }

    // MARK: - Scanning

    private func scan(
        _ directory: URL,
        relativeComponents: [String],
        into files: inout [SourceFile],
        failures: inout [BackupFailure]
    ) throws {
        try Task.checkCancellation()

        let children: [URL]
        do {
            children = try fileManager.contentsOfDirectory(
                at: directory,
                includingPropertiesForKeys: Array(Self.scanKeys),
                options: []
            )
        } catch {
            if relativeComponents.isEmpty {
                // Can't read the card at all.
                throw Self.isPermissionError(error)
                    ? BackupError.accessDenied(path: directory.path)
                    : BackupError.sourceUnreadable(path: directory.path, reason: error.localizedDescription)
            }
            failures.append(BackupFailure(
                relativePath: relativeComponents.joined(separator: "/") + "/",
                message: error.localizedDescription
            ))
            return
        }

        for child in children.sorted(by: { $0.lastPathComponent < $1.lastPathComponent }) {
            let name = child.lastPathComponent
            guard !FileFilter.shouldSkip(name) else { continue }
            let components = relativeComponents + [name]

            let values: URLResourceValues
            do {
                values = try child.resourceValues(forKeys: Self.scanKeys)
            } catch {
                failures.append(BackupFailure(relativePath: components.joined(separator: "/"), message: error.localizedDescription))
                continue
            }

            if values.isSymbolicLink == true {
                continue
            } else if values.isDirectory == true {
                try scan(child, relativeComponents: components, into: &files, failures: &failures)
            } else if values.isRegularFile == true {
                files.append(SourceFile(
                    url: child,
                    relativePath: components.joined(separator: "/"),
                    size: Int64(values.fileSize ?? 0),
                    modificationDate: values.contentModificationDate ?? Date(timeIntervalSince1970: 0),
                    creationDate: values.creationDate
                ))
            }
        }
    }

    // MARK: - Planning

    /// Returns where `file` should be copied, or `nil` when it is already backed up.
    ///
    /// Tries "name.ext", then "name (1).ext", "name (2).ext", … and stops at the first slot that is
    /// either free (copy there) or already holds this exact file (skip).
    func destination(for file: SourceFile, in cardRoot: URL, reserved: inout Set<String>) throws -> URL? {
        let original = cardRoot.appendingPathComponent(file.relativePath, isDirectory: false)
        for number in 0...Self.maxNameVariants {
            let candidate = number == 0 ? original : Self.numberedVariant(of: original, number: number)
            // Destination volumes are usually case-insensitive.
            let key = candidate.path.lowercased()
            if reserved.contains(key) { continue }

            var isDirectory: ObjCBool = false
            guard fileManager.fileExists(atPath: candidate.path, isDirectory: &isDirectory) else {
                reserved.insert(key)
                return candidate
            }
            if !isDirectory.boolValue, try isBackup(of: file, at: candidate) {
                return nil
            }
        }
        throw BackupError.tooManyNameCollisions
    }

    private func isBackup(of file: SourceFile, at candidate: URL) throws -> Bool {
        let values = try candidate.resourceValues(forKeys: [.fileSizeKey, .contentModificationDateKey])
        guard let size = values.fileSize, Int64(size) == file.size else { return false }
        if let modified = values.contentModificationDate,
           abs(modified.timeIntervalSince(file.modificationDate)) <= Self.timestampTolerance {
            return true
        }
        // Same size but a different timestamp. FAT cards store local time, so this happens after a
        // time-zone or daylight-saving change. Compare the bytes to be sure.
        return try contentsEqual(file.url, candidate)
    }

    static func numberedVariant(of url: URL, number: Int) -> URL {
        let directory = url.deletingLastPathComponent()
        let ext = url.pathExtension
        let stem = url.deletingPathExtension().lastPathComponent
        let name = ext.isEmpty ? "\(stem) (\(number))" : "\(stem) (\(number)).\(ext)"
        return directory.appendingPathComponent(name, isDirectory: false)
    }

    private func contentsEqual(_ first: URL, _ second: URL) throws -> Bool {
        let a = try FileHandle(forReadingFrom: first)
        defer { try? a.close() }
        let b = try FileHandle(forReadingFrom: second)
        defer { try? b.close() }
        while true {
            try Task.checkCancellation()
            let chunkA = try a.read(upToCount: Self.chunkSize) ?? Data()
            let chunkB = try b.read(upToCount: Self.chunkSize) ?? Data()
            if chunkA != chunkB { return false }
            if chunkA.isEmpty { return true }
        }
    }

    // MARK: - Copying

    private func copy(_ item: PlannedCopy, staging: URL, onBytes: (Int64) -> Void) throws {
        let file = item.file
        let temporary = staging.appendingPathComponent(UUID().uuidString, isDirectory: false)
        defer { try? fileManager.removeItem(at: temporary) } // No-op once the file has been moved.

        guard fileManager.createFile(atPath: temporary.path, contents: nil) else {
            throw CocoaError(.fileWriteUnknown, userInfo: [NSFilePathErrorKey: temporary.path])
        }

        let input = try FileHandle(forReadingFrom: file.url)
        defer { try? input.close() }
        let output = try FileHandle(forWritingTo: temporary)
        var outputIsOpen = true
        defer { if outputIsOpen { try? output.close() } }

        // Bypass the page cache: card data is read once, and verification must read the copy back
        // from disk rather than from memory.
        Self.disableCaching(input)
        Self.disableCaching(output)

        var hasher = SHA256()
        var written: Int64 = 0
        while let chunk = try input.read(upToCount: Self.chunkSize), !chunk.isEmpty {
            try Task.checkCancellation()
            if options.verifyCopies { hasher.update(data: chunk) }
            try output.write(contentsOf: chunk)
            written += Int64(chunk.count)
            onBytes(Int64(chunk.count))
        }
        try output.synchronize()
        try output.close()
        outputIsOpen = false

        guard written == file.size else {
            throw BackupError.sizeMismatch(expected: file.size, actual: written)
        }
        if options.verifyCopies {
            guard try Self.sha256(of: temporary) == hasher.finalize() else {
                throw BackupError.verificationFailed
            }
        }

        var attributes: [FileAttributeKey: Any] = [.modificationDate: file.modificationDate]
        if let created = file.creationDate {
            attributes[.creationDate] = created
        }
        try fileManager.setAttributes(attributes, ofItemAtPath: temporary.path)

        try fileManager.createDirectory(at: item.destination.deletingLastPathComponent(), withIntermediateDirectories: true)
        // moveItem fails rather than overwrite if something appeared at the destination meanwhile.
        try fileManager.moveItem(at: temporary, to: item.destination)
    }

    static func sha256(of url: URL) throws -> SHA256.Digest {
        let handle = try FileHandle(forReadingFrom: url)
        defer { try? handle.close() }
        disableCaching(handle)
        var hasher = SHA256()
        while let chunk = try handle.read(upToCount: chunkSize), !chunk.isEmpty {
            try Task.checkCancellation()
            hasher.update(data: chunk)
        }
        return hasher.finalize()
    }

    static func disableCaching(_ handle: FileHandle) {
        #if canImport(Darwin)
        _ = fcntl(handle.fileDescriptor, F_NOCACHE, 1)
        #endif
    }

    // MARK: - Error classification

    static func isOutOfSpace(_ error: Error) -> Bool {
        errorChain(error, contains: { e in
            (e.domain == NSCocoaErrorDomain && e.code == NSFileWriteOutOfSpaceError)
                || (e.domain == NSPOSIXErrorDomain && e.code == Int(ENOSPC))
        })
    }

    static func isPermissionError(_ error: Error) -> Bool {
        errorChain(error, contains: { e in
            (e.domain == NSCocoaErrorDomain && e.code == NSFileReadNoPermissionError)
                || (e.domain == NSPOSIXErrorDomain && (e.code == Int(EPERM) || e.code == Int(EACCES)))
        })
    }

    private static func errorChain(_ error: Error, contains predicate: (NSError) -> Bool) -> Bool {
        var current: NSError? = error as NSError
        while let candidate = current {
            if predicate(candidate) { return true }
            current = candidate.userInfo[NSUnderlyingErrorKey] as? NSError
        }
        return false
    }
}

// MARK: - Helpers

struct PlannedCopy {
    let file: SourceFile
    let destination: URL
}

/// Limits progress callbacks to about ten per second.
struct ProgressReporter {
    let handler: BackupEngine.ProgressHandler
    private var lastSent = Date.distantPast

    init(handler: @escaping BackupEngine.ProgressHandler) {
        self.handler = handler
    }

    mutating func send(_ progress: BackupProgress, force: Bool = false) {
        let now = Date()
        guard force || now.timeIntervalSince(lastSent) >= 0.1 else { return }
        lastSent = now
        handler(progress)
    }
}
