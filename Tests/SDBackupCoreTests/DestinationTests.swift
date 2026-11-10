import XCTest
@testable import SDBackupCore

/// Photos and videos with their own structures and folders, and multi-day shoots (1.3).
final class DestinationTests: XCTestCase {
    private let fileManager = FileManager.default
    private var sandbox: URL!
    private var projects: URL!
    private var videos: URL!
    private let day = ShootDay(year: 2026, month: 11, day: 2)

    override func setUpWithError() throws {
        sandbox = fileManager.temporaryDirectory.appendingPathComponent("SDDestinationTests-\(UUID().uuidString)", isDirectory: true)
        projects = sandbox.appendingPathComponent("Projects", isDirectory: true)
        videos = sandbox.appendingPathComponent("SSD/Videos", isDirectory: true)
        try fileManager.createDirectory(at: projects, withIntermediateDirectories: true)
        try fileManager.createDirectory(at: videos, withIntermediateDirectories: true)
    }

    override func tearDownWithError() throws {
        try? fileManager.removeItem(at: sandbox)
    }

    // MARK: - Helpers

    /// Tilen's photos: 2026 › Photo › Unedited / Exported › project.
    private let photoStructure = FolderStructure(levels: [.mediaType(), .split(), .project()])
    /// His videos: 2026 › Video › project, with the usual video folders.
    private let videoStructure = FolderStructure(levels: [.mediaType(), .project()])

