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
    /// The folder structure the project's photos follow, as it was when the project was created,
    /// or nil for the standard one (all projects made before structures could be changed). Kept
    /// with the project so changing the projects folder's structure never moves existing projects.
    public var structure: FolderStructure?
    /// For photos put in a folder the user chose: the project folder's path below its projects
    /// folder, e.g. ["2026", "Clients", "02.11.2026 Ana"]. Nil for photos laid out by the structure.
    /// (Projects made before 1.3 put their videos there too.)
    public var folder: [String]?
    /// The projects folder holding this project's videos, when they go to another folder than the
    /// photos. Nil: the same as `libraryID`.
    public var videoLibraryID: UUID?
    /// The structure the project's videos follow. Set on every project made since 1.3; nil for older
    /// projects, whose videos follow `structure` (and `folder`) as before.
    public var videoStructure: FolderStructure?
    /// For videos put in a folder the user chose: the project folder's path below the videos'
    /// projects folder.
    public var videoFolder: [String]?
    /// The multi-day shoot (e.g. "AMD Koledar 2026") the project belongs to: its folder takes the
    /// project folder's place in the structures, and the project folder goes inside it.
    public var series: String?
    /// Where that projects folder is on this computer right now, or "" when its drive isn't
    /// connected. Filled in when loading; never saved.
    public var rootPath: String = ""
    /// Where the videos' projects folder is on this computer right now (with `videoLibraryID`), or
    /// "". Filled in when loading; never saved.
    public var videoRootPath: String = ""

    private enum CodingKeys: String, CodingKey {
        case id, name, startDay, libraryID, lastUsed, structure, folder, videoLibraryID, videoStructure, videoFolder, series
    }

    public init(
        id: UUID = UUID(),
        name: String,
        startDay: ShootDay,
        libraryID: UUID = UUID(),
        structure: FolderStructure? = nil,
        folder: [String]? = nil,
        videoLibraryID: UUID? = nil,
        videoStructure: FolderStructure? = nil,
        videoFolder: [String]? = nil,
        series: String? = nil,
        rootPath: String = "",
        videoRootPath: String = "",
        lastUsed: Date = Date()
    ) {
        self.id = id
        self.name = name
        self.startDay = startDay
        self.libraryID = libraryID
        self.structure = structure == FolderStructure.standard ? nil : structure
        self.folder = folder?.isEmpty == true ? nil : folder
        self.videoLibraryID = videoLibraryID == libraryID ? nil : videoLibraryID
        self.videoStructure = videoStructure
        self.videoFolder = videoFolder?.isEmpty == true ? nil : videoFolder
        let trimmedSeries = series?.trimmingCharacters(in: .whitespacesAndNewlines) ?? ""
        self.series = trimmedSeries.isEmpty ? nil : trimmedSeries
        self.rootPath = rootPath
        self.videoRootPath = videoRootPath
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
        let folder = (try? container.decodeIfPresent([String].self, forKey: .folder)) ?? nil
        self.folder = folder?.isEmpty == true ? nil : folder
        videoLibraryID = (try? container.decodeIfPresent(UUID.self, forKey: .videoLibraryID)) ?? nil
        // A video structure this version can't read is read as the standard one; the project stays a
        // 1.3 one (whose videos don't follow the photos' structure).
        if container.contains(.videoStructure) {
            let decoded = (try? container.decodeIfPresent(FolderStructure.self, forKey: .videoStructure)) ?? nil
            videoStructure = decoded?.isValid == true ? decoded : FolderStructure.standard
        } else {
            videoStructure = nil
        }
        let videoFolder = (try? container.decodeIfPresent([String].self, forKey: .videoFolder)) ?? nil
        self.videoFolder = videoFolder?.isEmpty == true ? nil : videoFolder
        let series = ((try? container.decodeIfPresent(String.self, forKey: .series)) ?? nil)?
            .trimmingCharacters(in: .whitespacesAndNewlines)
        self.series = series?.isEmpty == true ? nil : series
    }

    /// The structure the project's photos follow.
    public var layout: FolderStructure {
        if let structure, structure.isValid { return structure }
        return .standard
    }

    /// The structure the project's videos follow.
    public var videoLayout: FolderStructure {
        if let videoStructure, videoStructure.isValid { return videoStructure }
        return layout
    }

    /// Made before 1.3: the videos follow the photos' structure and chosen folder.
    var hasLegacyVideoLayout: Bool { videoStructure == nil }

    /// "02.11.2026 Ana Wedding"
    public var folderName: String { folder?.last ?? layout.projectFolderName(name: name, startDay: startDay) }

    /// Videos go to another projects folder than photos.
    public var hasSeparateVideoFolder: Bool {
        guard videoLibraryID != nil else { return false }
        // Before 1.3, a project in a chosen folder kept its videos there.
        return !(hasLegacyVideoLayout && folder != nil)
    }

    /// The ID of the projects folder holding the videos.
    public var videoLibrary: UUID { hasSeparateVideoFolder ? videoLibraryID ?? libraryID : libraryID }

    /// Where the projects folder holding the videos is on this computer, or "".
    public var videoRoot: String { hasSeparateVideoFolder ? videoRootPath : rootPath }

    /// The record as saved: without this computer's paths.
    public var portable: ProjectInfo {
        var copy = self
        copy.rootPath = ""
        copy.videoRootPath = ""
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

    /// Where one kind of a project's files goes: the projects folder, the structure, and paths
    /// below that folder.
    struct Placement {
        let root: String
        let layout: FolderStructure
        /// The folder holding the camera folders (photos) or the video folders (videos).
        let files: [String]
        /// The empty Exported folder, with an Unedited / Exported split.
        let exported: [String]?
        /// The project folder itself.
        let projectFolder: [String]
    }

    static func placement(_ branch: FolderStructure.Branch, for project: ProjectInfo) -> Placement {
        let source: (layout: FolderStructure, chosen: [String]?, root: String)
        switch branch {
        case .photo:
            source = (project.layout, project.folder, project.rootPath)
        case .video where project.hasLegacyVideoLayout:
            // Before 1.3, videos followed the photos' structure and chosen folder.
            source = (project.layout, project.folder, project.videoRoot)
        case .video:
            source = (project.videoLayout, project.videoFolder, project.videoRoot)
        }
        let (layout, chosen, root) = source
        if let chosen {
            return Placement(
                root: root, layout: layout,
                files: chosen + layout.insideProjectFiles,
                exported: layout.insideProjectExported.map { chosen + $0 },
                projectFolder: chosen
            )
        }
        return Placement(
            root: root, layout: layout,
            files: layout.filesPath(branch, name: project.name, startDay: project.startDay, series: project.series),
            exported: layout.exportedPath(branch, name: project.name, startDay: project.startDay, series: project.series),
            projectFolder: layout.projectPath(branch, name: project.name, startDay: project.startDay, series: project.series)
        )
    }

    /// The folder holding the project's photo camera folders.
    public static func photoFolder(for project: ProjectInfo) -> URL {
        let placement = placement(.photo, for: project)
        return url(placement.root, placement.files)
    }

    /// The folder holding the project's video folders (Media, Project, …), in the videos'
    /// projects folder.
    public static func videoFolder(for project: ProjectInfo) -> URL {
        let placement = placement(.video, for: project)
        return url(placement.root, placement.files)
    }

    /// The project's folders: the photo and video project folders (one when they're the same).
    /// Used to recognise a project when the user picks its folder.
    public static func projectFolders(for project: ProjectInfo) -> [URL] {
        let photo = placement(.photo, for: project)
        let video = placement(.video, for: project)
        let folders = [url(photo.root, photo.projectFolder), url(video.root, video.projectFolder)]
        return folders[0].path == folders[1].path ? [folders[0]] : folders
    }

    /// The project's empty Exported folder for photos or videos, if its structure has a split.
    public static func exportedFolder(_ branch: FolderStructure.Branch, for project: ProjectInfo) -> URL? {
        let placement = placement(branch, for: project)
        return placement.exported.map { url(placement.root, $0) }
    }

    /// Photos go into the photo project folder; videos and audio into the video project's media
    /// folder. Both get a folder per camera unless the structure turns them off.
    public static func targetDirectory(kind: MediaKind, project: ProjectInfo, camera: String) -> URL {
        let layout = kind == .photo ? project.layout : placement(.video, for: project).layout
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
            for name in placement(.video, for: project).layout.videoFolders {
                let folder = videoFolder(for: project).appendingPathComponent(FolderStructure.safe(name, fallback: "Folder"), isDirectory: true)
                folders[folder.path] = folder
            }
            if let folder = exportedFolder(.video, for: project) {
                folders[folder.path] = folder
            }
        }
        for project in photoProjects.values {
            if let folder = exportedFolder(.photo, for: project) {
                folders[folder.path] = folder
            }
        }
        return ImportPlan(items: copies, foldersToCreate: folders.values.sorted { $0.path < $1.path })
    }

    /// The photo folder relative to the projects folder, as path components:
    /// ["2026", "Photo", "11 November", "02.11.2026 Ana Wedding"]. The same on every OS.
    public static func relativePhotoFolder(for project: ProjectInfo) -> [String] {
        placement(.photo, for: project).files
    }

    /// ["2026", "Video", "11 November", "02.11.2026 Ana Wedding"], relative to the videos'
    /// projects folder.
    public static func relativeVideoFolder(for project: ProjectInfo) -> [String] {
        placement(.video, for: project).files
    }

    static func url(_ root: String, _ components: [String]) -> URL {
        components.reduce(URL(fileURLWithPath: root, isDirectory: true)) {
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
