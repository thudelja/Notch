import Foundation

public enum MediaKind: String, Codable, Sendable {
    case photo
    case video
    case audio
    /// Belongs to another file: Sony/Canon clip XML, GoPro .THM/.LRV, DJI .LRF/.SRT, photo .XMP …
    case sidecar
    /// Camera system files, databases, anything else. Not imported.
    case other
}

/// A photo, video or audio file together with the sidecar files that belong to it.
public struct GroupedMedia: Sendable {
    public let file: SourceFile
    public let kind: MediaKind
    public internal(set) var sidecars: [SourceFile]
}

public enum MediaClassifier {
    static let photoExtensions: Set<String> = [
        "jpg", "jpeg", "heic", "heif", "hif", "png", "tif", "tiff", "webp", "avif",
        "dng", "cr2", "cr3", "crw", "nef", "nrw", "arw", "srf", "sr2", "raf", "orf", "rw2",
        "pef", "srw", "x3f", "3fr", "fff", "iiq", "rwl", "gpr", "insp",
    ]
    static let videoExtensions: Set<String> = [
        "mp4", "mov", "m4v", "mxf", "mts", "m2ts", "avi", "mkv", "crm", "braw", "r3d", "insv",
        "3gp", "mpg", "mpeg", "wmv",
    ]
    static let audioExtensions: Set<String> = ["wav", "bwf", "mp3", "m4a", "aac", "aif", "aiff", "flac"]
    static let sidecarExtensions: Set<String> = ["xml", "xmp", "thm", "lrf", "lrv", "srt", "aae"]

    public static func kind(ofFileNamed name: String) -> MediaKind {
        let ext = (name as NSString).pathExtension.lowercased()
        if photoExtensions.contains(ext) { return .photo }
        if videoExtensions.contains(ext) { return .video }
        if audioExtensions.contains(ext) { return .audio }
        if sidecarExtensions.contains(ext) { return .sidecar }
        return .other
    }

    /// Splits card files into media (with their sidecars attached), sidecars that belong to no
    /// media file, and other files.
    ///
    /// A sidecar belongs to the media file in the same folder whose name it starts with, e.g.
    /// C0001M01.XML → C0001.MP4, GX010123.THM → GX010123.MP4, IMG_0001.XMP → IMG_0001.CR3.
    public static func group(_ files: [SourceFile]) -> (media: [GroupedMedia], unmatchedSidecars: [SourceFile], other: [SourceFile]) {
        var media: [GroupedMedia] = []
        var sidecars: [SourceFile] = []
        var other: [SourceFile] = []

        for file in files {
            let mediaKind = Self.kind(ofFileNamed: (file.relativePath as NSString).lastPathComponent)
            switch mediaKind {
            case .photo, .video, .audio:
                media.append(GroupedMedia(file: file, kind: mediaKind, sidecars: []))
            case .sidecar:
                sidecars.append(file)
            case .other:
                other.append(file)
            }
        }

        // Index media by folder and base name.
        var exact: [String: [String: Int]] = [:]
        var byFolder: [String: [(base: String, index: Int)]] = [:]
        for (index, item) in media.enumerated() {
            let (folder, base) = folderAndBase(item.file.relativePath)
            if exact[folder, default: [:]][base] == nil {
                exact[folder, default: [:]][base] = index
            }
            byFolder[folder, default: []].append((base, index))
        }

        var unmatched: [SourceFile] = []
        for sidecar in sidecars {
            let (folder, base) = folderAndBase(sidecar.relativePath)
            if let index = exact[folder]?[base] {
                media[index].sidecars.append(sidecar)
                continue
            }
            var best: (length: Int, index: Int)?
            for candidate in byFolder[folder] ?? [] where base.hasPrefix(candidate.base) {
                if candidate.base.count > (best?.length ?? 0) {
                    best = (candidate.base.count, candidate.index)
                }
            }
            if let best {
                media[best.index].sidecars.append(sidecar)
            } else {
                unmatched.append(sidecar)
            }
        }
        return (media, unmatched, other)
    }

    /// ("DCIM/100CANON", "IMG_0001") for "DCIM/100CANON/IMG_0001.CR3". Base names are upper-cased.
    static func folderAndBase(_ relativePath: String) -> (String, String) {
        let path = relativePath as NSString
        let base = (path.lastPathComponent as NSString).deletingPathExtension.uppercased()
        return (path.deletingLastPathComponent, base)
    }
}
