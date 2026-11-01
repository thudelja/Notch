import XCTest
@testable import SDBackupCore

final class BackupEngineTests: XCTestCase {
    private let fileManager = FileManager.default
    private var sandbox: URL!
    private var card: URL!
    private var backups: URL!

    override func setUpWithError() throws {
        sandbox = fileManager.temporaryDirectory.appendingPathComponent("SDBackupTests-\(UUID().uuidString)", isDirectory: true)
        card = sandbox.appendingPathComponent("CARD", isDirectory: true)
        backups = sandbox.appendingPathComponent("Backups", isDirectory: true)
        try fileManager.createDirectory(at: card, withIntermediateDirectories: true)
        try fileManager.createDirectory(at: backups, withIntermediateDirectories: true)
    }

    override func tearDownWithError() throws {
        try? fileManager.removeItem(at: sandbox)
    }

    // MARK: - Helpers

    @discardableResult
    private func write(_ relativePath: String, _ contents: String, modified: Date = Date(timeIntervalSince1970: 1_700_000_000)) throws -> URL {
        let url = card.appendingPathComponent(relativePath)
        try fileManager.createDirectory(at: url.deletingLastPathComponent(), withIntermediateDirectories: true)
        try Data(contents.utf8).write(to: url)
        try fileManager.setAttributes([.modificationDate: modified], ofItemAtPath: url.path)
        return url
    }

    @discardableResult
    private func runBackup(verify: Bool = true) throws -> BackupResult {
        try BackupEngine(options: BackupOptions(verifyCopies: verify, reservedFreeSpace: 0))
            .run(source: card, destinationRoot: backups, cardFolderName: "CARD")
    }

    private func backedUp(_ relativePath: String) -> URL {
        backups.appendingPathComponent("CARD").appendingPathComponent(relativePath)
    }

    private func contents(of url: URL) throws -> String {
        String(decoding: try Data(contentsOf: url), as: UTF8.self)
    }

    // MARK: - Tests

    func testCopiesFilesPreservingStructureAndDates() throws {
        let date = Date(timeIntervalSince1970: 1_650_000_000)
        try write("DCIM/100CANON/IMG_0001.JPG", "photo one", modified: date)
        try write("DCIM/100CANON/IMG_0002.CR3", "raw two")
        try write("PRIVATE/M4ROOT/CLIP/C0001.MP4", "video")

        let result = try runBackup()

        XCTAssertEqual(result.filesCopied, 3)
        XCTAssertEqual(result.filesAlreadyBackedUp, 0)
        XCTAssertEqual(result.bytesCopied, Int64("photo one".utf8.count + "raw two".utf8.count + "video".utf8.count))
        XCTAssertTrue(result.failures.isEmpty)
        XCTAssertEqual(try contents(of: backedUp("DCIM/100CANON/IMG_0001.JPG")), "photo one")
        XCTAssertEqual(try contents(of: backedUp("PRIVATE/M4ROOT/CLIP/C0001.MP4")), "video")

        let copiedDate = try fileManager.attributesOfItem(atPath: backedUp("DCIM/100CANON/IMG_0001.JPG").path)[.modificationDate] as? Date
        XCTAssertEqual(copiedDate?.timeIntervalSince1970 ?? 0, date.timeIntervalSince1970, accuracy: 1)
        XCTAssertFalse(fileManager.fileExists(atPath: backups.appendingPathComponent(BackupEngine.stagingFolderName).path))
    }

    func testSecondRunCopiesOnlyNewFiles() throws {
        try write("DCIM/IMG_0001.JPG", "one")
        try runBackup()

        try write("DCIM/IMG_0002.JPG", "two")
        let result = try runBackup()

        XCTAssertEqual(result.filesCopied, 1)
        XCTAssertEqual(result.filesAlreadyBackedUp, 1)
    }

    func testNeverOverwritesADifferentFileWithTheSameName() throws {
        try write("DCIM/IMG_0001.JPG", "first card contents", modified: Date(timeIntervalSince1970: 1_600_000_000))
        try runBackup()

        // The card is reformatted and the camera reuses the file name.
        try write("DCIM/IMG_0001.JPG", "a brand new photo", modified: Date(timeIntervalSince1970: 1_700_000_000))
        let second = try runBackup()

        XCTAssertEqual(second.filesCopied, 1)
        XCTAssertEqual(try contents(of: backedUp("DCIM/IMG_0001.JPG")), "first card contents")
        XCTAssertEqual(try contents(of: backedUp("DCIM/IMG_0001 (1).JPG")), "a brand new photo")

        // Inserting the card again recognises the renamed copy.
        let third = try runBackup()
        XCTAssertEqual(third.filesCopied, 0)
        XCTAssertEqual(third.filesAlreadyBackedUp, 1)
    }

    func testShiftedTimestampWithSameContentIsNotCopiedAgain() throws {
        let url = try write("DCIM/IMG_0001.JPG", "same bytes", modified: Date(timeIntervalSince1970: 1_700_000_000))
        try runBackup()

        // FAT cards store local time, so a time-zone change shifts the reported date by hours.
        try fileManager.setAttributes([.modificationDate: Date(timeIntervalSince1970: 1_700_003_600)], ofItemAtPath: url.path)
        let result = try runBackup()

        XCTAssertEqual(result.filesCopied, 0)
        XCTAssertEqual(result.filesAlreadyBackedUp, 1)
    }

