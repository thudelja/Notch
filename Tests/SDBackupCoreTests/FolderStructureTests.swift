import XCTest
@testable import SDBackupCore

final class FolderStructureTests: XCTestCase {
    private let fileManager = FileManager.default
    private var sandbox: URL!
    private var root: URL!
    private let day = ShootDay(year: 2026, month: 11, day: 2)

    override func setUpWithError() throws {
        sandbox = fileManager.temporaryDirectory.appendingPathComponent("SDStructureTests-\(UUID().uuidString)", isDirectory: true)
        root = sandbox.appendingPathComponent("Projects", isDirectory: true)
        try fileManager.createDirectory(at: root, withIntermediateDirectories: true)
    }

    override func tearDownWithError() throws {
        try? fileManager.removeItem(at: sandbox)
    }

    // MARK: - Helpers

    /// Tilen's example: 2026 › Photo|Video › Unedited|Exported › month › project.
    private let splitAboveProject = FolderStructure(levels: [.mediaType(), .split(), .month(), .project()])
    private let splitInsideProject = FolderStructure(levels: [.mediaType(), .month(), .project(), .split()])

    private func makeProject(_ structure: FolderStructure?, name: String = "Ana Wedding") -> ProjectInfo {
        ProjectInfo(name: name, startDay: day, structure: structure, rootPath: root.path)
    }

    private func under(_ components: String...) -> String {
        components.reduce(root) { $0.appendingPathComponent($1, isDirectory: true) }.path
    }

    private struct NoMetadata: MetadataReading {
        func photoInfo(at url: URL) -> CaptureInfo { CaptureInfo() }
        func videoInfo(at url: URL) async -> CaptureInfo { CaptureInfo() }
    }

    /// A Canon photo with its XMP, a Canon clip and recorder audio, all on 2 November.
    private func mixedCard() async throws -> (CardAnalysis, URL) {
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
        return (analysis, card)
    }

    @discardableResult
    private func importCard(_ structure: FolderStructure?) async throws -> ImportPlan {
        let (analysis, card) = try await mixedCard()
        let project = makeProject(structure)
        var projects: [ShootDay: ProjectInfo] = [:]
        for summary in analysis.days {
            projects[summary.day] = project
        }
        let plan = ProjectLayout.plan(items: analysis.items, projects: projects) { $0.kind == .audio ? "Zoom H5" : "R5" }
        let result = try BackupEngine(options: BackupOptions(reservedFreeSpace: 0))
            .copy(plan.items, from: card, to: root, createFolders: plan.foldersToCreate)
        XCTAssertTrue(result.failures.isEmpty)
        return plan
    }

    private func assertFiles(_ paths: String..., file: StaticString = #filePath, line: UInt = #line) {
        for path in paths {
            XCTAssertTrue(fileManager.fileExists(atPath: root.appendingPathComponent(path).path), "Missing \(path)", file: file, line: line)
        }
    }

    private func assertEmptyFolders(_ paths: String..., file: StaticString = #filePath, line: UInt = #line) {
        for path in paths {
            let folder = root.appendingPathComponent(path, isDirectory: true)
            var isDirectory: ObjCBool = false
            XCTAssertTrue(fileManager.fileExists(atPath: folder.path, isDirectory: &isDirectory) && isDirectory.boolValue, "Missing \(path)", file: file, line: line)
            XCTAssertEqual((try? fileManager.contentsOfDirectory(atPath: folder.path))?.count, 0, "\(path) isn't empty", file: file, line: line)
        }
    }

    // MARK: - The standard structure

