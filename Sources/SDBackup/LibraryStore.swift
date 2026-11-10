import Foundation
import SDBackupCore

/// Projects, the projects folders ("libraries") they live in, camera names and per-card import
/// records.
///
/// Portable data, stored in each projects folder:
/// - `.sdbackup/library.json`: the folder's ID, its projects (without absolute paths) and the folder
///   structure new projects get. A drive mounted under another name, or read by a Windows version,
///   still makes sense.
///
/// Data local to this Mac, in ~/Library/Application Support/SD Backup (the app's name before it
/// was called Notch; kept so existing settings, projects and import records carry over):
/// - `libraries.json`: where each projects folder was last seen.
/// - `projects.json`: every project, so projects on a disconnected drive still show up.
/// - `Imports/<card>.json`: which files of each card were imported.
/// Camera names live in UserDefaults.
@MainActor
final class LibraryStore {
    private enum Key {
        static let modelAliases = "cameraModelAliases"
        static let cardAliases = "cameraCardAliases"
    }

    private struct ProjectCache: Codable {
        var formatVersion = 2
        var projects: [ProjectInfo]
    }

    /// projects.json as written before projects folders had IDs: an array with absolute paths.
    private struct LegacyProject: Decodable {
        let id: UUID
        let name: String
        let startDay: ShootDay
        let rootPath: String
        let lastUsed: Date
    }

    private let directory: URL
    /// Multi-day shoots, local to this Mac.
    let shoots: MultiDayShootStore
    private(set) var projects: [ProjectInfo] = []
    private var locations: [UUID: LibraryLocation] = [:]
    /// Camera model → folder name, e.g. "Canon EOS R5" → "R5".
    private var modelAliases: [String: String]
    /// Card ID → folder name for files without camera information (e.g. an audio recorder's card).
    private var cardAliases: [String: String]

    init() {
        let support = FileManager.default.urls(for: .applicationSupportDirectory, in: .userDomainMask).first
            ?? FileManager.default.homeDirectoryForCurrentUser.appendingPathComponent("Library/Application Support", isDirectory: true)
        let directory = support.appendingPathComponent("SD Backup", isDirectory: true)
        try? FileManager.default.createDirectory(
            at: directory.appendingPathComponent("Imports", isDirectory: true),
            withIntermediateDirectories: true
        )
        self.directory = directory
        shoots = MultiDayShootStore(file: directory.appendingPathComponent("series.json"))
        modelAliases = UserDefaults.standard.dictionary(forKey: Key.modelAliases) as? [String: String] ?? [:]
        cardAliases = UserDefaults.standard.dictionary(forKey: Key.cardAliases) as? [String: String] ?? [:]

        let saved = Self.read([LibraryLocation].self, from: directory.appendingPathComponent("libraries.json")) ?? []
        locations = Dictionary(saved.map { ($0.libraryID, $0) }, uniquingKeysWith: { first, _ in first })
        loadProjects()
        refreshLocations()
    }

    // MARK: - Projects folders

    /// The ID of the projects folder at `root`, creating the folder's manifest if it has none.
    /// Returns nil when the folder can't be created or written (e.g. its drive isn't connected).
    @discardableResult
    func library(at root: URL) -> UUID? {
        let root = root.standardizedFileURL
        let fileManager = FileManager.default
        var isDirectory: ObjCBool = false
        if !fileManager.fileExists(atPath: root.path, isDirectory: &isDirectory) {
            // Only create the folder when its parent exists, so an unplugged drive isn't recreated.
            guard fileManager.fileExists(atPath: root.deletingLastPathComponent().path),
                  (try? fileManager.createDirectory(at: root, withIntermediateDirectories: false)) != nil
            else { return nil }
        } else if !isDirectory.boolValue {
            return nil
        }

        let manifest: LibraryManifest
        if let existing = LibraryManifest.read(from: root) {
            manifest = existing
        } else {
            // A folder this Mac already knew (from before manifests existed) keeps its ID.
            let knownID = locations.values.first { $0.lastKnownPath == root.path }?.libraryID ?? UUID()
            let fresh = LibraryManifest(libraryID: knownID, projects: projects.filter { $0.libraryID == knownID })
            guard (try? fresh.write(to: root)) != nil else { return nil }
            manifest = fresh
        }

        locations[manifest.libraryID] = LibraryLocator.location(of: root, libraryID: manifest.libraryID)
        saveLocations()
        merge(manifest, at: root)
        setRoot(root, for: manifest.libraryID)
        return manifest.libraryID
    }

