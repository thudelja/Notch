import XCTest
@testable import SDBackupCore

final class ProjectImportTests: XCTestCase {
    private let fileManager = FileManager.default
    private var sandbox: URL!
    private var card: URL!
    private var root: URL!

    override func setUpWithError() throws {
        sandbox = fileManager.temporaryDirectory.appendingPathComponent("SDImportTests-\(UUID().uuidString)", isDirectory: true)
        card = sandbox.appendingPathComponent("CARD", isDirectory: true)
        root = sandbox.appendingPathComponent("Projects", isDirectory: true)
        try fileManager.createDirectory(at: card, withIntermediateDirectories: true)
        try fileManager.createDirectory(at: root, withIntermediateDirectories: true)
    }

    override func tearDownWithError() throws {
        try? fileManager.removeItem(at: sandbox)
    }

    // MARK: - Helpers

    private func date(_ year: Int, _ month: Int, _ day: Int, _ hour: Int = 12) -> Date {
        Calendar.current.date(from: DateComponents(year: year, month: month, day: day, hour: hour))!
    }

    @discardableResult
    private func write(_ relativePath: String, _ contents: String = "data", shot: Date? = nil) throws -> URL {
        let url = card.appendingPathComponent(relativePath)
        try fileManager.createDirectory(at: url.deletingLastPathComponent(), withIntermediateDirectories: true)
        try Data(contents.utf8).write(to: url)
        let when = shot ?? date(2026, 1, 1)
        try fileManager.setAttributes([.modificationDate: when, .creationDate: when], ofItemAtPath: url.path)
        return url
    }

    private func source(_ relativePath: String, size: Int64 = 1, modified: Date = Date(timeIntervalSince1970: 1_700_000_000)) -> SourceFile {
        SourceFile(
            url: URL(fileURLWithPath: "/Volumes/CARD").appendingPathComponent(relativePath),
            relativePath: relativePath,
            size: size,
            modificationDate: modified,
            creationDate: nil
        )
    }

    private struct FakeReader: MetadataReading {
        var photos: [String: CaptureInfo] = [:]
        var videos: [String: CaptureInfo] = [:]

        func photoInfo(at url: URL) -> CaptureInfo { photos[url.lastPathComponent] ?? CaptureInfo() }
        func videoInfo(at url: URL) async -> CaptureInfo { videos[url.lastPathComponent] ?? CaptureInfo() }
    }

    private let sonyXML = """
    <?xml version="1.0" encoding="UTF-8"?>
    <NonRealTimeMeta>
      <CreationDate value="2026-11-03T09:15:00+01:00"/>
      <Device manufacturer="Sony" modelName="ILCE-7M4" serialNo="1234"/>
    </NonRealTimeMeta>
    """

    /// A card with Canon photos and video, a Sony clip with its XML, recorder audio and junk.
    private func writeMixedCard() throws {
        try write("DCIM/100CANON/IMG_0001.CR3", "raw photo", shot: date(2026, 11, 2, 10))
        try write("DCIM/100CANON/IMG_0001.XMP", "xmp", shot: date(2026, 11, 2, 10))
        try write("DCIM/100CANON/MVI_0002.MP4", "canon clip", shot: date(2026, 11, 2, 11))
        try write("PRIVATE/M4ROOT/CLIP/C0001.MP4", "sony clip", shot: date(2026, 11, 3, 9))
        try write("PRIVATE/M4ROOT/CLIP/C0001M01.XML", sonyXML, shot: date(2026, 11, 3, 9))
        try write("ZOOM0001.WAV", "audio", shot: date(2026, 11, 2, 12))
        try write("MISC/CTG.BIN", "camera database")
        try write("MISC/STATUS.XML", "<status/>")
    }

    private var mixedCardReader: FakeReader {
        FakeReader(photos: ["IMG_0001.CR3": CaptureInfo(date: date(2026, 11, 2, 10), make: "Canon", model: "Canon EOS R5")])
    }

    // MARK: - Classification