    func testStandardStructureBuildsTodaysFolders() {
        let standard = FolderStructure.standard
        XCTAssertTrue(standard.isValid)
        XCTAssertFalse(standard.hasSplit)
        XCTAssertEqual(standard.filesPath(.photo, name: "Ana Wedding", startDay: day), ["2026", "Photo", "11 November", "02.11.2026 Ana Wedding"])
        XCTAssertEqual(standard.filesPath(.video, name: "Ana Wedding", startDay: day), ["2026", "Video", "11 November", "02.11.2026 Ana Wedding"])
        XCTAssertNil(standard.exportedPath(.photo, name: "Ana Wedding", startDay: day))
        XCTAssertEqual(standard.videoFolders, ["Media", "Project", "Assets", "Export"])
        // The caption Settings has always shown.
        XCTAssertEqual(standard.summary, [
            "Photos: 2026 › Photo › 11 November › 02.11.2026 Name › Camera",
            "Videos: 2026 › Video › 11 November › 02.11.2026 Name › Media › Camera, plus Project, Assets and Export folders.",
        ])
    }

    func testAStandardStructureIsStoredAsNone() {
        // Projects and manifests only record a structure that differs from the standard one.
        XCTAssertNil(makeProject(.standard).structure)
        XCTAssertNil(makeProject(FolderStructure(levels: [.mediaType(), .month(), .project()])).structure)
        XCTAssertNil(LibraryManifest(projects: [], structure: .standard).structure)
        XCTAssertNotNil(makeProject(splitAboveProject).structure)
        XCTAssertEqual(makeProject(nil).layout, .standard)
    }

    // MARK: - Custom structures

    func testMonthAndDateStyles() {
        XCTAssertEqual(FolderStructure.monthFolderName(day), "11 November")
        XCTAssertEqual(FolderStructure.monthFolderName(day, style: .number), "11")
        XCTAssertEqual(FolderStructure.monthFolderName(day, style: .name), "November")
        XCTAssertEqual(FolderStructure.monthFolderName(ShootDay(year: 2026, month: 3, day: 1), style: .numberAndName, language: .slovenian), "03 marec")
        XCTAssertEqual(FolderStructure.monthFolderName(ShootDay(year: 2026, month: 8, day: 1), style: .name, language: .slovenian), "avgust")

        XCTAssertEqual(FolderStructure.projectFolderName(name: "Ana Wedding", startDay: day), "02.11.2026 Ana Wedding")
        XCTAssertEqual(FolderStructure.projectFolderName(name: "Ana Wedding", startDay: day, level: .project(dateFormat: .yearMonthDay)), "2026-11-02 Ana Wedding")
        XCTAssertEqual(FolderStructure.projectFolderName(name: "Ana Wedding", startDay: day, level: .project(dateFormat: .compact)), "20261102 Ana Wedding")
        XCTAssertEqual(FolderStructure.projectFolderName(name: "Ana Wedding", startDay: day, level: .project(nameOrder: .nameFirst)), "Ana Wedding 02.11.2026")
        XCTAssertEqual(FolderStructure.projectFolderName(name: " a/b ", startDay: day, level: .project(dateFormat: .compact, nameOrder: .nameFirst)), "a-b 20261102")
    }

    func testCustomStructureWithoutPhotoVideoOrCameraFolders() async throws {
        // 2026 › Clients › 11 › "Ana Wedding 2026-11-02", photos and videos together, no camera folders.
        let structure = FolderStructure(
            levels: [.folder("Clients"), .month(style: .number), .project(dateFormat: .yearMonthDay, nameOrder: .nameFirst)],
            cameraFolders: false,
            videoFolders: ["Footage", "Edit"],
            mediaFolder: "Footage"
        )
        XCTAssertEqual(structure.problems, [])
        let project = makeProject(structure)
        let folder = under("2026", "Clients", "11", "Ana Wedding 2026-11-02")

        XCTAssertEqual(project.folderName, "Ana Wedding 2026-11-02")
        XCTAssertEqual(ProjectLayout.targetDirectory(kind: .photo, project: project, camera: "R5").path, folder)
        XCTAssertEqual(ProjectLayout.targetDirectory(kind: .video, project: project, camera: "R5").path, folder + "/Footage")
        XCTAssertEqual(structure.summary, [
            "Photos: 2026 › Clients › 11 › Name 2026-11-02",
            "Videos: 2026 › Clients › 11 › Name 2026-11-02 › Footage, plus Edit folder.",
        ])

        let plan = try await importCard(structure)
        assertFiles(
            "2026/Clients/11/Ana Wedding 2026-11-02/IMG_0001.CR3",
            "2026/Clients/11/Ana Wedding 2026-11-02/IMG_0001.XMP",
            "2026/Clients/11/Ana Wedding 2026-11-02/Footage/MVI_0002.MP4",
            "2026/Clients/11/Ana Wedding 2026-11-02/Footage/ZOOM0001.WAV"
        )
        assertEmptyFolders("2026/Clients/11/Ana Wedding 2026-11-02/Edit")
        XCTAssertEqual(plan.foldersToCreate.count, 2)
    }

