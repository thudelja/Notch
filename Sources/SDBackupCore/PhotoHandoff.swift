import Foundation

/// What to hand to a photo app (Lightroom) after an import: the folders that received new photos.
///
/// Folders rather than files keep the hand-off small whatever the size of the card, and photo apps
/// recognise the photos they already have, so a card inserted again only shows its new ones.
public enum PhotoHandoff {
    /// The distinct folders of the photos this import actually copied, sorted. Videos, audio,
    /// sidecars and files that were already there are left out; an import without new photos
    /// gives an empty list.
    public static func folders(from result: BackupResult) -> [URL] {
        var seen = Set<String>()
        var folders: [URL] = []
        for file in result.processedFiles where file.wasCopied {
            guard MediaClassifier.kind(ofFileNamed: file.destination.lastPathComponent) == .photo else { continue }
            let folder = file.destination.deletingLastPathComponent().standardizedFileURL
            if seen.insert(folder.path).inserted {
                folders.append(folder)
            }
        }
        return folders.sorted { $0.path < $1.path }
    }

    /// How many photos this import copied (what the folders hold that is new).
    public static func photoCount(in result: BackupResult) -> Int {
        result.processedFiles.filter {
            $0.wasCopied && MediaClassifier.kind(ofFileNamed: $0.destination.lastPathComponent) == .photo
        }.count
    }
}