    func testClassifiesByExtension() {
        XCTAssertEqual(MediaClassifier.kind(ofFileNamed: "IMG_0001.CR3"), .photo)
        XCTAssertEqual(MediaClassifier.kind(ofFileNamed: "img_0001.jpg"), .photo)
        XCTAssertEqual(MediaClassifier.kind(ofFileNamed: "C0001.MP4"), .video)
        XCTAssertEqual(MediaClassifier.kind(ofFileNamed: "A001.BRAW"), .video)
        XCTAssertEqual(MediaClassifier.kind(ofFileNamed: "ZOOM0001.WAV"), .audio)
        XCTAssertEqual(MediaClassifier.kind(ofFileNamed: "C0001M01.XML"), .sidecar)
        XCTAssertEqual(MediaClassifier.kind(ofFileNamed: "DJI_0001.LRF"), .sidecar)
        XCTAssertEqual(MediaClassifier.kind(ofFileNamed: "CTG.BIN"), .other)
    }

    func testSidecarsAttachToTheirMediaFile() {
        let grouped = MediaClassifier.group([
            source("PRIVATE/M4ROOT/CLIP/C0001.MP4"),
            source("PRIVATE/M4ROOT/CLIP/C0001M01.XML"),
            source("DCIM/100GOPRO/GX010123.MP4"),
            source("DCIM/100GOPRO/GX010123.THM"),
            source("DCIM/100GOPRO/GL010123.LRV"),
            source("DCIM/100MEDIA/DJI_0001.MP4"),
            source("DCIM/100MEDIA/DJI_0001.SRT"),
            source("DCIM/100CANON/IMG_0001.CR3"),
            source("DCIM/100CANON/IMG_0001.XMP"),
            source("MISC/STATUS.XML"),
            source("MISC/CTG.BIN"),
        ])

        func sidecars(of path: String) -> [String] {
            grouped.media.first { $0.file.relativePath == path }?.sidecars.map(\.relativePath) ?? []
        }
        XCTAssertEqual(grouped.media.count, 4)
        XCTAssertEqual(sidecars(of: "PRIVATE/M4ROOT/CLIP/C0001.MP4"), ["PRIVATE/M4ROOT/CLIP/C0001M01.XML"])
        XCTAssertEqual(sidecars(of: "DCIM/100GOPRO/GX010123.MP4"), ["DCIM/100GOPRO/GX010123.THM"])
        XCTAssertEqual(sidecars(of: "DCIM/100MEDIA/DJI_0001.MP4"), ["DCIM/100MEDIA/DJI_0001.SRT"])
        XCTAssertEqual(sidecars(of: "DCIM/100CANON/IMG_0001.CR3"), ["DCIM/100CANON/IMG_0001.XMP"])
        // GoPro's GL… proxy is named differently from its GX… clip, and STATUS.XML belongs to nothing.
        XCTAssertEqual(Set(grouped.unmatchedSidecars.map(\.relativePath)), ["DCIM/100GOPRO/GL010123.LRV", "MISC/STATUS.XML"])
        XCTAssertEqual(grouped.other.map(\.relativePath), ["MISC/CTG.BIN"])
    }

    // MARK: - Names and dates

    func testCameraShortNames() {
        XCTAssertEqual(CameraNames.shortName(make: "Canon", model: "Canon EOS R5"), "R5")
        XCTAssertEqual(CameraNames.shortName(make: "Canon", model: "Canon EOS R6m2"), "R6m2")
        XCTAssertEqual(CameraNames.shortName(make: "Canon", model: "Canon EOS 5D Mark IV"), "5D Mark IV")
        XCTAssertEqual(CameraNames.shortName(make: "SONY", model: "ILCE-7M4"), "A7 IV")
        XCTAssertEqual(CameraNames.shortName(make: "SONY", model: "ILCE-7SM3"), "A7S III")
        XCTAssertEqual(CameraNames.shortName(make: "SONY", model: "ILCE-6400"), "A6400")
        XCTAssertEqual(CameraNames.shortName(make: "Sony", model: "ILME-FX3"), "FX3")
        XCTAssertEqual(CameraNames.shortName(make: "NIKON CORPORATION", model: "NIKON Z 6_2"), "Z6 II")
        XCTAssertEqual(CameraNames.shortName(make: "Panasonic", model: "DC-GH6"), "GH6")
        XCTAssertEqual(CameraNames.shortName(make: "FUJIFILM", model: "X-T5"), "X-T5")
        XCTAssertEqual(CameraNames.shortName(make: "Apple", model: "iPhone 15 Pro"), "iPhone 15 Pro")
    }

