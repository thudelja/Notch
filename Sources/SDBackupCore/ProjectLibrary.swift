import Foundation

/// The file that identifies a projects folder ("library") and lists its projects:
/// `<projects folder>/.sdbackup/library.json`.
///
/// It is the portable record of the folder. It contains no absolute paths, uses ISO 8601 dates
/// and plain JSON, so it stays valid when the drive is mounted elsewhere ("/Volumes/SSD 1",
/// "E:\") and can be read by a Windows version of the app.
public struct LibraryManifest: Codable, Sendable, Equatable {
    public static let currentFormatVersion = 1
    public static let folderName = ".sdbackup"
    public static let fileName = "library.json"

    public var formatVersion: Int
    public let libraryID: UUID
    public var projects: [ProjectInfo]

    public init(libraryID: UUID = UUID(), projects: [ProjectInfo] = []) {
        self.formatVersion = Self.currentFormatVersion
        self.libraryID = libraryID
        self.projects = projects.map(\.portable)
    }

    public static func url(in root: URL) -> URL {
        root.appendingPathComponent(folderName, isDirectory: true).appendingPathComponent(fileName, isDirectory: false)
    }

    /// The manifest of the projects folder at `root`, if it has one.
    public static func read(from root: URL) -> LibraryManifest? {
        guard let data = try? Data(contentsOf: url(in: root)) else { return nil }
        return try? JSONCoding.decoder.decode(LibraryManifest.self, from: data)
    }

    public func write(to root: URL) throws {
        let folder = root.appendingPathComponent(Self.folderName, isDirectory: true)
        try FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true)
        var copy = self
        copy.projects = projects.map(\.portable).sorted { $0.id.uuidString < $1.id.uuidString }
        try JSONCoding.encoder.encode(copy).write(to: Self.url(in: root), options: .atomic)
    }

    /// Reads the folder's manifest, creating one (with a new ID) if it has none yet.
    public static func openOrCreate(at root: URL) throws -> LibraryManifest {
        if let existing = read(from: root) { return existing }
        let manifest = LibraryManifest()
        try manifest.write(to: root)
        return manifest
    }
}

/// Where a projects folder was last seen on this computer. Machine-local; not portable.
public struct LibraryLocation: Codable, Sendable, Equatable, Identifiable {
    public var id: UUID { libraryID }
    public let libraryID: UUID
    /// e.g. "/Volumes/SSD/Projects"
    public var lastKnownPath: String
    /// The folder's path relative to the root of its drive, e.g. "Projects". Used to find it again
    /// when the drive is mounted under a different name.
    public var pathOnVolume: String
    public var volumeName: String
    /// Known from before projects folders had manifests; the manifest is written once the folder
    /// is reachable.
    public var markerPending: Bool?

    public init(libraryID: UUID, lastKnownPath: String, pathOnVolume: String, volumeName: String, markerPending: Bool? = nil) {
        self.libraryID = libraryID
        self.lastKnownPath = lastKnownPath
        self.pathOnVolume = pathOnVolume
        self.volumeName = volumeName
        self.markerPending = markerPending
    }
}

public enum LibraryLocator {
    /// Finds where a projects folder is now: at its last known path, or at the same path on any
    /// connected drive. A folder only counts if its manifest has the right ID.
    public static func resolve(_ location: LibraryLocation, volumes: [URL]) -> URL? {
        let last = URL(fileURLWithPath: location.lastKnownPath, isDirectory: true)
        if LibraryManifest.read(from: last)?.libraryID == location.libraryID {
            return last
        }
        for volume in volumes {
            let candidate = location.pathOnVolume.isEmpty
                ? volume
                : volume.appendingPathComponent(location.pathOnVolume, isDirectory: true)
            if LibraryManifest.read(from: candidate)?.libraryID == location.libraryID {
                return candidate.standardizedFileURL
            }
        }
        return nil
    }

    /// Describes where the projects folder at `root` is, for remembering it on this computer.
    public static func location(of root: URL, libraryID: UUID) -> LibraryLocation {
        let root = root.standardizedFileURL
        let values = try? root.resourceValues(forKeys: [.volumeURLKey, .volumeLocalizedNameKey])
        let volumePath = values?.volume?.standardizedFileURL.path ?? "/"
        var relative = root.path
        if volumePath != "/", relative.hasPrefix(volumePath) {
            relative.removeFirst(volumePath.count)
        }
        while relative.hasPrefix("/") {
            relative.removeFirst()
        }
        return LibraryLocation(
            libraryID: libraryID,
            lastKnownPath: root.path,
            pathOnVolume: relative,
            volumeName: values?.volumeLocalizedName ?? ""
        )
    }
}

/// JSON settings shared by every file the app writes: ISO 8601 dates, readable output.
public enum JSONCoding {
    public static var encoder: JSONEncoder {
        let encoder = JSONEncoder()
        encoder.dateEncodingStrategy = .iso8601
        encoder.outputFormatting = [.prettyPrinted, .sortedKeys]
        return encoder
    }

    public static var decoder: JSONDecoder {
        let decoder = JSONDecoder()
        decoder.dateDecodingStrategy = .iso8601
        return decoder
    }
}
