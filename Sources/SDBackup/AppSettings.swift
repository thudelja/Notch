import Foundation
import SDBackupCore

enum CardDetection: String, CaseIterable, Identifiable {
    case memoryCards
    case anyRemovable

    var id: String { rawValue }

    var title: String {
        switch self {
        case .memoryCards: return String(localized: "SD and camera cards")
        case .anyRemovable: return String(localized: "Any removable drive")
        }
    }

    var explanation: String {
        switch self {
        case .memoryCards:
            return String(localized: "Cards in the built-in SD slot or a card reader, plus any removable drive with a camera (DCIM) folder.")
        case .anyRemovable:
            return String(localized: "Also includes USB flash drives and other removable media. External hard drives and SSDs are never included.")
        }
    }
}

/// User preferences, persisted in UserDefaults.
@MainActor
final class AppSettings: ObservableObject {
    private enum Key {
        static let destination = "destinationPath"
        static let videos = "videosPath"
        static let autoBackup = "autoBackupEnabled"
        static let detection = "cardDetection"
        static let verify = "verifyCopies"
        static let eject = "ejectWhenDone"
        static let notifications = "notificationsEnabled"
        static let photoApp = "photoApp"
        static let lastMigration = "lastMigrationPath"
        static let language = "language"
        /// Read by macOS at launch to pick the app's language.
        static let appleLanguages = "AppleLanguages"
    }

    private let defaults: UserDefaults

    @Published var destinationPath: String {
        didSet { defaults.set(destinationPath, forKey: Key.destination) }
    }
    /// A separate projects folder for videos, or "" to keep them with the photos.
    @Published var videosPath: String {
        didSet { defaults.set(videosPath, forKey: Key.videos) }
    }
    @Published var autoBackupEnabled: Bool {
        didSet { defaults.set(autoBackupEnabled, forKey: Key.autoBackup) }
    }
    @Published var detection: CardDetection {
        didSet { defaults.set(detection.rawValue, forKey: Key.detection) }
    }
    @Published var verifyCopies: Bool {
        didSet { defaults.set(verifyCopies, forKey: Key.verify) }
    }
    @Published var ejectWhenDone: Bool {
        didSet { defaults.set(ejectWhenDone, forKey: Key.eject) }
    }
    @Published var notificationsEnabled: Bool {
        didSet { defaults.set(notificationsEnabled, forKey: Key.notifications) }
    }
    /// The app new photos are opened in after an import.
    @Published var photoApp: PhotoApp {
        didSet { defaults.set(photoApp.rawValue, forKey: Key.photoApp) }
    }
    /// "" follows the Mac's language; "en" or "sl" picks one. macOS applies it when Notch next starts.
    @Published var language: String {
        didSet {
            defaults.set(language, forKey: Key.language)
            if language.isEmpty {
                defaults.removeObject(forKey: Key.appleLanguages)
            } else {
                defaults.set([language], forKey: Key.appleLanguages)
            }
        }
    }

    init(defaults: UserDefaults = .standard) {
        let defaultDestination = FileManager.default.homeDirectoryForCurrentUser
            .appendingPathComponent("Pictures/Projects", isDirectory: true).path
        defaults.register(defaults: [
            Key.destination: defaultDestination,
            Key.autoBackup: true,
            Key.detection: CardDetection.memoryCards.rawValue,
            Key.verify: true,
            Key.eject: false,
            Key.notifications: true,
            Key.photoApp: PhotoApp.none.rawValue,
        ])

        self.defaults = defaults
        destinationPath = defaults.string(forKey: Key.destination) ?? defaultDestination
        videosPath = defaults.string(forKey: Key.videos) ?? ""
        autoBackupEnabled = defaults.bool(forKey: Key.autoBackup)
        detection = CardDetection(rawValue: defaults.string(forKey: Key.detection) ?? "") ?? .memoryCards
        verifyCopies = defaults.bool(forKey: Key.verify)
        ejectWhenDone = defaults.bool(forKey: Key.eject)
        notificationsEnabled = defaults.bool(forKey: Key.notifications)
        photoApp = PhotoApp(rawValue: defaults.string(forKey: Key.photoApp) ?? "") ?? .none
        // A language picked for Notch in System Settings › General › Language & Region counts as picked here.
        let picked = defaults.string(forKey: Key.language)
            ?? (defaults.persistentDomain(forName: Bundle.main.bundleIdentifier ?? "")?[Key.appleLanguages] as? [String])?.first
        language = picked.map { $0.hasPrefix(AppLanguage.slovenian) ? AppLanguage.slovenian : $0.hasPrefix(AppLanguage.english) ? AppLanguage.english : "" } ?? ""
    }

    /// The language Notch shows after its next start: the picked one, or the Mac's.
    var nextLanguage: String {
        if !language.isEmpty { return language }
        let system = defaults.persistentDomain(forName: UserDefaults.globalDomain)?[Key.appleLanguages] as? [String] ?? Locale.preferredLanguages
        return Bundle.preferredLocalizations(from: [AppLanguage.english, AppLanguage.slovenian], forPreferences: system).first ?? AppLanguage.english
    }

    /// The chosen language isn't the one Notch shows: it switches when Notch starts again.
    var languageNeedsRestart: Bool { nextLanguage != AppLanguage.current }

    /// The folder a year was last moved to, offered first next time.
    var lastMigrationPath: String? {
        get { defaults.string(forKey: Key.lastMigration) }
        set { defaults.set(newValue, forKey: Key.lastMigration) }
    }

    var destinationURL: URL {
        URL(fileURLWithPath: (destinationPath as NSString).expandingTildeInPath, isDirectory: true)
    }

    var abbreviatedDestination: String {
        (destinationURL.path as NSString).abbreviatingWithTildeInPath
    }

    /// The separate projects folder for videos, or nil when videos go with the photos.
    var videosURL: URL? {
        guard !videosPath.isEmpty else { return nil }
        let url = URL(fileURLWithPath: (videosPath as NSString).expandingTildeInPath, isDirectory: true)
        return url.standardizedFileURL.path == destinationURL.standardizedFileURL.path ? nil : url
    }

    var abbreviatedVideos: String? {
        videosURL.map { ($0.path as NSString).abbreviatingWithTildeInPath }
    }
}