    func testParsesCameraDatesAsLocalWallClock() {
        let exif = MetadataParsing.parseDate("2026:11:02 23:30:00")
        XCTAssertEqual(exif.map { ShootDay($0) }, ShootDay(year: 2026, month: 11, day: 2))
        let iso = MetadataParsing.parseDate("2026-11-03T09:15:00+01:00")
        XCTAssertEqual(iso.map { ShootDay($0) }, ShootDay(year: 2026, month: 11, day: 3))
        XCTAssertNil(MetadataParsing.parseDate("0000:00:00 00:00:00"))
        XCTAssertNil(MetadataParsing.parseDate("    :  :     :  :  "))
    }

    func testParsesSonyClipXML() {
        let info = MetadataParsing.parseClipXML(sonyXML)
        XCTAssertEqual(info.make, "Sony")
        XCTAssertEqual(info.model, "ILCE-7M4")
        XCTAssertEqual(info.date.map { ShootDay($0) }, ShootDay(year: 2026, month: 11, day: 3))
    }

    func testFolderNames() {
        XCTAssertEqual(ProjectLayout.monthFolderName(ShootDay(year: 2026, month: 11, day: 2)), "11 November")
        XCTAssertEqual(ProjectLayout.monthFolderName(ShootDay(year: 2026, month: 1, day: 9)), "01 January")
        XCTAssertEqual(ProjectLayout.projectFolderName(name: "Ana Wedding", startDay: ShootDay(year: 2026, month: 11, day: 2)), "02.11.2026 Ana Wedding")
        XCTAssertEqual(ProjectLayout.projectFolderName(name: " a/b ", startDay: ShootDay(year: 2026, month: 3, day: 14)), "14.03.2026 a-b")

        let project = ProjectInfo(name: "Ana Wedding", startDay: ShootDay(year: 2026, month: 11, day: 2), rootPath: "/P")
        XCTAssertEqual(ProjectLayout.photoFolder(for: project).path, "/P/2026/Photo/11 November/02.11.2026 Ana Wedding")
        XCTAssertEqual(ProjectLayout.videoFolder(for: project).path, "/P/2026/Video/11 November/02.11.2026 Ana Wedding")
        XCTAssertEqual(
            ProjectLayout.targetDirectory(kind: .photo, project: project, camera: "R5").path,
            "/P/2026/Photo/11 November/02.11.2026 Ana Wedding/R5"
        )
        XCTAssertEqual(
            ProjectLayout.targetDirectory(kind: .video, project: project, camera: "R6").path,
            "/P/2026/Video/11 November/02.11.2026 Ana Wedding/Media/R6"
        )
        XCTAssertEqual(
            ProjectLayout.targetDirectory(kind: .audio, project: project, camera: "").path,
            "/P/2026/Video/11 November/02.11.2026 Ana Wedding/Media/Camera"
        )
    }

    // MARK: - Ledger

    func testLedgerRecognisesImportedFiles() {
        var ledger = ImportLedger()
        let file = source("DCIM/IMG_0001.JPG", size: 100, modified: Date(timeIntervalSince1970: 1_700_000_000))
        XCTAssertFalse(ledger.contains(file))

        ledger.record(file, destination: URL(fileURLWithPath: "/P/IMG_0001.JPG"))
        XCTAssertTrue(ledger.contains(file))
        XCTAssertTrue(ledger.contains(source("dcim/img_0001.jpg", size: 100, modified: Date(timeIntervalSince1970: 1_700_000_001))))
        XCTAssertFalse(ledger.contains(source("DCIM/IMG_0001.JPG", size: 101, modified: Date(timeIntervalSince1970: 1_700_000_000))))
        XCTAssertFalse(ledger.contains(source("DCIM/IMG_0001.JPG", size: 100, modified: Date(timeIntervalSince1970: 1_700_100_000))))
    }

