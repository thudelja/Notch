import Foundation
import SDBackupCore

/// A shoot over several days or cards, e.g. a calendar: its folder ("AMD Koledar 2026") takes the
/// project folder's place in the folder structures, and each day's project goes inside it.
struct MultiDayShoot: Codable, Identifiable, Hashable {
    let id: UUID
    var name: String
    var started: Date
    var lastUsed: Date
    /// Finished shoots aren't offered in the import panel; they can be reopened in Settings.
    var finished: Bool

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
        if let index = all.firstIndex(where: { $0.name.caseInsensitiveCompare(trimmed) == .orderedSame }) {
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
        guard let index = all.firstIndex(where: { $0.name.caseInsensitiveCompare(name) == .orderedSame }) else { return }
        all[index].lastUsed = Date()
        save()
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
