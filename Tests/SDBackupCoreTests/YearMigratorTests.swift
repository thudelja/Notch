import XCTest
@testable import SDBackupCore

final class YearMigratorTests: XCTestCase {
    private let fileManager = FileManager.default
    private var sandbox: URL!
    private var mac: URL!
    private var ssd: URL!

    private let photoFolder = "2026/Photo/11 November/02.11.2026 Ana Wedding/R5"
    private let videoFolder = "2026/Video/11 November/02.11.2026 Ana Wedding"

    override func setUpWithError() throws {
        sandbox = fileManager.temporaryDirectory.appendingPathComponent("SDMigrateTests-\(UUID().uuidString)", isDirectory: true)
        mac = sandbox.appendingPathComponent("Mac Projects", isDirectory: true)
        ssd = sandbox.appendingPathComponent("SSD Projects", isDirectory: true)
        try fileManager.createDirectory(at: mac, withIntermediateDirectories: true)
        try fileManager.createDirectory(at: ssd, withIntermediateDirectories: true)
    }

    override func tearDownWithError() throws {
        try? fileManager.removeItem(at: sandbox)
    }

    @discardableResult
    private func write(_ root: URL, _ relativePath: String, _ contents: String, modified: Date = Date(timeIntervalSince1970: 1_700_000_000)) throws -> URL {
        let url = root.appendingPathComponent(relativePath)
        try fileManager.createDirectory(at: url.deletingLastPathComponent(), withIntermediateDirectories: true)
        try Data(contents.utf8).write(to: url)
        try fileManager.setAttributes([.modificationDate: modified], ofItemAtPath: url.path)
        return url
    }

    private func exists(_ root: URL, _ relativePath: String) -> Bool {
        fileManager.fileExists(atPath: root.appendingPathComponent(relativePath).path)
    }

    private func contents(_ root: URL, _ relativePath: String) throws -> String {
        String(decoding: try Data(contentsOf: root.appendingPathComponent(relativePath)), as: UTF8.self)
    }

    private func move() throws -> MigrationResult {
        try YearMigrator().moveYear(2026, from: mac, to: ssd)
    }

    // MARK: - Tests

    func testMovesTheWholeYearAndRemovesItFromTheMac() throws {
        let date = Date(timeIntervalSince1970: 1_650_000_000)
        try write(mac, "\(photoFolder)/IMG_0001.CR3", "raw", modified: date)
        try write(mac, "\(videoFolder)/Media/R5/C0001.MP4", "clip")
        try write(mac, "\(videoFolder)/Project/Wedding.prproj", "edit")
        try fileManager.createDirectory(at: mac.appendingPathComponent("\(videoFolder)/Export"), withIntermediateDirectories: true)
        try write(mac, "2026/Photo/.DS_Store", "finder")
        try write(mac, "2025/Photo/old.jpg", "other year")

        let result = try move()

        XCTAssertEqual(result.filesMoved, 3)
        XCTAssertTrue(result.isComplete)
        XCTAssertEqual(try contents(ssd, "\(photoFolder)/IMG_0001.CR3"), "raw")
        XCTAssertEqual(try contents(ssd, "\(videoFolder)/Project/Wedding.prproj"), "edit")
        XCTAssertTrue(exists(ssd, "\(videoFolder)/Export"), "Empty folders are recreated")
        let movedDate = try fileManager.attributesOfItem(atPath: ssd.appendingPathComponent("\(photoFolder)/IMG_0001.CR3").path)[.modificationDate] as? Date
        XCTAssertEqual(movedDate?.timeIntervalSince1970 ?? 0, date.timeIntervalSince1970, accuracy: 1)

        XCTAssertFalse(exists(mac, "2026"), "The year folder is gone from the Mac")
        XCTAssertTrue(exists(mac, "2025/Photo/old.jpg"), "Other years are untouched")
        XCTAssertFalse(exists(ssd, "2026/\(BackupEngine.stagingFolderName)"))
    }

    func testMovingAgainFinishesAnInterruptedMove() throws {
        // A previous, interrupted run already put an identical copy on the SSD.
        try write(mac, "\(photoFolder)/IMG_0001.CR3", "raw", modified: Date(timeIntervalSince1970: 1_700_000_000))
        try write(ssd, "\(photoFolder)/IMG_0001.CR3", "raw", modified: Date(timeIntervalSince1970: 1_700_009_999))
        try write(mac, "\(photoFolder)/IMG_0002.CR3", "second")

        let result = try move()

        XCTAssertEqual(result.filesMoved, 1)
        XCTAssertEqual(result.duplicatesRemoved, 1)
        XCTAssertFalse(exists(mac, "2026"))
        XCTAssertFalse(exists(ssd, "\(photoFolder)/IMG_0001 (1).CR3"))
    }

    func testSameNameSizeAndDateButDifferentBytesIsNotTreatedAsMoved() throws {
        let date = Date(timeIntervalSince1970: 1_700_000_000)
        try write(mac, "\(photoFolder)/IMG_0001.CR3", "AAAA", modified: date)
        try write(ssd, "\(photoFolder)/IMG_0001.CR3", "BBBB", modified: date)

        let result = try move()

        XCTAssertEqual(result.filesMoved, 1)
        XCTAssertEqual(result.duplicatesRemoved, 0)
        XCTAssertEqual(try contents(ssd, "\(photoFolder)/IMG_0001.CR3"), "BBBB")
        XCTAssertEqual(try contents(ssd, "\(photoFolder)/IMG_0001 (1).CR3"), "AAAA")
        XCTAssertFalse(exists(mac, "2026"))
    }

    func testLeavesWhatItCannotMoveAndReportsIt() throws {
        try write(mac, "\(photoFolder)/IMG_0001.CR3", "raw")
        try fileManager.createSymbolicLink(
            at: mac.appendingPathComponent("2026/shortcut"),
            withDestinationURL: sandbox
        )

        let result = try move()

        XCTAssertEqual(result.filesMoved, 1)
        XCTAssertEqual(result.itemsLeftBehind, 1)
        XCTAssertFalse(result.isComplete)
        XCTAssertTrue(exists(mac, "2026/shortcut"))
        XCTAssertFalse(exists(mac, "\(photoFolder)/IMG_0001.CR3"))
    }

    func testRefusesToMoveIntoTheSameFolder() throws {
        try write(mac, "\(photoFolder)/IMG_0001.CR3", "raw")
        XCTAssertThrowsError(try YearMigrator().moveYear(2026, from: mac, to: mac)) { error in
            XCTAssertEqual(error as? BackupError, .destinationIsSource)
        }
        XCTAssertTrue(exists(mac, "\(photoFolder)/IMG_0001.CR3"))
    }

    func testReportsADisconnectedDrive() throws {
        try write(mac, "\(photoFolder)/IMG_0001.CR3", "raw")
        let unplugged = sandbox.appendingPathComponent("Unplugged SSD/Projects")
        XCTAssertThrowsError(try YearMigrator().moveYear(2026, from: mac, to: unplugged)) { error in
            guard case .destinationUnavailable? = error as? BackupError else {
                return XCTFail("Unexpected error: \(error)")
            }
        }
        XCTAssertTrue(exists(mac, "\(photoFolder)/IMG_0001.CR3"))
    }

    func testListsYearFolders() throws {
        for folder in ["2024", "2026", "Archive", "20255"] {
            try fileManager.createDirectory(at: mac.appendingPathComponent(folder), withIntermediateDirectories: true)
        }
        try write(mac, "2023", "a file, not a folder")
        XCTAssertEqual(YearMigrator.years(in: mac), [2024, 2026])
    }
}
