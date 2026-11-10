import XCTest
@testable import SDBackupCore

/// Projects created in a folder the user chose, and videos in a separate projects folder.
final class ProjectLocationTests: XCTestCase {
    private let fileManager = FileManager.default
    private var sandbox: URL!
    private var projects: URL!
    private var videos: URL!
    private let day = ShootDay(year: 2026, month: 11, day: 2)
    private let calendarFolder = ["2026", "AMD Koledar 2026", "02.11.2026 Ana"]

    override func setUpWithError() throws {
        sandbox = fileManager.temporaryDirectory.appendingPathComponent("SDLocationTests-\(UUID().uuidString)", isDirectory: true)
        projects = sandbox.appendingPathComponent("Projects", isDirectory: true)
        videos = sandbox.appendingPathComponent("SSD/Videos", isDirectory: true)
        try fileManager.createDirectory(at: projects, withIntermediateDirectories: true)
        try fileManager.createDirectory(at: videos, withIntermediateDirectories: true)
    }

    override func tearDownWithError() throws {
        try? fileManager.removeItem(at: sandbox)
    }

    // MARK: - Helpers

    private func inChosenFolder(_ structure: FolderStructure? = nil) -> ProjectInfo {
        ProjectInfo(name: "Ana", startDay: day, structure: structure, folder: calendarFolder, rootPath: projects.path)
    }

    private func withSeparateVideos(_ structure: FolderStructure? = nil) -> ProjectInfo {
        ProjectInfo(name: "Ana Wedding", startDay: day, structure: structure, videoLibraryID: UUID(), rootPath: projects.path, videoRootPath: videos.path)
    }

    private func under(_ root: URL, _ components: [String]) -> String {
        components.reduce(root) { $0.appendingPathComponent($1, isDirectory: true) }.path
    }

    private func exists(_ root: URL, _ path: String) -> Bool {
        fileManager.fileExists(atPath: root.appendingPathComponent(path).path)
    }

    private struct NoMetadata: MetadataReading {
        func photoInfo(at url: URL) -> CaptureInfo { CaptureInfo() }
        func videoInfo(at url: URL) async -> CaptureInfo { CaptureInfo() }
    }

    /// Copies a card with a photo, its XMP, a clip and recorder audio (all on 2 November) into the project.
    @discardableResult
    private func importCard(into project: ProjectInfo) async throws -> ImportPlan {
        let card = sandbox.appendingPathComponent("CARD", isDirectory: true)
        let shot = Calendar.current.date(from: DateComponents(year: 2026, month: 11, day: 2, hour: 10))!
        for (path, contents) in [
            ("DCIM/100CANON/IMG_0001.CR3", "raw photo"),
            ("DCIM/100CANON/IMG_0001.XMP", "xmp"),
            ("DCIM/100CANON/MVI_0002.MP4", "canon clip"),
            ("ZOOM0001.WAV", "audio"),
        ] {
            let url = card.appendingPathComponent(path)
            try fileManager.createDirectory(at: url.deletingLastPathComponent(), withIntermediateDirectories: true)
            try Data(contents.utf8).write(to: url)
            try fileManager.setAttributes([.modificationDate: shot, .creationDate: shot], ofItemAtPath: url.path)
        }
        let analysis = try await CardAnalyzer(reader: NoMetadata()).analyze(source: card, isAlreadyImported: { _ in false })
        var assigned: [ShootDay: ProjectInfo] = [:]
        for summary in analysis.days {
            assigned[summary.day] = project
        }
        let plan = ProjectLayout.plan(items: analysis.items, projects: assigned) { $0.kind == .audio ? "Zoom H5" : "R5" }
        let result = try BackupEngine(options: BackupOptions(reservedFreeSpace: 0))
            .copy(plan.items, from: card, to: URL(fileURLWithPath: project.rootPath, isDirectory: true), createFolders: plan.foldersToCreate)
        XCTAssertTrue(result.failures.isEmpty)
        XCTAssertEqual(result.filesCopied, 4)
        return plan
    }

    // MARK: - A project in a chosen folder

    func testProjectInAChosenFolderPutsEverythingInIt() async throws {
        let project = inChosenFolder()
        let folder = under(projects, calendarFolder)

        XCTAssertEqual(project.folderName, "02.11.2026 Ana")
        XCTAssertFalse(project.hasSeparateVideoFolder)
        XCTAssertEqual(ProjectLayout.photoFolder(for: project).path, folder)
        XCTAssertEqual(ProjectLayout.videoFolder(for: project).path, folder)
        XCTAssertEqual(ProjectLayout.projectFolders(for: project).map(\.path), [folder])
        XCTAssertEqual(ProjectLayout.targetDirectory(kind: .photo, project: project, camera: "R5").path, folder + "/R5")
        XCTAssertEqual(ProjectLayout.targetDirectory(kind: .video, project: project, camera: "R5").path, folder + "/Media/R5")

        try await importCard(into: project)
        let root = URL(fileURLWithPath: folder, isDirectory: true)
        for path in ["R5/IMG_0001.CR3", "R5/IMG_0001.XMP", "Media/R5/MVI_0002.MP4", "Media/Zoom H5/ZOOM0001.WAV", "Project", "Assets", "Export"] {
            XCTAssertTrue(exists(root, path), "Missing \(path)")
        }
        // Nothing is made where the structure would have put the project.
        XCTAssertEqual(try fileManager.contentsOfDirectory(atPath: projects.appendingPathComponent("2026").path), ["AMD Koledar 2026"])
    }