    func testVideosCanGoStraightIntoTheProjectFolder() {
        var structure = FolderStructure.standard
        structure.videoFolders = ["Project"]
        structure.mediaFolder = ""
        XCTAssertTrue(structure.isValid)
        XCTAssertEqual(
            ProjectLayout.targetDirectory(kind: .video, project: makeProject(structure), camera: "R5").path,
            under("2026", "Video", "11 November", "02.11.2026 Ana Wedding", "R5")
        )
    }

    // MARK: - Unedited / Exported

    func testSplitAboveTheProjectFolder() async throws {
        let structure = splitAboveProject
        XCTAssertTrue(structure.isValid)
        XCTAssertEqual(structure.filesPath(.photo, name: "Ana Wedding", startDay: day), ["2026", "Photo", "Unedited", "11 November", "02.11.2026 Ana Wedding"])
        XCTAssertEqual(structure.exportedPath(.photo, name: "Ana Wedding", startDay: day), ["2026", "Photo", "Exported", "11 November", "02.11.2026 Ana Wedding"])
        XCTAssertEqual(structure.exportedPath(.video, name: "Ana Wedding", startDay: day), ["2026", "Video", "Exported", "11 November", "02.11.2026 Ana Wedding"])
        XCTAssertEqual(structure.summary[2], "Exports: 2026 › Photo › Exported › 11 November › 02.11.2026 Name and 2026 › Video › Exported › 11 November › 02.11.2026 Name")
        XCTAssertEqual(structure.examples(separator: "/"), [
            FolderExample(title: "A photo", path: "2026/Photo/Unedited/11 November/02.11.2026 Ana Wedding/R5/IMG_0001.CR3"),
            FolderExample(title: "A video", path: "2026/Video/Unedited/11 November/02.11.2026 Ana Wedding/Media/R5/C0001.MP4"),
            FolderExample(title: "Photo exports", path: "2026/Photo/Exported/11 November/02.11.2026 Ana Wedding/"),
            FolderExample(title: "Video exports", path: "2026/Video/Exported/11 November/02.11.2026 Ana Wedding/"),
        ])

        try await importCard(structure)
        assertFiles(
            "2026/Photo/Unedited/11 November/02.11.2026 Ana Wedding/R5/IMG_0001.CR3",
            "2026/Photo/Unedited/11 November/02.11.2026 Ana Wedding/R5/IMG_0001.XMP",
            "2026/Video/Unedited/11 November/02.11.2026 Ana Wedding/Media/R5/MVI_0002.MP4",
            "2026/Video/Unedited/11 November/02.11.2026 Ana Wedding/Media/Zoom H5/ZOOM0001.WAV"
        )
        assertEmptyFolders(
            "2026/Photo/Exported/11 November/02.11.2026 Ana Wedding",
            "2026/Video/Exported/11 November/02.11.2026 Ana Wedding",
            "2026/Video/Unedited/11 November/02.11.2026 Ana Wedding/Export"
        )
    }