    /// Finds every known projects folder again (drives may have been connected, disconnected or
    /// mounted under another name) and fills in each project's `rootPath` ("" when offline).
    func refreshLocations() {
        let volumes = FileManager.default.mountedVolumeURLs(includingResourceValuesForKeys: nil, options: [.skipHiddenVolumes]) ?? []
        var roots: [UUID: URL] = [:]
        var registryChanged = false

        for (id, location) in locations {
            var found = LibraryLocator.resolve(location, volumes: volumes)
            if found == nil, location.markerPending == true {
                // Known from before manifests existed: write one now that the folder is reachable.
                let url = URL(fileURLWithPath: location.lastKnownPath, isDirectory: true)
                let manifest = LibraryManifest(libraryID: id, projects: projects.filter { $0.libraryID == id })
                if FileManager.default.fileExists(atPath: url.path), (try? manifest.write(to: url)) != nil {
                    found = url
                }
            }
            guard let root = found else { continue }
            roots[id] = root
            let current = LibraryLocator.location(of: root, libraryID: id)
            if current != location {
                locations[id] = current
                registryChanged = true
            }
        }
        if registryChanged {
            saveLocations()
        }

        for (id, root) in roots {
            if let manifest = LibraryManifest.read(from: root) {
                merge(manifest, at: root)
            }
            writeManifest(for: id, at: root)
        }
        for index in projects.indices {
            projects[index].rootPath = roots[projects[index].libraryID]?.path ?? ""
            projects[index].videoRootPath = projects[index].videoLibraryID.flatMap { roots[$0]?.path } ?? ""
        }
    }

    // MARK: - Folder structure

    /// The folder structure new projects' photos or videos in the projects folder at `root` get.
    func structure(_ branch: FolderStructure.Branch, at root: URL) -> FolderStructure {
        let manifest = LibraryManifest.read(from: root.standardizedFileURL)
        switch branch {
        case .photo: return manifest?.layout ?? .standard
        case .video: return manifest?.videoLayout ?? .standard
        }
    }

    /// Saves the folder structure new projects' photos or videos in the projects folder at `root`
    /// get. Existing projects keep theirs. False when the folder isn't available.
    func setStructure(_ structure: FolderStructure, for branch: FolderStructure.Branch, at root: URL) -> Bool {
        let root = root.standardizedFileURL
        guard structure.isValid, let libraryID = library(at: root) else { return false }
        var manifest = LibraryManifest.read(from: root)
            ?? LibraryManifest(libraryID: libraryID, projects: projects.filter { $0.libraryID == libraryID })
        switch branch {
        case .photo:
            // A folder set up before 1.3 has one structure for both: the videos keep it.
            if manifest.videoStructure == nil {
                manifest.videoStructure = manifest.layout
            }
            manifest.structure = structure == .standard ? nil : structure
        case .video:
            manifest.videoStructure = structure
        }
        return (try? manifest.write(to: root)) != nil
    }

    // MARK: - Projects

    func recentProjects(limit: Int = 2) -> [ProjectInfo] {
        Array(projects.sorted { $0.lastUsed > $1.lastUsed }.prefix(limit))
    }

    func project(id: UUID) -> ProjectInfo? {
        projects.first { $0.id == id }
    }

    /// Where one kind of a new project's files goes.
    struct Place: Equatable {
        /// The projects folder whose structure lays it out.
        var root: URL
        /// A folder the user chose instead: the project folder is made inside it.
        var chosenFolder: URL?
    }