    // MARK: - Analysis

    func testAnalyzerGroupsFilesByDayAndCamera() async throws {
        try writeMixedCard()

        let analysis = try await CardAnalyzer(reader: mixedCardReader).analyze(source: card, isAlreadyImported: { _ in false })

        XCTAssertEqual(analysis.items.count, 4)
        XCTAssertEqual(analysis.ignoredFiles, 2)
        XCTAssertEqual(analysis.days.map(\.day), [ShootDay(year: 2026, month: 11, day: 2), ShootDay(year: 2026, month: 11, day: 3)])
        XCTAssertEqual(analysis.days.first?.photos, 1)
        XCTAssertEqual(analysis.days.first?.videos, 1)
        XCTAssertEqual(analysis.days.first?.audio, 1)
        XCTAssertEqual(analysis.days.last?.videos, 1)

        func item(_ name: String) -> MediaItem? { analysis.items.first { $0.file.relativePath.hasSuffix(name) } }
        XCTAssertEqual(item("C0001.MP4")?.cameraModel, "ILCE-7M4")
        XCTAssertEqual(item("C0001.MP4")?.sidecars.count, 1)
        // The Canon clip has no metadata, but every photo on the card came from the R5.
        XCTAssertEqual(item("MVI_0002.MP4")?.cameraModel, "Canon EOS R5")
        XCTAssertNil(item("ZOOM0001.WAV")?.cameraModel)

        XCTAssertEqual(analysis.cameraModels, ["Canon EOS R5", "ILCE-7M4"])
        XCTAssertTrue(analysis.hasUnknownCamera)
    }

    func testAnalyzerLeavesOutImportedFiles() async throws {
        try writeMixedCard()
        let analysis = try await CardAnalyzer(reader: mixedCardReader).analyze(
            source: card,
            isAlreadyImported: { $0.relativePath.hasSuffix("IMG_0001.CR3") }
        )
        XCTAssertEqual(analysis.alreadyImported, 1)
        XCTAssertEqual(analysis.items.count, 3)
    }

    // MARK: - End to end

    func testImportsIntoProjectFolders() async throws {
        try writeMixedCard()
        let analyzer = CardAnalyzer(reader: mixedCardReader)
        let analysis = try await analyzer.analyze(source: card, isAlreadyImported: { _ in false })

        // Both shoot days go into one project that started on 2 November.
        let project = ProjectInfo(name: "Ana Wedding", startDay: ShootDay(year: 2026, month: 11, day: 2), rootPath: root.path)
        var projects: [ShootDay: ProjectInfo] = [:]
        for summary in analysis.days {
            projects[summary.day] = project
        }
        let plan = ProjectLayout.plan(items: analysis.items, projects: projects) { item in
            item.cameraModel.map { CameraNames.shortName(make: item.cameraMake, model: $0) } ?? "Zoom H5"
        }
        XCTAssertEqual(plan.fileCount, 6)

        let result = try BackupEngine(options: BackupOptions(reservedFreeSpace: 0))
            .copy(plan.items, from: card, to: root, createFolders: plan.foldersToCreate)
        XCTAssertEqual(result.filesCopied, 6)
        XCTAssertTrue(result.failures.isEmpty)

        let photo = "2026/Photo/11 November/02.11.2026 Ana Wedding"
        let video = "2026/Video/11 November/02.11.2026 Ana Wedding"
        for path in [
            "\(photo)/R5/IMG_0001.CR3",
            "\(photo)/R5/IMG_0001.XMP",
            "\(video)/Media/R5/MVI_0002.MP4",
            "\(video)/Media/A7 IV/C0001.MP4",
            "\(video)/Media/A7 IV/C0001M01.XML",
            "\(video)/Media/Zoom H5/ZOOM0001.WAV",
        ] {
            XCTAssertTrue(fileManager.fileExists(atPath: root.appendingPathComponent(path).path), "Missing \(path)")
        }
        for folder in ["Project", "Assets", "Export"] {
            XCTAssertTrue(fileManager.fileExists(atPath: root.appendingPathComponent("\(video)/\(folder)").path), "Missing \(folder)")
        }

        // Re-inserting the card offers nothing new.
        var ledger = ImportLedger()
        for file in result.processedFiles {
            ledger.record(file.source, destination: file.destination)
        }
        let again = try await analyzer.analyze(source: card, isAlreadyImported: { ledger.contains($0) })
        XCTAssertTrue(again.isEmpty)
        XCTAssertEqual(again.alreadyImported, 4)
    }

