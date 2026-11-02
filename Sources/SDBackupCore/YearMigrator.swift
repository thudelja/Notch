import Foundation

public struct MigrationResult: Sendable {
    /// The year folder on the new drive.
    public let destination: URL
    public let filesMoved: Int
    public let bytesMoved: Int64
    /// Files that were already on the new drive with identical contents; the old copy was removed.
    public let duplicatesRemoved: Int
    public let failures: [BackupFailure]
    /// Files, links or folders still in the old year folder afterwards.
    public let itemsLeftBehind: Int

    public var isComplete: Bool { failures.isEmpty && itemsLeftBehind == 0 }
}

/// Moves a whole year folder (e.g. Projects/2026) to another drive, keeping its structure.
///
/// Each file is copied, read back and checked with SHA-256, and only then deleted from the old
/// location. Stopping half-way is safe: every file is either moved, or still in its old place.
/// Running it again finishes the job: files already on the new drive with identical contents are
/// recognised (byte for byte) and removed from the old location. A different file that happens to
/// have the same name on the new drive is kept, and the moved one is saved as "name (1).ext".
public struct YearMigrator {
    private let fileManager: FileManager

    public init(fileManager: FileManager = .default) {
        self.fileManager = fileManager
    }

    /// The year folders (2025, 2026, …) inside a projects folder, oldest first.
    public static func years(in root: URL, fileManager: FileManager = .default) -> [Int] {
        guard let names = try? fileManager.contentsOfDirectory(atPath: root.path) else { return [] }
        return names.compactMap { name -> Int? in
            guard name.count == 4, let year = Int(name), (1900...2999).contains(year) else { return nil }
            var isDirectory: ObjCBool = false
            guard fileManager.fileExists(atPath: root.appendingPathComponent(name).path, isDirectory: &isDirectory),
                  isDirectory.boolValue
            else { return nil }
            return year
        }
        .sorted()
    }

    /// Moves `sourceRoot/<year>` to `destinationRoot/<year>`.
    public func moveYear(
        _ year: Int,
        from sourceRoot: URL,
        to destinationRoot: URL,
        progress: @escaping BackupEngine.ProgressHandler = { _ in }
    ) throws -> MigrationResult {
        let source = sourceRoot.standardizedFileURL.appendingPathComponent(String(year), isDirectory: true)
        let destination = destinationRoot.standardizedFileURL.appendingPathComponent(String(year), isDirectory: true)

        var isDirectory: ObjCBool = false
        guard fileManager.fileExists(atPath: source.path, isDirectory: &isDirectory), isDirectory.boolValue else {
            throw BackupError.sourceUnreadable(path: source.path, reason: "There is no \(year) folder.")
        }
        let sourcePath = fileManager.canonicalPath(of: source)
        let destinationPath = fileManager.canonicalPath(of: destination)
        if sourcePath == destinationPath || sourcePath.hasPrefix(destinationPath + "/") {
            throw BackupError.destinationIsSource
        }
        guard fileManager.fileExists(atPath: destinationRoot.path) else {
            throw BackupError.destinationUnavailable(path: destinationRoot.path)
        }

        let scan = try CardScanner(fileManager: fileManager).scan(source)
        let items = scan.files.map { file -> CopyItem in
            let folder = (file.relativePath as NSString).deletingLastPathComponent
            let target = folder.isEmpty ? destination : destination.appendingPathComponent(folder, isDirectory: true)
            return CopyItem(file: file, targetDirectory: target)
        }
        // Recreate every folder, including empty ones such as an unused Export folder.
        let folders = scan.directories.map { destination.appendingPathComponent($0, isDirectory: true) }

        var deleteFailures: [BackupFailure] = []
        var duplicatesRemoved = 0
        let engine = BackupEngine(options: BackupOptions(verifyCopies: true, compareDuplicateContents: true))
        let result = try engine.copy(
            items,
            from: source,
            to: destination,
            createFolders: folders,
            fileCompleted: { done in
                do {
                    try fileManager.removeItem(at: done.source.url)
                    if !done.wasCopied { duplicatesRemoved += 1 }
                } catch {
                    deleteFailures.append(BackupFailure(
                        relativePath: done.source.relativePath,
                        message: "Copied and verified, but the original couldn’t be deleted: \(error.localizedDescription)"
                    ))
                }
            },
            progress: progress
        )

        let leftBehind = removeEmptyFolders(source)

        return MigrationResult(
            destination: destination,
            filesMoved: result.filesCopied,
            bytesMoved: result.bytesCopied,
            duplicatesRemoved: duplicatesRemoved,
            failures: scan.failures + result.failures + deleteFailures,
            itemsLeftBehind: leftBehind
        )
    }

    /// Deletes Finder metadata and empty folders, deepest first. Returns how many items remain.
    private func removeEmptyFolders(_ root: URL) -> Int {
        let keys: Set<URLResourceKey> = [.isDirectoryKey, .isSymbolicLinkKey]

        func clean(_ directory: URL) -> Int {
            guard let children = try? fileManager.contentsOfDirectory(
                at: directory,
                includingPropertiesForKeys: Array(keys),
                options: []
            ) else { return 1 }

            var remaining = 0
            for child in children {
                let name = child.lastPathComponent
                let values = try? child.resourceValues(forKeys: keys)
                if values?.isDirectory == true, values?.isSymbolicLink != true {
                    let inside = clean(child)
                    if inside > 0 {
                        remaining += inside
                    } else if (try? fileManager.removeItem(at: child)) == nil {
                        remaining += 1
                    }
                } else if name == ".DS_Store" || name.hasPrefix("._") {
                    try? fileManager.removeItem(at: child)
                } else {
                    remaining += 1
                }
            }
            return remaining
        }

        let remaining = clean(root)
        if remaining == 0 {
            try? fileManager.removeItem(at: root)
        }
        return remaining
    }
}