    /// The existing project with this name and start day in these places, or a new one. Photos and
    /// videos each follow their projects folder's structure, without the Photo / Video level when
    /// they go to different folders; a project of a multi-day shoot (`series`) goes in the shoot's
    /// folder. Nil when a folder isn't available and was never seen on this computer.
    func newProject(_ name: String, startDay: ShootDay, series: String?, photos: Place, videos: Place) -> ProjectInfo? {
        let trimmed = name.trimmingCharacters(in: .whitespacesAndNewlines)
        var photoStructure = structure(.photo, at: photos.root)
        var videoStructure = structure(.video, at: videos.root)
        if photos.root.standardizedFileURL.path != videos.root.standardizedFileURL.path {
            photoStructure = photoStructure.withoutMediaType
            videoStructure = videoStructure.withoutMediaType
        }
        guard let photo = resolve(photos, projectFolderName: photoStructure.projectFolderName(name: trimmed, startDay: startDay)),
              let video = resolve(videos, projectFolderName: videoStructure.projectFolderName(name: trimmed, startDay: startDay))
        else { return nil }
        let series = series?.trimmingCharacters(in: .whitespacesAndNewlines)
        if var existing = projects.first(where: {
            $0.libraryID == photo.id && $0.folder == photo.folder && $0.startDay == startDay
                && $0.name.caseInsensitiveCompare(trimmed) == .orderedSame
                && ($0.series ?? "").caseInsensitiveCompare(series ?? "") == .orderedSame
        }) {
            existing.rootPath = photo.path
            return existing
        }
        return ProjectInfo(
            name: trimmed, startDay: startDay, libraryID: photo.id, structure: photoStructure, folder: photo.folder,
            videoLibraryID: video.id, videoStructure: videoStructure, videoFolder: video.folder, series: series,
            rootPath: photo.path, videoRootPath: video.path
        )
    }

    /// The projects folder for a place, and the project folder's path in it for a chosen folder. A
    /// chosen folder belongs to the nearest projects folder at or above it (so its projects move with
    /// their year and are found on another computer); one outside every projects folder becomes one.
    private func resolve(_ place: Place, projectFolderName: String) -> (id: UUID, path: String, folder: [String]?)? {
        if let chosen = place.chosenFolder?.standardizedFileURL {
            guard let root = projectsFolder(containing: chosen) ?? (library(at: chosen) == nil ? nil : chosen),
                  let id = library(at: root)
            else { return nil }
            let relative = Array(chosen.pathComponents.dropFirst(root.pathComponents.count))
            return (id, root.path, relative + [projectFolderName])
        }
        let root = place.root.standardizedFileURL
        if let id = library(at: root) {
            return (id, root.path, nil)
        }
        // A folder on a drive that isn't connected is still recorded; its files wait for it.
        if let id = locations.values.first(where: { $0.lastKnownPath == root.path })?.libraryID {
            return (id, "", nil)
        }
        return nil
    }

    /// The known project whose folder is `folder` (its photo or video project folder, or the folder
    /// it was created in), if any.
    func project(atFolder folder: URL) -> ProjectInfo? {
        let path = folder.standardizedFileURL.path
        return projects.first { project in
            !project.rootPath.isEmpty
                && ProjectLayout.projectFolders(for: project).contains { $0.standardizedFileURL.path == path }
        }
    }

    /// The nearest folder at or above `folder` that is a projects folder (has a library.json).
    private func projectsFolder(containing folder: URL) -> URL? {
        var current = folder.standardizedFileURL
        while true {
            if LibraryManifest.read(from: current) != nil { return current }
            let parent = current.deletingLastPathComponent().standardizedFileURL
            if parent.path == current.path { return nil }
            current = parent
        }
    }

    /// Saves new projects and moves the given ones to the top of the recent list.
    func markUsed(_ used: [ProjectInfo]) {
        // Whole seconds: that is what the manifests store (ISO 8601).
        let now = Date(timeIntervalSince1970: Date().timeIntervalSince1970.rounded(.down))
        for var project in used {
            project.lastUsed = now
            if let index = projects.firstIndex(where: { $0.id == project.id }) {
                projects[index] = project
            } else {
                projects.append(project)
            }
        }
        saveCache()
        for project in used where !project.rootPath.isEmpty {
            writeManifest(for: project.libraryID, at: URL(fileURLWithPath: project.rootPath, isDirectory: true))
        }
        for project in used where project.hasSeparateVideoFolder && !project.videoRootPath.isEmpty {
            writeManifest(for: project.videoLibrary, at: URL(fileURLWithPath: project.videoRootPath, isDirectory: true))
        }
    }