    func testSplitInsideTheProjectFolder() async throws {
        let structure = FolderStructure(levels: [.mediaType(), .month(), .project(), .split(unedited: "RAW", exported: "Final")])
        XCTAssertTrue(structure.isValid)
        XCTAssertEqual(
            ProjectLayout.targetDirectory(kind: .photo, project: makeProject(structure), camera: "R5").path,
            under("2026", "Photo", "11 November", "02.11.2026 Ana Wedding", "RAW", "R5")
        )
        XCTAssertEqual(structure.exportedPath(.photo, name: "Ana Wedding", startDay: day), ["2026", "Photo", "11 November", "02.11.2026 Ana Wedding", "Final"])

        try await importCard(structure)
        assertFiles(
            "2026/Photo/11 November/02.11.2026 Ana Wedding/RAW/R5/IMG_0001.CR3",
            "2026/Video/11 November/02.11.2026 Ana Wedding/RAW/Media/R5/MVI_0002.MP4"
        )
        assertEmptyFolders(
            "2026/Photo/11 November/02.11.2026 Ana Wedding/Final",
            "2026/Video/11 November/02.11.2026 Ana Wedding/Final"
        )
    }

    func testOnlyBranchesThatReceiveFilesGetAnExportedFolder() async throws {
        let (analysis, _) = try await mixedCard()
        let photos = analysis.items.filter { $0.kind == .photo }

        let plan = ProjectLayout.plan(items: photos, projects: [day: makeProject(splitAboveProject)]) { _ in "R5" }

        XCTAssertEqual(plan.foldersToCreate.map(\.path), [under("2026", "Photo", "Exported", "11 November", "02.11.2026 Ana Wedding")])
    }

    func testWithoutPhotoVideoFoldersOneExportedFolderIsShared() async throws {
        let structure = FolderStructure(levels: [.split(), .project()], cameraFolders: false)
        XCTAssertEqual(structure.summary[2], "Exports: 2026 › Exported › 02.11.2026 Name")
        XCTAssertEqual(structure.examples(separator: "/"), [
            FolderExample(title: "A photo", path: "2026/Unedited/02.11.2026 Ana Wedding/IMG_0001.CR3"),
            FolderExample(title: "A video", path: "2026/Unedited/02.11.2026 Ana Wedding/Media/C0001.MP4"),
            FolderExample(title: "Exports", path: "2026/Exported/02.11.2026 Ana Wedding/"),
        ])

        let plan = try await importCard(structure)
        XCTAssertEqual(plan.foldersToCreate.filter { $0.path.hasSuffix("Exported/02.11.2026 Ana Wedding") }.count, 1)
        assertEmptyFolders("2026/Exported/02.11.2026 Ana Wedding")
        assertFiles("2026/Unedited/02.11.2026 Ana Wedding/IMG_0001.CR3", "2026/Unedited/02.11.2026 Ana Wedding/Media/MVI_0002.MP4")
    }

    // MARK: - Checking

    func testValidationExplainsWhatIsWrong() {
        func problems(_ levels: [FolderLevel]) -> [String] { FolderStructure(levels: levels).problems }
        func videoProblems(_ videoFolders: [String], mediaFolder: String = "Media") -> [String] {
            var structure = FolderStructure.standard
            structure.videoFolders = videoFolders
            structure.mediaFolder = mediaFolder
            return structure.problems
        }

        XCTAssertTrue(problems([.mediaType()]).contains("The structure needs exactly one project folder."))
        XCTAssertTrue(problems([.project(), .project()]).contains("The structure needs exactly one project folder."))
        XCTAssertTrue(problems([.project(), .month()]).contains("Only the Unedited / Exported folders can go inside the project folder."))
        XCTAssertTrue(problems([.split(), .project(), .split()]).contains("Use Unedited / Exported only once."))
        XCTAssertTrue(problems([.mediaType(), .mediaType(), .project()]).contains("Use Photo / Video only once."))
        XCTAssertTrue(problems([.month(), .month(style: .name), .project()]).contains("Use the month only once."))
        XCTAssertTrue(problems([.mediaType(photo: " ", video: "Video"), .project()]).contains("The photo and video folders need names."))
        XCTAssertTrue(problems([.mediaType(photo: "Media", video: "media "), .project()]).contains("The photo and video folders need different names."))
        XCTAssertTrue(problems([.folder(""), .project()]).contains("A folder level has no name."))
        XCTAssertTrue(problems([.project(), .split(unedited: "Out", exported: "out")]).contains("The Unedited and Exported folders need different names."))
        XCTAssertTrue(videoProblems(["Media", ""]).contains("Every video project folder needs a name."))
        XCTAssertTrue(videoProblems(["Media", "media"]).contains("Video project folders need different names."))
        XCTAssertTrue(videoProblems(["Media"], mediaFolder: "Footage").contains("The folder for videos must be one of the video project folders."))

        XCTAssertEqual(splitAboveProject.problems, [])
        XCTAssertEqual(splitInsideProject.problems, [])
        XCTAssertEqual(FolderStructure(levels: [.project()], videoFolders: [], mediaFolder: "").problems, [])
    }

