import Foundation

/// Lists every regular file on a card, skipping operating-system metadata and symbolic links.
public struct CardScanner {
    static let resourceKeys: Set<URLResourceKey> = [
        .isDirectoryKey, .isRegularFileKey, .isSymbolicLinkKey,
        .fileSizeKey, .contentModificationDateKey, .creationDateKey,
    ]

    private let fileManager: FileManager

    public init(fileManager: FileManager = .default) {
        self.fileManager = fileManager
    }

    /// Throws only when the card itself can't be read; unreadable subfolders are reported as failures.
    public func scan(_ source: URL) throws -> (files: [SourceFile], failures: [BackupFailure]) {
        var files: [SourceFile] = []
        var failures: [BackupFailure] = []
        try scan(source.standardizedFileURL, relativeComponents: [], into: &files, failures: &failures)
        return (files, failures)
    }

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
                includingPropertiesForKeys: Array(Self.resourceKeys),
                options: []
            )
        } catch {
            if relativeComponents.isEmpty {
                throw BackupEngine.isPermissionError(error)
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
                values = try child.resourceValues(forKeys: Self.resourceKeys)
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
}
