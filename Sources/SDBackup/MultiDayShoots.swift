import Foundation
import SDBackupCore

/// Folders picked with Other… in the import panel (1.4): a project's photos go into `photos` and its
/// videos into `videos` (nil: their usual place), each in a new "date name" folder or, without
/// `newFolder`, straight into the folder. Paths: remembered on this Mac only.
struct OtherFolder: Codable, Hashable {
    var photos: String?
    var videos: String?
    var newFolder: Bool = true

    /// The folder's name, as the panel shows it: the photos' folder, or the videos' without photos.
    var name: String { URL(fileURLWithPath: photos ?? videos ?? "").lastPathComponent }

    /// Remembered by its photos' folder (or its videos', without photos).
    func samePlace(as other: OtherFolder) -> Bool {
        Self.standardized(photos ?? videos) == Self.standardized(other.photos ?? other.videos)
    }

    private static func standardized(_ path: String?) -> String {
        URL(fileURLWithPath: path ?? "").standardizedFileURL.path.lowercased()
    }
}

/// A shoot over several days or cards, e.g. a calendar: its folder ("AMD Koledar 2026") takes the
/// project folder's place in the folder structures, and each day's project goes inside it. With
/// `folder`, a remembered Other… folder instead (1.4), offered and finished the same way.
struct MultiDayShoot: Codable, Identifiable, Hashable {
    let id: UUID
    var name: String
    var started: Date
    var lastUsed: Date
    /// Finished shoots aren't offered in the import panel; they can be reopened in Settings.
    var finished: Bool
    var folder: OtherFolder? = nil

    /// Offered in the import panel: not finished, and used in the last 100 days.
    var isOpen: Bool {
        !finished && lastUsed > Date().addingTimeInterval(-MultiDayShootStore.hiddenAfter)
    }
}

/// The multi-day shoots, saved in series.json next to the project list. Local to this Mac: each
/// project records its shoot's name, so its folders don't depend on this list.
@MainActor
final class MultiDayShootStore: ObservableObject {
    /// Shoots unused for this long leave the import panel.
    static let hiddenAfter: TimeInterval = 100 * 24 * 60 * 60

    private let file: URL
    @Published private(set) var all: [MultiDayShoot] = []

    init(file: URL) {
        self.file = file
        if let data = try? Data(contentsOf: file),
           let saved = try? JSONCoding.decoder.decode([MultiDayShoot].self, from: data) {
            all = saved
        }
    }

    /// Shoots to offer in the import panel, most recently used first.
    var open: [MultiDayShoot] {
        all.filter(\.isOpen).sorted { $0.lastUsed > $1.lastUsed }
    }

    func shoot(id: UUID) -> MultiDayShoot? {
        all.first { $0.id == id }
    }

    /// The shoot with this name (reopened if it was finished), or a new one.
    @discardableResult
    func start(_ name: String) -> MultiDayShoot {
        let trimmed = name.trimmingCharacters(in: .whitespacesAndNewlines)
        if let index = all.firstIndex(where: { $0.folder == nil && $0.name.caseInsensitiveCompare(trimmed) == .orderedSame }) {
            all[index].finished = false
            all[index].lastUsed = Date()
            save()
            return all[index]
        }
        let shoot = MultiDayShoot(id: UUID(), name: trimmed, started: Date(), lastUsed: Date(), finished: false)
        all.append(shoot)
        save()
        return shoot
    }

    func markUsed(_ name: String) {
        guard let index = all.firstIndex(where: { $0.folder == nil && $0.name.caseInsensitiveCompare(name) == .orderedSame }) else { return }
        all[index].lastUsed = Date()
        save()
    }

    /// Remembers folders picked with Other… (reopened and updated if they were remembered before).
    @discardableResult
    func remember(_ folder: OtherFolder) -> MultiDayShoot {
        if let index = all.firstIndex(where: { $0.folder?.samePlace(as: folder) == true }) {
            all[index].folder = folder
            all[index].name = folder.name
            all[index].finished = false
            all[index].lastUsed = Date()
            save()
            return all[index]
        }
        let entry = MultiDayShoot(id: UUID(), name: folder.name, started: Date(), lastUsed: Date(), finished: false, folder: folder)
        all.append(entry)
        save()
        return entry
    }

    func finish(_ id: UUID) {
        guard let index = all.firstIndex(where: { $0.id == id }) else { return }
        all[index].finished = true
        save()
    }

    /// Back in the import panel, as if just used.
    func reopen(_ id: UUID) {
        guard let index = all.firstIndex(where: { $0.id == id }) else { return }
        all[index].finished = false
        all[index].lastUsed = Date()
        save()
    }

    func remove(_ id: UUID) {
        all.removeAll { $0.id == id }
        save()
    }

    private func save() {
        guard let data = try? JSONCoding.encoder.encode(all) else { return }
        try? data.write(to: file, options: .atomic)
    }
}