    /// After a year folder was moved from `oldRoot` to `newRoot`: the projects (or, for projects
    /// with a separate videos folder, the videos) in it now belong to the projects folder at
    /// `newRoot`. Returns how many projects changed.
    @discardableResult
    func moveProjects(year: Int, from oldRoot: URL, to newRoot: URL) -> Int {
        guard let oldID = LibraryManifest.read(from: oldRoot)?.libraryID
                ?? locations.values.first(where: { $0.lastKnownPath == oldRoot.standardizedFileURL.path })?.libraryID,
              let newID = library(at: newRoot)
        else { return 0 }

        let yearText = String(year)
        let newPath = newRoot.standardizedFileURL.path
        var changed: [ProjectInfo] = []
        for index in projects.indices {
            var project = projects[index]
            var moved = false
            if project.libraryID == oldID, ProjectLayout.relativePhotoFolder(for: project).first == yearText {
                project.libraryID = newID
                project.rootPath = newPath
                moved = true
            }
            if project.hasSeparateVideoFolder, project.videoLibraryID == oldID,
               ProjectLayout.relativeVideoFolder(for: project).first == yearText {
                project.videoLibraryID = newID
                project.videoRootPath = newPath
                moved = true
            }
            guard moved else { continue }
            if project.videoLibraryID == project.libraryID {
                project.videoLibraryID = nil
                project.videoRootPath = ""
            }
            projects[index] = project
            changed.append(project)
        }
        if !changed.isEmpty {
            saveCache()
            writeManifest(for: oldID, at: oldRoot)
            writeManifest(for: newID, at: newRoot)
            // The other folder each project lives in lists it too.
            for project in changed {
                if !project.rootPath.isEmpty, project.libraryID != newID {
                    writeManifest(for: project.libraryID, at: URL(fileURLWithPath: project.rootPath, isDirectory: true))
                }
                if project.hasSeparateVideoFolder, !project.videoRootPath.isEmpty, project.videoLibrary != newID {
                    writeManifest(for: project.videoLibrary, at: URL(fileURLWithPath: project.videoRootPath, isDirectory: true))
                }
            }
        }
        return changed.count
    }

    // MARK: - Camera names

    func cameraName(forModel model: String, make: String?) -> String {
        modelAliases[model] ?? CameraNames.shortName(make: make, model: model)
    }

    func cameraName(forCard cardID: String) -> String? {
        cardAliases[cardID]
    }

    /// `names` maps camera models to folder names; the key "" holds the name for files without
    /// camera information, which is remembered for this card.
    func rememberCameraNames(_ names: [String: String], cardID: String) {
        for (key, name) in names {
            let trimmed = name.trimmingCharacters(in: .whitespacesAndNewlines)
            guard !trimmed.isEmpty else { continue }
            if key.isEmpty {
                cardAliases[cardID] = trimmed
            } else {
                modelAliases[key] = trimmed
            }
        }
        UserDefaults.standard.set(modelAliases, forKey: Key.modelAliases)
        UserDefaults.standard.set(cardAliases, forKey: Key.cardAliases)
    }

    func forgetCameraNames() {
        modelAliases = [:]
        cardAliases = [:]
        UserDefaults.standard.removeObject(forKey: Key.modelAliases)
        UserDefaults.standard.removeObject(forKey: Key.cardAliases)
    }

    // MARK: - Import records

    func ledger(forCard cardID: String) -> ImportLedger {
        Self.read(ImportLedger.self, from: ledgerURL(cardID)) ?? ImportLedger()
    }

    func saveLedger(_ ledger: ImportLedger, forCard cardID: String) {
        Self.write(ledger, to: ledgerURL(cardID))
    }

    /// After this, every file on every card is offered again.
    func forgetImports() {
        try? FileManager.default.removeItem(at: importsDirectory)
        try? FileManager.default.createDirectory(at: importsDirectory, withIntermediateDirectories: true)
    }

    // MARK: - Keeping things in sync

