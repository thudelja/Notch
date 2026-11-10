import Foundation
import SDBackupCore

/// The import that is currently copying.
struct ActiveBackup: Identifiable {
    let id = UUID()
    let volume: VolumeInfo
    /// The project name(s) being imported into.
    let title: String
    let startedAt = Date()
    var progress = BackupProgress(phase: .scanning)
    var copyStartedAt: Date?

    var estimatedSecondsRemaining: TimeInterval? {
        Format.remaining(progress, since: copyStartedAt)
    }
}

/// A year folder being moved to another drive.
struct ActiveMigration: Identifiable {
    let id = UUID()
    let year: Int
    let destinationRoot: URL
    let driveName: String
    var progress = BackupProgress(phase: .scanning)
    var copyStartedAt: Date?

    var yearText: String { String(year) }

    var estimatedSecondsRemaining: TimeInterval? {
        Format.remaining(progress, since: copyStartedAt)
    }
}

/// A finished import, shown in the menu and persisted between launches.
struct HistoryEntry: Identifiable, Codable, Equatable {
    enum Outcome: String, Codable {
        case completed
        case completedWithErrors
        case failed
        case cancelled
    }

    var id = UUID()
    /// Project name(s), or the card name when nothing was imported.
    let title: String
    let cardName: String
    let cardID: String
    let date: Date
    let outcome: Outcome
    let filesCopied: Int
    let bytesCopied: Int64
    let filesAlreadyBackedUp: Int
    let failedFiles: Int
    let message: String?
    /// The project folder to reveal in Finder.
    let folderPath: String?
    /// `nil` for entries saved before moving years existed; those are card imports.
    var kind: Kind? = nil

    enum Kind: String, Codable {
        case cardImport
        case migration
    }

    var summary: String {
        if kind == .migration {
            switch outcome {
            case .completed:
                return String(localized: "\(Format.count(filesCopied, "file")) · \(Format.bytes(bytesCopied)) · to \(cardName)")
            case .completedWithErrors:
                return Plural.text(filesCopied, "{0:N0} file moved to {1}", "{0:N0} files moved to {1}", cardName) + " · "
                    + Plural.text(failedFiles, "{0:N0} stayed on this Mac", "{0:N0} stayed on this Mac")
            case .failed, .cancelled:
                return message ?? String(localized: "Move stopped")
            }
        }
        switch outcome {
        case .completed:
            return filesCopied == 0
                ? String(localized: "Already there · from \(cardName)")
                : String(localized: "\(Format.count(filesCopied, "file")) · \(Format.bytes(bytesCopied)) · from \(cardName)")
        case .completedWithErrors:
            return Plural.text(filesCopied, "{0:N0} file copied", "{0:N0} files copied") + " · "
                + Plural.text(failedFiles, "{0:N0} couldn’t be copied", "{0:N0} couldn’t be copied")
        case .failed, .cancelled:
            return message ?? String(localized: "Import stopped")
        }
    }
}

enum Format {
    static func bytes(_ count: Int64) -> String {
        ByteCountFormatter.string(fromByteCount: count, countStyle: .file)
    }

    /// "1 photo", "1,234 photos": the noun in Notch's language and its form for the number (see `Plural`).
    static func count(_ number: Int, _ singular: String, _ plural: String? = nil) -> String {
        "\(number.formatted()) \(Plural.noun(number, singular, plural ?? singular + "s"))"
    }

    /// Time left, from the copy speed so far.
    static func remaining(_ progress: BackupProgress, since start: Date?) -> TimeInterval? {
        guard let start, progress.bytesProcessed > 0 else { return nil }
        let elapsed = Date().timeIntervalSince(start)
        guard elapsed > 3 else { return nil }
        let bytesPerSecond = Double(progress.bytesProcessed) / elapsed
        return Double(max(0, progress.bytesToCopy - progress.bytesProcessed)) / bytesPerSecond
    }

    static func duration(_ seconds: TimeInterval) -> String {
        let formatter = DateComponentsFormatter()
        formatter.allowedUnits = seconds >= 3600 ? [.hour, .minute] : [.minute, .second]
        formatter.unitsStyle = .abbreviated
        formatter.maximumUnitCount = 2
        return formatter.string(from: max(1, seconds)) ?? ""
    }
}

/// Texts about a count, translated as in the Windows version: Localizable.strings has "photo|photos" (or a whole text,
/// "{0:N0} file copied|{0:N0} files copied") with Slovenian's four forms, for 1, 2, 3–4 and 5 or more (101 like 1, 102 like
/// 2…): "fotografija|fotografiji|fotografije|fotografij". English uses the singular for 1 and the plural otherwise.
enum Plural {
    /// The noun for `number`: "photo" or "photos" in English.
    static func noun(_ number: Int, _ singular: String, _ plural: String) -> String {
        form(number, singular, plural)
    }

    /// A text about a count, like "{0} minutes ago": {0} is the count (`{0:N0}` with thousands separators), {1}… the
    /// other values.
    static func text(_ number: Int, _ singular: String, _ plural: String, _ values: String...) -> String {
        fill(form(number, singular, plural), [number.formatted()] + values)
    }

    private static func form(_ number: Int, _ singular: String, _ plural: String) -> String {
        let key = singular + "|" + plural
        let translated = Bundle.main.localizedString(forKey: key, value: nil, table: nil)
        let forms = translated.components(separatedBy: "|")
        guard translated != key, forms.count == 4 else { return number == 1 ? singular : plural }
        let rest = abs(number) % 100
        return forms[rest == 1 ? 0 : rest == 2 ? 1 : (rest == 3 || rest == 4) ? 2 : 3]
    }

    /// Replaces {0}, {0:N0}, {1}… in one pass, so a value that contains braces stays as it is.
    static func fill(_ text: String, _ values: [String]) -> String {
        var result = ""
        var rest = Substring(text)
        while let open = rest.firstIndex(of: "{") {
            result += rest[..<open]
            let tail = rest[rest.index(after: open)...]
            guard let close = tail.firstIndex(of: "}"),
                  let index = Int(tail[..<close].prefix { $0.isNumber }),
                  tail[..<close].allSatisfy({ $0.isNumber || $0 == ":" || $0 == "N" }),
                  values.indices.contains(index)
            else {
                result += "{"
                rest = tail
                continue
            }
            result += values[index]
            rest = tail[tail.index(after: close)...]
        }
        return result + rest
    }
}