    func testPlanSkipsDaysWithoutAProject() async throws {
        try writeMixedCard()
        let analysis = try await CardAnalyzer(reader: mixedCardReader).analyze(source: card, isAlreadyImported: { _ in false })
        let project = ProjectInfo(name: "Day Two", startDay: ShootDay(year: 2026, month: 11, day: 3), rootPath: root.path)

        let plan = ProjectLayout.plan(items: analysis.items, projects: [ShootDay(year: 2026, month: 11, day: 3): project]) { _ in "A7 IV" }

        XCTAssertEqual(plan.items.map { ($0.file.relativePath as NSString).lastPathComponent }.sorted(), ["C0001.MP4", "C0001M01.XML"])
        XCTAssertEqual(plan.foldersToCreate.count, 4)
    }

    func testPhotoHandoffListsOnlyFoldersWithNewPhotos() async throws {
        try writeMixedCard()
        let analysis = try await CardAnalyzer(reader: mixedCardReader).analyze(source: card, isAlreadyImported: { _ in false })
        let project = ProjectInfo(name: "Ana Wedding", startDay: ShootDay(year: 2026, month: 11, day: 2), rootPath: root.path)
        var projects: [ShootDay: ProjectInfo] = [:]
        for summary in analysis.days {
            projects[summary.day] = project
        }
        let plan = ProjectLayout.plan(items: analysis.items, projects: projects) { item in
            item.cameraModel.map { CameraNames.shortName(make: item.cameraMake, model: $0) } ?? "Zoom H5"
        }
        let engine = BackupEngine(options: BackupOptions(reservedFreeSpace: 0))

        // Only the photo camera folder: no video, audio or sidecar folders.
        let first = try engine.copy(plan.items, from: card, to: root, createFolders: plan.foldersToCreate)
        let photoFolder = root.appendingPathComponent("2026/Photo/11 November/02.11.2026 Ana Wedding/R5", isDirectory: true)
        XCTAssertEqual(PhotoHandoff.folders(from: first).map(\.path), [photoFolder.standardizedFileURL.path])
        XCTAssertEqual(PhotoHandoff.photoCount(in: first), 1)

        // The same card again copies nothing new, so there is nothing to hand over.
        let second = try engine.copy(plan.items, from: card, to: root, createFolders: plan.foldersToCreate)
        XCTAssertTrue(PhotoHandoff.folders(from: second).isEmpty)
        XCTAssertEqual(PhotoHandoff.photoCount(in: second), 0)

        // A day with only videos has no photos to hand over.
        let videoDay = ProjectInfo(name: "Day Two", startDay: ShootDay(year: 2026, month: 11, day: 3), rootPath: root.path)
        let videoPlan = ProjectLayout.plan(items: analysis.items, projects: [ShootDay(year: 2026, month: 11, day: 3): videoDay]) { _ in "A7 IV" }
        let videos = try engine.copy(videoPlan.items, from: card, to: root, createFolders: videoPlan.foldersToCreate)
        XCTAssertGreaterThan(videos.filesCopied, 0)
        XCTAssertTrue(PhotoHandoff.folders(from: videos).isEmpty)
    }
}
