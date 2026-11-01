import Foundation

/// Decides which entries on a card are operating-system metadata rather than user data.
///
/// The list is deliberately short: when in doubt a file is backed up.
public enum FileFilter {
    static let ignoredNames: Set<String> = [
        // macOS
        ".Spotlight-V100",
        ".fseventsd",
        ".Trashes",
        ".TemporaryItems",
        ".DocumentRevisions-V100",
        ".DS_Store",
        ".apdisk",
        // Windows
        "System Volume Information",
        "$RECYCLE.BIN",
        "RECYCLER",
    ]

    public static func shouldSkip(_ name: String) -> Bool {
        // "._name" files are AppleDouble resource forks that macOS writes to FAT/exFAT volumes.
        name.hasPrefix("._") || ignoredNames.contains(name)
    }
}
