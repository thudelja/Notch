import Foundation
import SDBackupCore

/// Projects, camera names and per-card import records.
///
/// Projects and import records live in ~/Library/Application Support/SD Backup; camera names in
/// UserDefaults.
@MainActor
final class LibraryStore {
    private enum Key {
        static let modelAliases = "cameraModelAliases"
        static let cardAliases = "cameraCardAliases"
    }

    private let directory: URL
    private(set) var projects: [ProjectInfo]
    /// Camera model → folder name, e.g. "Canon EOS R5" → "R5".
    private var modelAliases: [String: String]
    /// Card ID → folder name for files without camera information (e.g. an audio recorder's card).
    private var cardAliases: [String: String]

    init() {
        let support = FileManager.default.urls(for: .applicationSupportDirectory, in: .userDomainMask).first
            ?? FileManager.default.homeDirectoryForCurrentUser.appendingPathComponent("Library/Application Support", isDirectory: true)
        let directory = support.appendingPathComponent("SD Backup", isDirectory: true)
        try? FileManager.default.createDirectory(
            at: directory.appendingPathComponent("Imports", isDirectory: true),
            withIntermediateDirectories: true
        )
        self.directory = directory
        modelAliases = UserDefaults.standard.dictionary(forKey: Key.modelAliases) as? [String: String] ?? [:]
        cardAliases = UserDefaults.standard.dictionary(forKey: Key.cardAliases) as? [String: String] ?? [:]
        projects = Self.read([ProjectInfo].self, from: directory.appendingPathComponent("projects.json")) ?? []
    }

    // MARK: - Projects

    func recentProjects(limit: Int = 3) -> [ProjectInfo] {
        Array(projects.sorted { $0.lastUsed > $1.lastUsed }.prefix(limit))
    }

    func project(id: UUID) -> ProjectInfo? {
        projects.first { $0.id == id }
    }

    /// The existing project with this name and start day, or a new one.
    func projectNamed(_ name: String, startDay: ShootDay, rootPath: String) -> ProjectInfo {
        let trimmed = name.trimmingCharacters(in: .whitespacesAndNewlines)
        if let existing = projects.first(where: {
            $0.name.caseInsensitiveCompare(trimmed) == .orderedSame && $0.startDay == startDay && $0.rootPath == rootPath
        }) {
            return existing
        }
        return ProjectInfo(name: trimmed, startDay: startDay, rootPath: rootPath)
    }

    /// Saves new projects and moves the given ones to the top of the recent list.
    func markUsed(_ used: [ProjectInfo]) {
        let now = Date()
        for var project in used {
            project.lastUsed = now
            if let index = projects.firstIndex(where: { $0.id == project.id }) {
                projects[index] = project
            } else {
                projects.append(project)
            }
        }
        Self.write(projects, to: projectsURL)
    }

    // MARK: - Camera names

    func cameraName(forModel model: String, make: String?) -> String {
        modelAliases[model] ?? CameraNames.shortName(make: make, model: model)
    }

    func cameraName(forCard cardID: String) -> String? {
        cardAliases[cardID]
    }

    /// `names` maps camera models to folder names; the key "" holds the name for files without
    /// camera information, which is remembered for this card.
    func rememberCameraNames(_ names: [String: String], cardID: String) {
        for (key, name) in names {
            let trimmed = name.trimmingCharacters(in: .whitespacesAndNewlines)
            guard !trimmed.isEmpty else { continue }
            if key.isEmpty {
                cardAliases[cardID] = trimmed
            } else {
                modelAliases[key] = trimmed
            }
        }
        UserDefaults.standard.set(modelAliases, forKey: Key.modelAliases)
        UserDefaults.standard.set(cardAliases, forKey: Key.cardAliases)
    }

    func forgetCameraNames() {
        modelAliases = [:]
        cardAliases = [:]
        UserDefaults.standard.removeObject(forKey: Key.modelAliases)
        UserDefaults.standard.removeObject(forKey: Key.cardAliases)
    }

    // MARK: - Import records

    func ledger(forCard cardID: String) -> ImportLedger {
        Self.read(ImportLedger.self, from: ledgerURL(cardID)) ?? ImportLedger()
    }

    func saveLedger(_ ledger: ImportLedger, forCard cardID: String) {
        Self.write(ledger, to: ledgerURL(cardID))
    }

    /// After this, every file on every card is offered again.
    func forgetImports() {
        try? FileManager.default.removeItem(at: importsDirectory)
        try? FileManager.default.createDirectory(at: importsDirectory, withIntermediateDirectories: true)
    }

    // MARK: - Files

    private var projectsURL: URL { directory.appendingPathComponent("projects.json") }
    private var importsDirectory: URL { directory.appendingPathComponent("Imports", isDirectory: true) }

    private func ledgerURL(_ cardID: String) -> URL {
        importsDirectory.appendingPathComponent(BackupEngine.sanitizedFolderName(cardID, fallback: "card") + ".json")
    }

    private static func read<T: Decodable>(_ type: T.Type, from url: URL) -> T? {
        guard let data = try? Data(contentsOf: url) else { return nil }
        return try? JSONDecoder().decode(type, from: data)
    }

    private static func write<T: Encodable>(_ value: T, to url: URL) {
        guard let data = try? JSONEncoder().encode(value) else { return }
        try? data.write(to: url, options: .atomic)
    }
}
