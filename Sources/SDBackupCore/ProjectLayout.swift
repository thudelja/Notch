import Foundation

/// A shoot that cards are imported into.
///
/// Saved records contain no absolute paths: a project names the projects folder ("library") it
/// lives in by ID, and its folders are derived from its name, start day and folder structure (see
/// `ProjectLayout`). That keeps records valid when a drive is mounted under another name, or read on
/// another OS.
public struct ProjectInfo: Codable, Hashable, Identifiable, Sendable {
    public let id: UUID
    public var name: String
    /// The first shoot day. Decides the year and month folders and the date in the folder name.
    public var startDay: ShootDay
    /// The projects folder this project lives in (its `.sdbackup/library.json` ID).
    public var libraryID: UUID
    public var lastUsed: Date
    /// The folder structure the project was created with, or nil for the standard one (all projects
    /// made before structures could be changed). Kept with the project so changing the projects
    /// folder's structure never moves existing projects.
    public var structure: FolderStructure?
    /// Where that projects folder is on this computer right now, or "" when its drive isn't
    /// connected. Filled in when loading; never saved.
    public var rootPath: String = ""

    private enum CodingKeys: String, CodingKey {
        case id, name, startDay, libraryID, lastUsed, structure
    }

    public init(
        id: UUID = UUID(),
        name: String,
        startDay: ShootDay,
        libraryID: UUID = UUID(),
        structure: FolderStructure? = nil,
        rootPath: String = "",
        lastUsed: Date = Date()
    ) {
        self.id = id
        self.name = name
        self.startDay = startDay
        self.libraryID = libraryID
        self.structure = structure == FolderStructure.standard ? nil : structure
        self.rootPath = rootPath
        self.lastUsed = lastUsed
    }

    public init(from decoder: Decoder) throws {
        let container = try decoder.container(keyedBy: CodingKeys.self)
        id = try container.decode(UUID.self, forKey: .id)
        name = try container.decode(String.self, forKey: .name)
        startDay = try container.decode(ShootDay.self, forKey: .startDay)
        libraryID = try container.decode(UUID.self, forKey: .libraryID)
        lastUsed = try container.decode(Date.self, forKey: .lastUsed)
        // A structure this version can't read, or a broken one, leaves the project on the standard one.
        let structure = (try? container.decodeIfPresent(FolderStructure.self, forKey: .structure)) ?? nil
        self.structure = structure?.isValid == true ? structure : nil
    }

    /// The structure the project's folders follow.
    public var layout: FolderStructure {
        if let structure, structure.isValid { return structure }
        return .standard
    }

    /// "02.11.2026 Ana Wedding"
    public var folderName: String { layout.projectFolderName(name: name, startDay: startDay) }

    /// The record as saved: without this computer's path.
    public var portable: ProjectInfo {
        var copy = self
        copy.rootPath = ""
        return copy
    }
}

/// Everything needed to copy a card's files into their projects.
public struct ImportPlan: Sendable {
    public let items: [CopyItem]
    /// Video project skeletons (Media, Project, Assets, Export) and empty Exported folders.
    public let foldersToCreate: [URL]

    public var fileCount: Int { items.count }
    public var byteCount: Int64 { items.reduce(0) { $0 + $1.file.size } }
}

/// Where a project's files go. The standard structure:
///
///     <root>/2026/Photo/11 November/02.11.2026 Ana Wedding/<Camera>/IMG_0001.CR3
///     <root>/2026/Video/11 November/02.11.2026 Ana Wedding/Media/<Camera>/C0001.MP4
///                                                         /Project  /Assets  /Export
///
/// Each project follows its own `ProjectInfo.layout` (see `FolderStructure`).
public enum ProjectLayout {
    public static var videoSubfolders: [String] { FolderStructure.standard.videoFolders }

    /// "11 November"
    public static func monthFolderName(_ day: ShootDay) -> String {
        FolderStructure.monthFolderName(day)
    }

    /// "02.11.2026 Ana Wedding"
    public static func projectFolderName(name: String, startDay: ShootDay) -> String {
        FolderStructure.projectFolderName(name: name, startDay: startDay)
    }

    /// The folder holding the project's photo camera folders.
    public static func photoFolder(for project: ProjectInfo) -> URL {
        url(project, relativePhotoFolder(for: project))
    }

    /// The folder holding the project's video folders (Media, Project, …).
    public static func videoFolder(for project: ProjectInfo) -> URL {
        url(project, relativeVideoFolder(for: project))
    }