    // MARK: - Existing projects and the manifest

    func testChangingTheStructureNeverMovesExistingProjects() throws {
        // A project from before structures could change, and one made with a custom structure.
        let old = ProjectInfo(name: "Old Shoot", startDay: day, rootPath: root.path)
        let custom = ProjectInfo(name: "New Shoot", startDay: day, structure: splitAboveProject, rootPath: root.path)
        try LibraryManifest(projects: [old, custom], structure: splitInsideProject).write(to: root)

        let manifest = try XCTUnwrap(LibraryManifest.read(from: root))
        XCTAssertEqual(manifest.layout, splitInsideProject)
        func loaded(_ name: String) throws -> ProjectInfo {
            var project = try XCTUnwrap(manifest.projects.first { $0.name == name })
            project.rootPath = root.path
            return project
        }

        XCTAssertEqual(ProjectLayout.photoFolder(for: try loaded("Old Shoot")).path, under("2026", "Photo", "11 November", "02.11.2026 Old Shoot"))
        XCTAssertEqual(ProjectLayout.photoFolder(for: try loaded("New Shoot")).path, under("2026", "Photo", "Unedited", "11 November", "02.11.2026 New Shoot"))
    }

    func testStructureRoundTripsThroughTheManifest() throws {
        let structure = FolderStructure(
            levels: [
                .mediaType(photo: "Foto", video: "Video"), .split(unedited: "Neurejeno", exported: "Izvoženo"),
                .month(style: .name, language: .slovenian), .folder("Stranke"), .project(dateFormat: .compact, nameOrder: .nameFirst),
            ],
            cameraFolders: false,
            videoFolders: ["Posnetki", "Montaža"],
            mediaFolder: "Posnetki"
        )
        try LibraryManifest(projects: [makeProject(structure)], structure: structure).write(to: root)

        let json = try String(contentsOf: LibraryManifest.url(in: root), encoding: .utf8)
        XCTAssertTrue(json.contains("\"kind\" : \"mediaType\""))
        XCTAssertTrue(json.contains("\"monthStyle\" : \"name\""))
        XCTAssertTrue(json.contains("\"monthLanguage\" : \"slovenian\""))
        XCTAssertTrue(json.contains("\"dateFormat\" : \"compact\""))
        XCTAssertTrue(json.contains("\"nameOrder\" : \"nameFirst\""))
        XCTAssertTrue(json.contains("\"cameraFolders\" : false"))
        XCTAssertFalse(json.contains("null"), "Unused options aren't written")

        let read = try XCTUnwrap(LibraryManifest.read(from: root))
        XCTAssertEqual(read.structure, structure)
        XCTAssertEqual(read.projects.first?.structure, structure)
    }

