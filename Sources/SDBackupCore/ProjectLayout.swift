import Foundation

/// A shoot that cards are imported into.
public struct ProjectInfo: Codable, Hashable, Identifiable, Sendable {
    public let id: UUID
    public var name: String
    /// The first shoot day. Decides the year and month folders and the date in the folder name.
    public var startDay: ShootDay
    /// The projects folder this project lives in.
    public var rootPath: String
    public var lastUsed: Date

    public init(id: UUID = UUID(), name: String, startDay: ShootDay, rootPath: String, lastUsed: Date = Date()) {
        self.id = id
        self.name = name
        self.startDay = startDay
        self.rootPath = rootPath
        self.lastUsed = lastUsed
    }

    /// "02.11.2026 Ana Wedding"
    public var folderName: String { ProjectLayout.projectFolderName(name: name, startDay: startDay) }
}

/// Everything needed to copy a card's files into their projects.
public struct ImportPlan: Sendable {
    public let items: [CopyItem]
    /// Video project skeletons: Media, Project, Assets, Export.
    public let foldersToCreate: [URL]

    public var fileCount: Int { items.count }
    public var byteCount: Int64 { items.reduce(0) { $0 + $1.file.size } }
}

/// The folder structure:
///
///     <root>/2026/Photo/11 November/02.11.2026 Ana Wedding/<Camera>/IMG_0001.CR3
///     <root>/2026/Video/11 November/02.11.2026 Ana Wedding/Media/<Camera>/C0001.MP4
///                                                         /Project  /Assets  /Export
public enum ProjectLayout {
    public static let videoSubfolders = ["Media", "Project", "Assets", "Export"]

    static let monthNames = [
        "January", "February", "March", "April", "May", "June",
        "July", "August", "September", "October", "November", "December",
    ]

    /// "11 November"
    public static func monthFolderName(_ day: ShootDay) -> String {
        let index = min(max(day.month, 1), 12) - 1
        return "\(ShootDay.twoDigits(day.month)) \(monthNames[index])"
    }

    /// "02.11.2026 Ana Wedding"
    public static func projectFolderName(name: String, startDay: ShootDay) -> String {
        "\(startDay.folderDate) \(BackupEngine.sanitizedFolderName(name, fallback: "Untitled"))"
    }

    public static func photoFolder(for project: ProjectInfo) -> URL {
        projectFolder(project, branch: "Photo")
    }

    public static func videoFolder(for project: ProjectInfo) -> URL {
        projectFolder(project, branch: "Video")
    }

    /// Photos go into the photo project folder; videos and audio into the video project's Media
    /// folder. Both get a folder per camera.
    public static func targetDirectory(kind: MediaKind, project: ProjectInfo, camera: String) -> URL {
        let cameraFolder = BackupEngine.sanitizedFolderName(camera, fallback: "Camera")
        switch kind {
        case .photo:
            return photoFolder(for: project).appendingPathComponent(cameraFolder, isDirectory: true)
        default:
            return videoFolder(for: project)
                .appendingPathComponent("Media", isDirectory: true)
                .appendingPathComponent(cameraFolder, isDirectory: true)
        }
    }

    /// Builds the copy list. Items whose day has no project (skipped days) are left out. Sidecars
    /// go into the same folder as their media file.
    public static func plan(
        items: [MediaItem],
        projects: [ShootDay: ProjectInfo],
        cameraName: (MediaItem) -> String
    ) -> ImportPlan {
        var copies: [CopyItem] = []
        var videoProjects: [UUID: ProjectInfo] = [:]
        for item in items {
            guard let project = projects[item.day] else { continue }
            let directory = targetDirectory(kind: item.kind, project: project, camera: cameraName(item))
            copies.append(CopyItem(file: item.file, targetDirectory: directory))
            copies.append(contentsOf: item.sidecars.map { CopyItem(file: $0, targetDirectory: directory) })
            if item.kind != .photo {
                videoProjects[project.id] = project
            }
        }
        let folders = videoProjects.values
            .flatMap { project in videoSubfolders.map { videoFolder(for: project).appendingPathComponent($0, isDirectory: true) } }
            .sorted { $0.path < $1.path }
        return ImportPlan(items: copies, foldersToCreate: folders)
    }

    private static func projectFolder(_ project: ProjectInfo, branch: String) -> URL {
        URL(fileURLWithPath: project.rootPath, isDirectory: true)
            .appendingPathComponent(String(project.startDay.year), isDirectory: true)
            .appendingPathComponent(branch, isDirectory: true)
            .appendingPathComponent(monthFolderName(project.startDay), isDirectory: true)
            .appendingPathComponent(project.folderName, isDirectory: true)
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