    func testAChosenFolderKeepsTheStructuresFoldersInsideTheProject() async throws {
        // Unedited / Exported inside the project folder still applies.
        let inside = inChosenFolder(FolderStructure(levels: [.mediaType(), .month(), .project(), .split()], cameraFolders: false))
        let folder = under(projects, calendarFolder)
        XCTAssertEqual(ProjectLayout.targetDirectory(kind: .photo, project: inside, camera: "R5").path, folder + "/Unedited")
        XCTAssertEqual(ProjectLayout.exportedFolder(.photo, for: inside)?.path, folder + "/Exported")
        XCTAssertEqual(ProjectLayout.projectFolders(for: inside).map(\.path), [folder])

        try await importCard(into: inside)
        let root = URL(fileURLWithPath: folder, isDirectory: true)
        XCTAssertTrue(exists(root, "Unedited/IMG_0001.CR3"))
        XCTAssertTrue(exists(root, "Exported"))

        // The levels above the project folder (here an Unedited / Exported split) don't.
        let above = inChosenFolder(FolderStructure(levels: [.mediaType(), .split(), .month(), .project()]))
        XCTAssertEqual(ProjectLayout.targetDirectory(kind: .photo, project: above, camera: "R5").path, folder + "/R5")
        XCTAssertNil(ProjectLayout.exportedFolder(.photo, for: above))
    }

    // MARK: - Videos in a separate projects folder

    func testVideosGoToTheirOwnProjectsFolder() async throws {
        let project = withSeparateVideos()
        XCTAssertTrue(project.hasSeparateVideoFolder)
        XCTAssertEqual(project.videoRoot, videos.path)
        XCTAssertEqual(
            ProjectLayout.targetDirectory(kind: .photo, project: project, camera: "R5").path,
            under(projects, ["2026", "Photo", "11 November", "02.11.2026 Ana Wedding", "R5"])
        )
        XCTAssertEqual(
            ProjectLayout.targetDirectory(kind: .video, project: project, camera: "R6").path,
            under(videos, ["2026", "Video", "11 November", "02.11.2026 Ana Wedding", "Media", "R6"])
        )
        XCTAssertEqual(ProjectLayout.projectFolders(for: project).map(\.path), [
            under(projects, ["2026", "Photo", "11 November", "02.11.2026 Ana Wedding"]),
            under(videos, ["2026", "Video", "11 November", "02.11.2026 Ana Wedding"]),
        ])

        let plan = try await importCard(into: project)
        XCTAssertTrue(exists(projects, "2026/Photo/11 November/02.11.2026 Ana Wedding/R5/IMG_0001.CR3"))
        XCTAssertTrue(exists(videos, "2026/Video/11 November/02.11.2026 Ana Wedding/Media/R5/MVI_0002.MP4"))
        XCTAssertTrue(exists(videos, "2026/Video/11 November/02.11.2026 Ana Wedding/Media/Zoom H5/ZOOM0001.WAV"))
        XCTAssertTrue(plan.foldersToCreate.allSatisfy { $0.path.hasPrefix(videos.path) })
        XCTAssertFalse(exists(projects, "2026/Video"))
    }

    func testExportedFoldersFollowTheirFiles() {
        let project = withSeparateVideos(FolderStructure(levels: [.mediaType(), .split(), .month(), .project()]))
        XCTAssertEqual(
            ProjectLayout.exportedFolder(.photo, for: project)?.path,
            under(projects, ["2026", "Photo", "Exported", "11 November", "02.11.2026 Ana Wedding"])
        )
        XCTAssertEqual(
            ProjectLayout.exportedFolder(.video, for: project)?.path,
            under(videos, ["2026", "Video", "Exported", "11 November", "02.11.2026 Ana Wedding"])
        )
    }

    func testAProjectInAChosenFolderKeepsItsVideosThere() {
        var project = inChosenFolder()
        project.videoLibraryID = UUID()
        project.videoRootPath = videos.path
        XCTAssertFalse(project.hasSeparateVideoFolder)
        XCTAssertEqual(
            ProjectLayout.targetDirectory(kind: .video, project: project, camera: "R5").path,
            under(projects, calendarFolder + ["Media", "R5"])
        )
    }