    func testSameSizeDifferentContentIsKeptSeparately() throws {
        let url = try write("DCIM/IMG_0001.JPG", "AAAA", modified: Date(timeIntervalSince1970: 1_700_000_000))
        try runBackup()

        try Data("BBBB".utf8).write(to: url)
        try fileManager.setAttributes([.modificationDate: Date(timeIntervalSince1970: 1_700_003_600)], ofItemAtPath: url.path)
        let result = try runBackup()

        XCTAssertEqual(result.filesCopied, 1)
        XCTAssertEqual(try contents(of: backedUp("DCIM/IMG_0001.JPG")), "AAAA")
        XCTAssertEqual(try contents(of: backedUp("DCIM/IMG_0001 (1).JPG")), "BBBB")
    }

    func testNumberedNamesOnTheCardDoNotCollideWithRenamedCopies() throws {
        try write("DCIM/IMG.JPG", "old", modified: Date(timeIntervalSince1970: 1_600_000_000))
        try runBackup()

        try write("DCIM/IMG.JPG", "newer", modified: Date(timeIntervalSince1970: 1_700_000_000))
        try write("DCIM/IMG (1).JPG", "literal name", modified: Date(timeIntervalSince1970: 1_700_000_000))
        let result = try runBackup()

        XCTAssertEqual(result.filesCopied, 2)
        XCTAssertTrue(result.failures.isEmpty)
        let backedUpContents = try Set(["DCIM/IMG.JPG", "DCIM/IMG (1).JPG", "DCIM/IMG (2).JPG"].map { try contents(of: backedUp($0)) })
        XCTAssertEqual(backedUpContents, ["old", "newer", "literal name"])
    }

    func testSkipsSystemMetadataButKeepsOtherHiddenFiles() throws {
        try write("DCIM/IMG_0001.JPG", "photo")
        try write("DCIM/._IMG_0001.JPG", "apple double")
        try write(".Spotlight-V100/Store-V2/index", "spotlight")
        try write(".DS_Store", "finder")
        try write(".camera-settings", "keep me")

        let result = try runBackup()

        XCTAssertEqual(result.filesCopied, 2)
        XCTAssertTrue(fileManager.fileExists(atPath: backedUp(".camera-settings").path))
        XCTAssertFalse(fileManager.fileExists(atPath: backedUp("DCIM/._IMG_0001.JPG").path))
        XCTAssertFalse(fileManager.fileExists(atPath: backedUp(".DS_Store").path))
        XCTAssertFalse(fileManager.fileExists(atPath: backedUp(".Spotlight-V100").path))
    }

    func testCopiesEmptyFiles() throws {
        try write("MISC/empty.txt", "")
        let result = try runBackup()
        XCTAssertEqual(result.filesCopied, 1)
        XCTAssertTrue(fileManager.fileExists(atPath: backedUp("MISC/empty.txt").path))
    }

    func testReportsProgress() throws {
        try write("DCIM/IMG_0001.JPG", "12345")
        var updates: [BackupProgress] = []
        try BackupEngine(options: BackupOptions(reservedFreeSpace: 0))
            .run(source: card, destinationRoot: backups, cardFolderName: "CARD") { updates.append($0) }

        XCTAssertEqual(updates.first?.phase, .scanning)
        XCTAssertEqual(updates.last?.phase, .copying)
        XCTAssertEqual(updates.last?.filesProcessed, 1)
        XCTAssertEqual(updates.last?.bytesProcessed, 5)
        XCTAssertEqual(updates.last?.fractionCompleted, 1)
    }

    func testRefusesDestinationInsideSource() {
        XCTAssertThrowsError(
            try BackupEngine().run(source: card, destinationRoot: card.appendingPathComponent("Backups"), cardFolderName: "CARD")
        ) { error in
            XCTAssertEqual(error as? BackupError, .destinationInsideSource)
        }
    }

    func testReportsUnavailableDestinationInsteadOfCreatingIt() {
        let missing = sandbox.appendingPathComponent("Unplugged Drive/Backups", isDirectory: true)
        XCTAssertThrowsError(
            try BackupEngine().run(source: card, destinationRoot: missing, cardFolderName: "CARD")
        ) { error in
            guard case .destinationUnavailable? = error as? BackupError else {
                return XCTFail("Unexpected error: \(error)")
            }
        }
        XCTAssertFalse(fileManager.fileExists(atPath: missing.path))
    }

    func testCancelledBackupLeavesNothingBehind() async throws {
        try write("DCIM/IMG_0001.JPG", "photo")
        let source: URL = card
        let destination: URL = backups

        let task = Task { () throws -> BackupResult in
            withUnsafeCurrentTask { $0?.cancel() }
            return try BackupEngine().run(source: source, destinationRoot: destination, cardFolderName: "CARD")
        }

        do {
            _ = try await task.value
            XCTFail("Expected the backup to be cancelled")
        } catch is CancellationError {
            // Expected.
        }
        XCTAssertFalse(fileManager.fileExists(atPath: backedUp("DCIM/IMG_0001.JPG").path))
    }

    func testFolderNameSanitising() {
        XCTAssertEqual(BackupEngine.sanitizedFolderName("EOS_DIGITAL"), "EOS_DIGITAL")
        XCTAssertEqual(BackupEngine.sanitizedFolderName("a/b:c"), "a-b-c")
        XCTAssertEqual(BackupEngine.sanitizedFolderName("   "), "Untitled Card")
        XCTAssertEqual(BackupEngine.sanitizedFolderName(".hidden"), "hidden")
    }

    func testNumberedVariants() {
        let url = URL(fileURLWithPath: "/b/DCIM/IMG_0001.JPG")
        XCTAssertEqual(BackupEngine.numberedVariant(of: url, number: 2).lastPathComponent, "IMG_0001 (2).JPG")
        XCTAssertEqual(BackupEngine.numberedVariant(of: URL(fileURLWithPath: "/b/README"), number: 1).lastPathComponent, "README (1)")
    }
}