    private func makeProject(series: String? = nil) -> ProjectInfo {
        ProjectInfo(name: "Ana", startDay: day, structure: photoStructure, videoStructure: videoStructure, series: series, rootPath: projects.path)
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

    // MARK: - Own structures in one folder

    func testPhotosAndVideosHaveTheirOwnStructures() async throws {
        let project = makeProject()
        XCTAssertFalse(project.hasLegacyVideoLayout)
        XCTAssertEqual(ProjectLayout.targetDirectory(kind: .photo, project: project, camera: "R5").path,
                       under(projects, ["2026", "Photo", "Unedited", "02.11.2026 Ana", "R5"]))
        XCTAssertEqual(ProjectLayout.exportedFolder(.photo, for: project)?.path,
                       under(projects, ["2026", "Photo", "Exported", "02.11.2026 Ana"]))
        XCTAssertEqual(ProjectLayout.targetDirectory(kind: .video, project: project, camera: "R5").path,
                       under(projects, ["2026", "Video", "02.11.2026 Ana", "Media", "R5"]))
        XCTAssertNil(ProjectLayout.exportedFolder(.video, for: project))

        try await importCard(into: project)
        XCTAssertTrue(exists(projects, "2026/Photo/Unedited/02.11.2026 Ana/R5/IMG_0001.CR3"))
        XCTAssertTrue(exists(projects, "2026/Photo/Unedited/02.11.2026 Ana/R5/IMG_0001.XMP"))
        XCTAssertTrue(exists(projects, "2026/Photo/Exported/02.11.2026 Ana"))
        XCTAssertTrue(exists(projects, "2026/Video/02.11.2026 Ana/Media/R5/MVI_0002.MP4"))
        XCTAssertTrue(exists(projects, "2026/Video/02.11.2026 Ana/Media/Zoom H5/ZOOM0001.WAV"))
        XCTAssertTrue(exists(projects, "2026/Video/02.11.2026 Ana/Export"))
        XCTAssertFalse(exists(projects, "2026/Video/Unedited"))
    }

    func testDifferentFoldersLeaveOutThePhotoVideoLevel() {
        XCTAssertEqual(FolderStructure.standard.withoutMediaType.levels.map(\.kind), [.month, .project])
        XCTAssertFalse(FolderStructure.standard.withoutMediaType.hasMediaType)

        let project = ProjectInfo(
            name: "Ana", startDay: day,
            structure: FolderStructure.standard.withoutMediaType,
            videoLibraryID: UUID(), videoStructure: FolderStructure.standard.withoutMediaType,
            rootPath: projects.path, videoRootPath: videos.path
        )
        XCTAssertTrue(project.hasSeparateVideoFolder)
        XCTAssertEqual(ProjectLayout.targetDirectory(kind: .photo, project: project, camera: "R5").path,
                       under(projects, ["2026", "11 November", "02.11.2026 Ana", "R5"]))
        XCTAssertEqual(ProjectLayout.targetDirectory(kind: .video, project: project, camera: "R5").path,
                       under(videos, ["2026", "11 November", "02.11.2026 Ana", "Media", "R5"]))
    }

    // MARK: - Multi-day shoots

    func testAMultiDayShootsFolderTakesTheProjectFoldersPlace() async throws {
        let project = makeProject(series: "AMD Koledar 2026")
        XCTAssertEqual(ProjectLayout.targetDirectory(kind: .photo, project: project, camera: "R5").path,
                       under(projects, ["2026", "Photo", "Unedited", "AMD Koledar 2026", "02.11.2026 Ana", "R5"]))
        XCTAssertEqual(ProjectLayout.exportedFolder(.photo, for: project)?.path,
                       under(projects, ["2026", "Photo", "Exported", "AMD Koledar 2026", "02.11.2026 Ana"]))
        XCTAssertEqual(ProjectLayout.targetDirectory(kind: .video, project: project, camera: "R5").path,
                       under(projects, ["2026", "Video", "AMD Koledar 2026", "02.11.2026 Ana", "Media", "R5"]))
        XCTAssertEqual(ProjectLayout.projectFolders(for: project).map(\.path), [
            under(projects, ["2026", "Photo", "Unedited", "AMD Koledar 2026", "02.11.2026 Ana"]),
            under(projects, ["2026", "Video", "AMD Koledar 2026", "02.11.2026 Ana"]),
        ])

        // With a month level, the shoot's folder sits in the month of each shoot.
        XCTAssertEqual(FolderStructure.standard.filesPath(.photo, name: "Ana", startDay: day, series: "AMD Koledar 2026"),
                       ["2026", "Photo", "11 November", "AMD Koledar 2026", "02.11.2026 Ana"])
        // The shoot's name is made safe like a project name.
        XCTAssertEqual(FolderStructure.standard.filesPath(.photo, name: "Ana", startDay: day, series: " a/b "),
                       ["2026", "Photo", "11 November", "a-b", "02.11.2026 Ana"])
        XCTAssertNil(ProjectInfo(name: "x", startDay: day, series: "  ").series)

        try await importCard(into: project)
        XCTAssertTrue(exists(projects, "2026/Photo/Unedited/AMD Koledar 2026/02.11.2026 Ana/R5/IMG_0001.CR3"))
        XCTAssertTrue(exists(projects, "2026/Photo/Exported/AMD Koledar 2026/02.11.2026 Ana"))
        XCTAssertTrue(exists(projects, "2026/Video/AMD Koledar 2026/02.11.2026 Ana/Media/R5/MVI_0002.MP4"))
    }

    // MARK: - Folders chosen per media

    func testPhotosAndVideosCanEachGoToAChosenFolder() {
        // Photos into a chosen folder; videos stay at their default destination.
        var photosChosen = makeProject()
        photosChosen.folder = ["Clients", "02.11.2026 Ana"]
        XCTAssertEqual(ProjectLayout.targetDirectory(kind: .photo, project: photosChosen, camera: "R5").path,
                       under(projects, ["Clients", "02.11.2026 Ana", "R5"]))
        XCTAssertEqual(ProjectLayout.targetDirectory(kind: .video, project: photosChosen, camera: "R5").path,
                       under(projects, ["2026", "Video", "02.11.2026 Ana", "Media", "R5"]))
        XCTAssertNil(ProjectLayout.exportedFolder(.photo, for: photosChosen), "The split is above the project folder")

        // Videos into a chosen folder on the videos drive.
        var videosChosen = makeProject()
        videosChosen.videoLibraryID = UUID()
        videosChosen.videoRootPath = videos.path
        videosChosen.videoFolder = ["Edits", "02.11.2026 Ana"]
        XCTAssertTrue(videosChosen.hasSeparateVideoFolder)
        XCTAssertEqual(ProjectLayout.targetDirectory(kind: .video, project: videosChosen, camera: "R5").path,
                       under(videos, ["Edits", "02.11.2026 Ana", "Media", "R5"]))
        XCTAssertEqual(ProjectLayout.targetDirectory(kind: .photo, project: videosChosen, camera: "R5").path,
                       under(projects, ["2026", "Photo", "Unedited", "02.11.2026 Ana", "R5"]))
        XCTAssertEqual(ProjectLayout.projectFolders(for: videosChosen).map(\.path), [
            under(projects, ["2026", "Photo", "Unedited", "02.11.2026 Ana"]),
            under(videos, ["Edits", "02.11.2026 Ana"]),
        ])
    }

    func testProjectsFromBefore13KeepTheirVideosWithThePhotos() {
        // 1.2: a project in a chosen folder put its videos there too; no video structure was saved.
        var legacy = ProjectInfo(name: "Ana", startDay: day, folder: ["2026", "AMD Koledar 2026", "02.11.2026 Ana"], rootPath: projects.path)
        XCTAssertTrue(legacy.hasLegacyVideoLayout)
        XCTAssertEqual(ProjectLayout.targetDirectory(kind: .video, project: legacy, camera: "R5").path,
                       under(projects, ["2026", "AMD Koledar 2026", "02.11.2026 Ana", "Media", "R5"]))

        // A 1.3 project with the same photo folder keeps its videos at their default destination.
        legacy.videoStructure = .standard
        XCTAssertEqual(ProjectLayout.targetDirectory(kind: .video, project: legacy, camera: "R5").path,
                       under(projects, ["2026", "Video", "11 November", "02.11.2026 Ana", "Media", "R5"]))
    }

    // MARK: - Saved records

    func testNewFieldsRoundTripThroughTheManifest() throws {
        let libraryID = UUID()
        var project = makeProject(series: "AMD Koledar 2026")
        project.libraryID = libraryID
        project.videoFolder = ["Edits", "02.11.2026 Ana"]
        project.lastUsed = Date(timeIntervalSince1970: 1_793_628_480)
        try LibraryManifest(libraryID: libraryID, projects: [project], structure: photoStructure, videoStructure: videoStructure).write(to: projects)

        let json = try String(contentsOf: LibraryManifest.url(in: projects), encoding: .utf8)
        XCTAssertTrue(json.contains("\"series\" : \"AMD Koledar 2026\""))
        XCTAssertTrue(json.contains("\"videoFolder\" : ["))
        XCTAssertTrue(json.contains("\"videoStructure\" : {"))

        let read = try XCTUnwrap(LibraryManifest.read(from: projects))
        XCTAssertEqual(read.layout, photoStructure)
        XCTAssertEqual(read.videoLayout, videoStructure)
        XCTAssertEqual(read.projects.first, project.portable)
    }

    func testFoldersSetUpBefore13UseThePhotoStructureForVideos() throws {
        try LibraryManifest(projects: [], structure: photoStructure).write(to: projects)
        let manifest = try XCTUnwrap(LibraryManifest.read(from: projects))
        XCTAssertNil(manifest.videoStructure)
        XCTAssertEqual(manifest.videoLayout, photoStructure)
    }

    func testReadsTheNewFieldsWrittenByWindows() throws {
        // What the Windows version (System.Text.Json, declaration order) writes.
        let windowsJSON = """
        {
          "formatVersion": 1,
          "libraryID": "7F1C2E0A-3B9D-4C51-9E2A-6D8B1F4A2C33",
          "projects": [
            {
              "id": "C3A15B7E-0D2F-4E8A-9B61-2F7C4D9E8A10",
              "lastUsed": "2026-11-02T14:08:00Z",
              "libraryID": "7F1C2E0A-3B9D-4C51-9E2A-6D8B1F4A2C33",
              "name": "Ana",
              "series": "AMD Koledar 2026",
              "startDay": { "year": 2026, "month": 11, "day": 2 },
              "videoFolder": [ "Edits", "02.11.2026 Ana" ],
              "videoStructure": { "cameraFolders": true, "levels": [ { "kind": "project" } ], "mediaFolder": "Media", "videoFolders": [ "Media" ] }
            }
          ],
          "videoStructure": { "cameraFolders": false, "levels": [ { "kind": "project" } ], "mediaFolder": "", "videoFolders": [] }
        }
        """
        let folder = projects.appendingPathComponent(".sdbackup", isDirectory: true)
        try fileManager.createDirectory(at: folder, withIntermediateDirectories: true)
        try Data(windowsJSON.utf8).write(to: folder.appendingPathComponent("library.json"))

        let manifest = try XCTUnwrap(LibraryManifest.read(from: projects))
        XCTAssertFalse(manifest.videoLayout.cameraFolders)
        let project = try XCTUnwrap(manifest.projects.first)
        XCTAssertEqual(project.series, "AMD Koledar 2026")
        XCTAssertEqual(project.videoFolder, ["Edits", "02.11.2026 Ana"])
        XCTAssertEqual(project.videoLayout.levels.map(\.kind), [.project])
    }

    func testAVideoStructureItCantReadIsTheStandardOne() throws {
        let json = """
        { "formatVersion": 1, "libraryID": "7F1C2E0A-3B9D-4C51-9E2A-6D8B1F4A2C33", "projects": [
          { "id": "C3A15B7E-0D2F-4E8A-9B61-2F7C4D9E8A10", "lastUsed": "2026-11-02T14:08:00Z", "libraryID": "7F1C2E0A-3B9D-4C51-9E2A-6D8B1F4A2C33",
            "name": "Ana", "startDay": { "day": 2, "month": 11, "year": 2026 }, "videoStructure": { "levels": [ { "kind": "weekday" } ] } } ] }
        """
        let folder = projects.appendingPathComponent(".sdbackup", isDirectory: true)
        try fileManager.createDirectory(at: folder, withIntermediateDirectories: true)
        try Data(json.utf8).write(to: folder.appendingPathComponent("library.json"))

        let project = try XCTUnwrap(LibraryManifest.read(from: projects)?.projects.first)
        XCTAssertFalse(project.hasLegacyVideoLayout)
        XCTAssertEqual(project.videoLayout, .standard)
    }
}
