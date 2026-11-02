import Foundation

/// A photo, video or audio file ready to import, with its shoot day and camera.
public struct MediaItem: Sendable, Identifiable {
    public var id: String { file.relativePath }
    public let file: SourceFile
    public let kind: MediaKind
    public let day: ShootDay
    public let cameraMake: String?
    /// The model as written by the camera, e.g. "Canon EOS R5". `nil` when unknown.
    public let cameraModel: String?
    public let sidecars: [SourceFile]

    public init(file: SourceFile, kind: MediaKind, day: ShootDay, cameraMake: String?, cameraModel: String?, sidecars: [SourceFile]) {
        self.file = file
        self.kind = kind
        self.day = day
        self.cameraMake = cameraMake
        self.cameraModel = cameraModel
        self.sidecars = sidecars
    }

    public var totalSize: Int64 { sidecars.reduce(file.size) { $0 + $1.size } }
}

public struct DaySummary: Identifiable, Sendable {
    public var id: ShootDay { day }
    public let day: ShootDay
    public var photos = 0
    public var videos = 0
    public var audio = 0
    public var bytes: Int64 = 0

    public init(day: ShootDay) {
        self.day = day
    }

    public var fileCount: Int { photos + videos + audio }
}

/// What is new on a card, grouped so the user can assign each shoot day to a project.
public struct CardAnalysis: Sendable {
    public let items: [MediaItem]
    /// Media files skipped because they were imported before.
    public let alreadyImported: Int
    /// Files that are not photos, videos, audio or sidecars of them.
    public let ignoredFiles: Int
    public let failures: [BackupFailure]

    public init(items: [MediaItem], alreadyImported: Int, ignoredFiles: Int, failures: [BackupFailure]) {
        self.items = items
        self.alreadyImported = alreadyImported
        self.ignoredFiles = ignoredFiles
        self.failures = failures
    }

    public var isEmpty: Bool { items.isEmpty }
    public var totalBytes: Int64 { items.reduce(0) { $0 + $1.totalSize } }

    /// One entry per shoot day, oldest first.
    public var days: [DaySummary] {
        var summaries: [ShootDay: DaySummary] = [:]
        for item in items {
            var summary = summaries[item.day] ?? DaySummary(day: item.day)
            switch item.kind {
            case .photo: summary.photos += 1
            case .video: summary.videos += 1
            default: summary.audio += 1
            }
            summary.bytes += item.totalSize
            summaries[item.day] = summary
        }
        return summaries.values.sorted { $0.day < $1.day }
    }

    /// Distinct camera models, most files first.
    public var cameraModels: [String] {
        var counts: [String: Int] = [:]
        for item in items {
            if let model = item.cameraModel { counts[model, default: 0] += 1 }
        }
        return counts.sorted { $0.value != $1.value ? $0.value > $1.value : $0.key < $1.key }.map { $0.key }
    }

    public var hasUnknownCamera: Bool { items.contains { $0.cameraModel == nil } }

    public func itemCount(camera model: String?) -> Int {
        items.filter { $0.cameraModel == model }.count
    }

    public func make(forModel model: String) -> String? {
        items.first { $0.cameraModel == model }?.cameraMake
    }
}

/// Lists a card, works out each file's shoot day and camera, and leaves out what was imported before.
public struct CardAnalyzer {
    /// AVFoundation is only asked about this many clips per folder; cameras use one folder per body.
    static let videoProbesPerFolder = 3

    private let reader: MetadataReading
    private let fileManager: FileManager

    public init(reader: MetadataReading = SystemMetadataReader(), fileManager: FileManager = .default) {
        self.reader = reader
        self.fileManager = fileManager
    }

    public func analyze(
        source: URL,
        isAlreadyImported: (SourceFile) -> Bool,
        progress: (_ done: Int, _ total: Int) -> Void = { _, _ in }
    ) async throws -> CardAnalysis {
        let scan = try CardScanner(fileManager: fileManager).scan(source)
        let grouped = MediaClassifier.group(scan.files)

        var alreadyImported = 0
        let candidates = grouped.media.filter { media in
            if isAlreadyImported(media.file) {
                alreadyImported += 1
                return false
            }
            return true
        }

        // Read capture information.
        var infos: [CaptureInfo] = []
        infos.reserveCapacity(candidates.count)
        var probesByFolder: [String: Int] = [:]
        var videoCameraByFolder: [String: CaptureInfo] = [:]

        for (index, media) in candidates.enumerated() {
            try Task.checkCancellation()
            progress(index, candidates.count)

            var info = CaptureInfo()
            switch media.kind {
            case .photo:
                info = reader.photoInfo(at: media.file.url)
            case .video:
                if let xml = media.sidecars.first(where: { $0.relativePath.lowercased().hasSuffix(".xml") }),
                   xml.size < 2_000_000,
                   let text = try? String(contentsOf: xml.url, encoding: .utf8) {
                    info = MetadataParsing.parseClipXML(text)
                }
                if info.model == nil {
                    let folder = (media.file.relativePath as NSString).deletingLastPathComponent
                    if let known = videoCameraByFolder[folder] {
                        info.make = known.make
                        info.model = known.model
                    } else if probesByFolder[folder, default: 0] < Self.videoProbesPerFolder {
                        probesByFolder[folder, default: 0] += 1
                        let probed = await reader.videoInfo(at: media.file.url)
                        if probed.model != nil {
                            videoCameraByFolder[folder] = probed
                            info.make = probed.make
                            info.model = probed.model
                        }
                    }
                }
            default:
                break
            }
            infos.append(info)
        }
        progress(candidates.count, candidates.count)

        // Videos without camera information: if every photo on the card came from one camera,
        // the videos almost certainly did too.
        let photoCameras = Set(zip(candidates, infos).compactMap { media, info -> String? in
            media.kind == .photo ? info.model : nil
        })
        var onlyPhotoCamera: CaptureInfo?
        if photoCameras.count == 1, let index = candidates.indices.first(where: { candidates[$0].kind == .photo && infos[$0].model != nil }) {
            onlyPhotoCamera = infos[index]
        }

        var items: [MediaItem] = []
        items.reserveCapacity(candidates.count)
        for (media, info) in zip(candidates, infos) {
            var make = info.make
            var model = info.model
            if model == nil, media.kind == .video, let camera = onlyPhotoCamera {
                make = camera.make
                model = camera.model
            }
            let date = info.date ?? media.file.creationDate ?? media.file.modificationDate
            items.append(MediaItem(
                file: media.file,
                kind: media.kind,
                day: ShootDay(date),
                cameraMake: make,
                cameraModel: model,
                sidecars: media.sidecars
            ))
        }

        return CardAnalysis(
            items: items,
            alreadyImported: alreadyImported,
            ignoredFiles: grouped.other.count + grouped.unmatchedSidecars.count,
            failures: scan.failures
        )
    }
}