    func testReadsAStructureWrittenByWindows() throws {
        // What the Windows version (System.Text.Json, declaration order) writes for Tilen's example structure.
        let windowsJSON = """
        {
          "formatVersion": 1,
          "libraryID": "7F1C2E0A-3B9D-4C51-9E2A-6D8B1F4A2C33",
          "projects": [],
          "structure": {
            "cameraFolders": true,
            "levels": [
              { "kind": "mediaType", "photo": "Photo", "video": "Video" },
              { "exported": "Exported", "kind": "split", "unedited": "Unedited" },
              { "kind": "month", "monthLanguage": "english", "monthStyle": "numberAndName" },
              { "dateFormat": "dayMonthYear", "kind": "project", "nameOrder": "dateFirst" }
            ],
            "mediaFolder": "Media",
            "videoFolders": ["Media", "Project", "Assets", "Export"]
          }
        }
        """
        let folder = sandbox.appendingPathComponent("FromWindows/.sdbackup", isDirectory: true)
        try fileManager.createDirectory(at: folder, withIntermediateDirectories: true)
        try Data(windowsJSON.utf8).write(to: folder.appendingPathComponent("library.json"))

        XCTAssertEqual(LibraryManifest.read(from: folder.deletingLastPathComponent())?.structure, splitAboveProject)
    }

    func testAStructureItCantUseMeansTheStandardOneAndKeepsTheProjects() throws {
        // Written by a newer version (an unknown kind of level) or broken: the projects are still read.
        for structureJSON in [
            #"{ "levels": [ { "kind": "weekday" }, { "kind": "project" } ], "cameraFolders": true, "videoFolders": [], "mediaFolder": "" }"#,
            #"{ "levels": [ { "kind": "month" } ], "cameraFolders": true, "videoFolders": [], "mediaFolder": "" }"#,
            #"{ "levels": "photo" }"#,
            #""standard""#,
        ] {
            let json = """
            {
              "formatVersion": 1,
              "libraryID": "7F1C2E0A-3B9D-4C51-9E2A-6D8B1F4A2C33",
              "projects": [
                { "id": "C3A15B7E-0D2F-4E8A-9B61-2F7C4D9E8A10", "lastUsed": "2026-11-02T14:08:00Z", "libraryID": "7F1C2E0A-3B9D-4C51-9E2A-6D8B1F4A2C33",
                  "name": "Ana Wedding", "startDay": { "day": 2, "month": 11, "year": 2026 }, "structure": \(structureJSON) }
              ],
              "structure": \(structureJSON)
            }
            """
            let folder = sandbox.appendingPathComponent("Odd/.sdbackup", isDirectory: true)
            try fileManager.createDirectory(at: folder, withIntermediateDirectories: true)
            try Data(json.utf8).write(to: folder.appendingPathComponent("library.json"))

            let manifest = try XCTUnwrap(LibraryManifest.read(from: folder.deletingLastPathComponent()), structureJSON)
            XCTAssertNil(manifest.structure, structureJSON)
            XCTAssertEqual(manifest.layout, .standard)
            XCTAssertEqual(manifest.projects.count, 1, structureJSON)
            XCTAssertNil(manifest.projects.first?.structure, structureJSON)
            XCTAssertEqual(manifest.projects.first?.folderName, "02.11.2026 Ana Wedding")
        }
    }

    // MARK: - Moving a year

    func testMovingAYearKeepsACustomStructure() async throws {
        try await importCard(splitAboveProject)
        let ssd = sandbox.appendingPathComponent("SSD", isDirectory: true)
        try fileManager.createDirectory(at: ssd, withIntermediateDirectories: true)

        let result = try YearMigrator().moveYear(2026, from: root, to: ssd)

        XCTAssertTrue(result.isComplete)
        XCTAssertTrue(fileManager.fileExists(atPath: ssd.appendingPathComponent("2026/Photo/Unedited/11 November/02.11.2026 Ana Wedding/R5/IMG_0001.CR3").path))
        XCTAssertTrue(fileManager.fileExists(atPath: ssd.appendingPathComponent("2026/Photo/Exported/11 November/02.11.2026 Ana Wedding").path), "Empty Exported folders move too")
        XCTAssertFalse(fileManager.fileExists(atPath: root.appendingPathComponent("2026").path))
    }
}