    // MARK: - Saved records

    func testNewFieldsRoundTripThroughTheManifestWithoutLocalPaths() throws {
        let libraryID = UUID()
        let videosID = UUID()
        let lastUsed = Date(timeIntervalSince1970: 1_793_628_480) // Whole seconds, as stored.
        let chosen = ProjectInfo(name: "Ana", startDay: day, libraryID: libraryID, folder: calendarFolder, rootPath: projects.path, lastUsed: lastUsed)
        let separate = ProjectInfo(
            name: "Ana Wedding", startDay: day, libraryID: libraryID, videoLibraryID: videosID,
            rootPath: projects.path, videoRootPath: videos.path, lastUsed: lastUsed
        )
        try LibraryManifest(libraryID: libraryID, projects: [chosen, separate]).write(to: projects)

        let json = try String(contentsOf: LibraryManifest.url(in: projects), encoding: .utf8)
        XCTAssertTrue(json.contains("\"folder\" : ["))
        XCTAssertTrue(json.contains("\"AMD Koledar 2026\""))
        XCTAssertTrue(json.contains("\"videoLibraryID\" : \"\(videosID.uuidString)\""))
        XCTAssertFalse(json.contains("SSD"), "The videos' location stays on this computer")

        let read = try XCTUnwrap(LibraryManifest.read(from: projects))
        XCTAssertEqual(read.projects.first { $0.id == chosen.id }?.folder, calendarFolder)
        let readSeparate = try XCTUnwrap(read.projects.first { $0.id == separate.id })
        XCTAssertEqual(readSeparate.videoLibraryID, videosID)
        XCTAssertEqual(readSeparate.videoRootPath, "")
        XCTAssertEqual(readSeparate, separate.portable)
    }

    func testTheVideosManifestListsItsProjectsWithoutTakingThemOver() throws {
        let photosID = UUID()
        let videosID = UUID()
        let project = ProjectInfo(name: "Ana Wedding", startDay: day, libraryID: photosID, videoLibraryID: videosID)
        try LibraryManifest(libraryID: videosID, projects: [project]).write(to: videos)

        let listed = try XCTUnwrap(LibraryManifest.read(from: videos)?.projects.first)
        XCTAssertEqual(listed.libraryID, photosID)
        XCTAssertEqual(listed.videoLibraryID, videosID)
    }

    func testReadsTheNewFieldsWrittenByWindows() throws {
        // What the Windows version (System.Text.Json, declaration order) writes.
        let windowsJSON = """
        {
          "formatVersion": 1,
          "libraryID": "7F1C2E0A-3B9D-4C51-9E2A-6D8B1F4A2C33",
          "projects": [
            {
              "folder": [ "2026", "AMD Koledar 2026", "02.11.2026 Ana" ],
              "id": "C3A15B7E-0D2F-4E8A-9B61-2F7C4D9E8A10",
              "lastUsed": "2026-11-02T14:08:00Z",
              "libraryID": "7F1C2E0A-3B9D-4C51-9E2A-6D8B1F4A2C33",
              "name": "Ana",
              "startDay": { "year": 2026, "month": 11, "day": 2 },
              "videoLibraryID": "0B4E3C2A-1D5F-4A6B-8C7D-9E0F1A2B3C4D"
            }
          ]
        }
        """
        let folder = projects.appendingPathComponent(".sdbackup", isDirectory: true)
        try fileManager.createDirectory(at: folder, withIntermediateDirectories: true)
        try Data(windowsJSON.utf8).write(to: folder.appendingPathComponent("library.json"))

        let project = try XCTUnwrap(LibraryManifest.read(from: projects)?.projects.first)
        XCTAssertEqual(project.folder, calendarFolder)
        XCTAssertEqual(project.videoLibraryID, UUID(uuidString: "0B4E3C2A-1D5F-4A6B-8C7D-9E0F1A2B3C4D"))
        XCTAssertEqual(project.folderName, "02.11.2026 Ana")
    }

    func testProjectsWithoutTheNewFieldsAreUnchanged() {
        let project = ProjectInfo(name: "Ana Wedding", startDay: day, rootPath: projects.path)
        XCTAssertNil(project.folder)
        XCTAssertFalse(project.hasSeparateVideoFolder)
        XCTAssertEqual(project.videoLibrary, project.libraryID)
        XCTAssertEqual(ProjectLayout.videoFolder(for: project).path, under(projects, ["2026", "Video", "11 November", "02.11.2026 Ana Wedding"]))
        // A videos library equal to the project's own library means "the same folder".
        let libraryID = UUID()
        XCTAssertNil(ProjectInfo(name: "x", startDay: day, libraryID: libraryID, videoLibraryID: libraryID).videoLibraryID)
        XCTAssertNil(ProjectInfo(name: "x", startDay: day, folder: []).folder)
    }
}
