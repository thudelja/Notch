import XCTest
@testable import SDBackupCore

final class ProjectLibraryTests: XCTestCase {
    private let fileManager = FileManager.default
    private var sandbox: URL!

    override func setUpWithError() throws {
        sandbox = fileManager.temporaryDirectory.appendingPathComponent("SDLibraryTests-\(UUID().uuidString)", isDirectory: true)
        try fileManager.createDirectory(at: sandbox, withIntermediateDirectories: true)
    }

    override func tearDownWithError() throws {
        try? fileManager.removeItem(at: sandbox)
    }

    private func folder(_ path: String) throws -> URL {
        let url = sandbox.appendingPathComponent(path, isDirectory: true)
        try fileManager.createDirectory(at: url, withIntermediateDirectories: true)
        return url
    }

    private let project = ProjectInfo(
        name: "Ana Wedding",
        startDay: ShootDay(year: 2026, month: 11, day: 2),
        libraryID: UUID(),
        rootPath: "/Users/someone/Pictures/Projects",
        lastUsed: Date(timeIntervalSince1970: 1_790_000_000)
    )

    func testManifestContainsNoAbsolutePaths() throws {
        let root = try folder("SSD/Projects")
        try LibraryManifest(libraryID: project.libraryID, projects: [project]).write(to: root)

        let json = try String(contentsOf: LibraryManifest.url(in: root), encoding: .utf8)
        XCTAssertFalse(json.contains("/Users/someone"), "Paths of this computer must not be saved")
        XCTAssertFalse(json.contains(sandbox.path))
        XCTAssertFalse(json.contains("rootPath"))
        XCTAssertTrue(json.contains("\"name\" : \"Ana Wedding\""))
        XCTAssertTrue(json.contains("2026-09-21T"), "Dates are ISO 8601")

        let read = try XCTUnwrap(LibraryManifest.read(from: root))
        XCTAssertEqual(read.libraryID, project.libraryID)
        XCTAssertEqual(read.projects, [project.portable])
        XCTAssertEqual(read.projects.first?.rootPath, "")
    }

    func testOpenOrCreateKeepsTheSameID() throws {
        let root = try folder("Projects")
        let first = try LibraryManifest.openOrCreate(at: root)
        let second = try LibraryManifest.openOrCreate(at: root)
        XCTAssertEqual(first.libraryID, second.libraryID)
        XCTAssertTrue(fileManager.fileExists(atPath: root.appendingPathComponent(".sdbackup/library.json").path))
    }

    func testFindsAFolderAtItsLastKnownPath() throws {
        let root = try folder("SSD/Projects")
        let manifest = try LibraryManifest.openOrCreate(at: root)
        let location = LibraryLocation(libraryID: manifest.libraryID, lastKnownPath: root.path, pathOnVolume: "Projects", volumeName: "SSD")
        XCTAssertEqual(LibraryLocator.resolve(location, volumes: [])?.standardizedFileURL.path, root.standardizedFileURL.path)
    }

    func testFindsAFolderWhenTheDriveIsMountedUnderAnotherName() throws {
        // The drive was /Volumes/SSD last time and now shows up as /Volumes/SSD 1 (or E:\ on Windows).
        let nowMountedAt = try folder("SSD 1")
        let root = try folder("SSD 1/Projects")
        let manifest = try LibraryManifest.openOrCreate(at: root)
        let location = LibraryLocation(
            libraryID: manifest.libraryID,
            lastKnownPath: sandbox.appendingPathComponent("SSD/Projects").path,
            pathOnVolume: "Projects",
            volumeName: "SSD"
        )
        let otherDrive = try folder("Other")

        let found = LibraryLocator.resolve(location, volumes: [otherDrive, nowMountedAt])

        XCTAssertEqual(found?.standardizedFileURL.path, root.standardizedFileURL.path)
    }

    func testIgnoresAFolderWithTheSameNameFromAnotherDrive() throws {
        let impostor = try folder("Impostor/Projects")
        _ = try LibraryManifest.openOrCreate(at: impostor) // a different library ID
        let location = LibraryLocation(libraryID: UUID(), lastKnownPath: impostor.path, pathOnVolume: "Projects", volumeName: "SSD")

        XCTAssertNil(LibraryLocator.resolve(location, volumes: [sandbox.appendingPathComponent("Impostor")]))
    }

    func testDescribesWhereAFolderIs() throws {
        let root = try folder("Projects")
        let location = LibraryLocator.location(of: root, libraryID: project.libraryID)
        XCTAssertEqual(location.lastKnownPath, root.standardizedFileURL.path)
        XCTAssertFalse(location.pathOnVolume.hasPrefix("/"))
        XCTAssertTrue(location.pathOnVolume.hasSuffix("Projects"))
    }

    func testProjectFoldersAreDerivedRelativeToTheProjectsFolder() {
        XCTAssertEqual(ProjectLayout.relativePhotoFolder(for: project), ["2026", "Photo", "11 November", "02.11.2026 Ana Wedding"])
        XCTAssertEqual(ProjectLayout.relativeVideoFolder(for: project), ["2026", "Video", "11 November", "02.11.2026 Ana Wedding"])

        var onSSD = project
        onSSD.rootPath = "/Volumes/SSD/Projects"
        XCTAssertEqual(ProjectLayout.photoFolder(for: onSSD).path, "/Volumes/SSD/Projects/2026/Photo/11 November/02.11.2026 Ana Wedding")
    }
}