    private func loadProjects() {
        guard let data = try? Data(contentsOf: projectsURL) else { return }
        if let cache = try? JSONCoding.decoder.decode(ProjectCache.self, from: data) {
            projects = cache.projects
            return
        }
        // Upgrade the older format, which stored each project's absolute folder path.
        guard let legacy = try? JSONDecoder().decode([LegacyProject].self, from: data) else { return }
        var idsByRoot: [String: UUID] = [:]
        for old in legacy where idsByRoot[old.rootPath] == nil {
            let id = UUID()
            idsByRoot[old.rootPath] = id
            if locations.values.contains(where: { $0.lastKnownPath == old.rootPath }) { continue }
            var location = LibraryLocator.location(of: URL(fileURLWithPath: old.rootPath, isDirectory: true), libraryID: id)
            location.markerPending = true
            locations[id] = location
        }
        projects = legacy.map { old in
            let id = locations.values.first { $0.lastKnownPath == old.rootPath }?.libraryID ?? idsByRoot[old.rootPath] ?? UUID()
            return ProjectInfo(id: old.id, name: old.name, startDay: old.startDay, libraryID: id, rootPath: old.rootPath, lastUsed: old.lastUsed)
        }
        saveLocations()
        saveCache()
    }

    /// Adds projects found in a projects folder's manifest (e.g. created on another computer).
    private func merge(_ manifest: LibraryManifest, at root: URL) {
        var changed = false
        for var incoming in manifest.projects {
            let known = projects.first { $0.id == incoming.id }
            if incoming.hasSeparateVideoFolder, incoming.videoLibraryID == manifest.libraryID {
                // A videos folder lists the projects whose videos it holds; they belong to another folder.
                incoming.videoRootPath = root.path
                incoming.rootPath = known?.rootPath ?? ""
            } else {
                incoming.libraryID = manifest.libraryID
                incoming.rootPath = root.path
                incoming.videoRootPath = known?.videoRootPath ?? ""
            }
            if let index = projects.firstIndex(where: { $0.id == incoming.id }) {
                if incoming.lastUsed > projects[index].lastUsed {
                    projects[index] = incoming
                    changed = true
                }
            } else {
                projects.append(incoming)
                changed = true
            }
        }
        if changed {
            saveCache()
        }
    }

    private func setRoot(_ root: URL, for libraryID: UUID) {
        for index in projects.indices {
            if projects[index].libraryID == libraryID {
                projects[index].rootPath = root.path
            }
            if projects[index].videoLibraryID == libraryID {
                projects[index].videoRootPath = root.path
            }
        }
    }

    /// Rewrites the folder's manifest if it would change, so connecting a drive doesn't
    /// rewrite files on it needlessly. The folder's structure is kept.
    private func writeManifest(for libraryID: UUID, at root: URL) {
        let existing = LibraryManifest.read(from: root)
        if let existing, existing.libraryID != libraryID { return }
        let updated = LibraryManifest(
            libraryID: libraryID,
            projects: projects.filter { $0.libraryID == libraryID || ($0.hasSeparateVideoFolder && $0.videoLibraryID == libraryID) },
            structure: existing?.structure,
            videoStructure: existing?.videoStructure
        )
        if let existing, Self.encoded(existing) == Self.encoded(updated) { return }
        try? updated.write(to: root)
    }

    /// The manifest exactly as it would be written (sorted, dates in whole seconds).
    private static func encoded(_ manifest: LibraryManifest) -> Data? {
        var copy = manifest
        copy.projects = manifest.projects.map(\.portable).sorted { $0.id.uuidString < $1.id.uuidString }
        return try? JSONCoding.encoder.encode(copy)
    }

    private func saveCache() {
        Self.write(ProjectCache(projects: projects.map(\.portable)), to: projectsURL)
    }

    private func saveLocations() {
        Self.write(locations.values.sorted { $0.lastKnownPath < $1.lastKnownPath }, to: librariesURL)
    }

    private var projectsURL: URL { directory.appendingPathComponent("projects.json") }
    private var librariesURL: URL { directory.appendingPathComponent("libraries.json") }
    private var importsDirectory: URL { directory.appendingPathComponent("Imports", isDirectory: true) }

    private func ledgerURL(_ cardID: String) -> URL {
        importsDirectory.appendingPathComponent(BackupEngine.sanitizedFolderName(cardID, fallback: "card") + ".json")
    }

    private static func read<T: Decodable>(_ type: T.Type, from url: URL) -> T? {
        guard let data = try? Data(contentsOf: url) else { return nil }
        return try? JSONCoding.decoder.decode(type, from: data)
    }

    private static func write<T: Encodable>(_ value: T, to url: URL) {
        guard let data = try? JSONCoding.encoder.encode(value) else { return }
        try? data.write(to: url, options: .atomic)
    }
}