    /// Photos go into the photo project folder; videos and audio into the video project's media
    /// folder. Both get a folder per camera unless the structure turns them off.
    public static func targetDirectory(kind: MediaKind, project: ProjectInfo, camera: String) -> URL {
        let layout = project.layout
        var directory: URL
        switch kind {
        case .photo:
            directory = photoFolder(for: project)
        default:
            directory = videoFolder(for: project)
            if !layout.mediaFolder.isEmpty {
                directory.appendPathComponent(FolderStructure.safe(layout.mediaFolder, fallback: "Media"), isDirectory: true)
            }
        }
        if layout.cameraFolders {
            directory.appendPathComponent(BackupEngine.sanitizedFolderName(camera, fallback: "Camera"), isDirectory: true)
        }
        return directory
    }

    /// Builds the copy list. Items whose day has no project (skipped days) are left out. Sidecars
    /// go into the same folder as their media file. Projects receiving videos get their video
    /// folders; with an Unedited / Exported split, projects also get their empty Exported folder.
    public static func plan(
        items: [MediaItem],
        projects: [ShootDay: ProjectInfo],
        cameraName: (MediaItem) -> String
    ) -> ImportPlan {
        var copies: [CopyItem] = []
        var photoProjects: [UUID: ProjectInfo] = [:]
        var videoProjects: [UUID: ProjectInfo] = [:]
        for item in items {
            guard let project = projects[item.day] else { continue }
            let directory = targetDirectory(kind: item.kind, project: project, camera: cameraName(item))
            copies.append(CopyItem(file: item.file, targetDirectory: directory))
            copies.append(contentsOf: item.sidecars.map { CopyItem(file: $0, targetDirectory: directory) })
            if item.kind == .photo {
                photoProjects[project.id] = project
            } else {
                videoProjects[project.id] = project
            }
        }
        var folders: [String: URL] = [:]
        for project in videoProjects.values {
            for name in project.layout.videoFolders {
                let folder = videoFolder(for: project).appendingPathComponent(FolderStructure.safe(name, fallback: "Folder"), isDirectory: true)
                folders[folder.path] = folder
            }
            if let exported = project.layout.exportedPath(.video, name: project.name, startDay: project.startDay) {
                let folder = url(project, exported)
                folders[folder.path] = folder
            }
        }
        for project in photoProjects.values {
            if let exported = project.layout.exportedPath(.photo, name: project.name, startDay: project.startDay) {
                let folder = url(project, exported)
                folders[folder.path] = folder
            }
        }
        return ImportPlan(items: copies, foldersToCreate: folders.values.sorted { $0.path < $1.path })
    }

    /// The photo folder relative to the projects folder, as path components:
    /// ["2026", "Photo", "11 November", "02.11.2026 Ana Wedding"]. The same on every OS.
    public static func relativePhotoFolder(for project: ProjectInfo) -> [String] {
        project.layout.filesPath(.photo, name: project.name, startDay: project.startDay)
    }

    /// ["2026", "Video", "11 November", "02.11.2026 Ana Wedding"]
    public static func relativeVideoFolder(for project: ProjectInfo) -> [String] {
        project.layout.filesPath(.video, name: project.name, startDay: project.startDay)
    }

    private static func url(_ project: ProjectInfo, _ components: [String]) -> URL {
        components.reduce(URL(fileURLWithPath: project.rootPath, isDirectory: true)) {
            $0.appendingPathComponent($1, isDirectory: true)
        }
    }
}

/// Remembers which files of one card were imported, so re-inserting it only offers new files.
///
/// A file is identified by its path on the card, size and modification time. Reformatting a card
/// changes its volume ID, so a reformatted card gets a fresh ledger.
public struct ImportLedger: Codable, Sendable {
    public private(set) var entries: [String: String] = [:]

    public init() {}

    public var count: Int { entries.count }

    public static func key(for file: SourceFile) -> String {
        let seconds = Int((file.modificationDate.timeIntervalSince1970 / BackupEngine.timestampTolerance).rounded(.down))
        return "\(file.relativePath.lowercased())|\(file.size)|\(seconds)"
    }

    public func contains(_ file: SourceFile) -> Bool {
        entries[Self.key(for: file)] != nil
    }

    public mutating func record(_ file: SourceFile, destination: URL) {
        entries[Self.key(for: file)] = destination.path
    }
}
