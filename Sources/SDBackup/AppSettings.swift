import Foundation

enum CardDetection: String, CaseIterable, Identifiable {
    case memoryCards
    case anyRemovable

    var id: String { rawValue }

    var title: String {
        switch self {
        case .memoryCards: return "SD and camera cards"
        case .anyRemovable: return "Any removable drive"
        }
    }

    var explanation: String {
        switch self {
        case .memoryCards:
            return "Cards in the built-in SD slot or a card reader, plus any removable drive with a camera (DCIM) folder."
        case .anyRemovable:
            return "Also includes USB flash drives and other removable media. External hard drives and SSDs are never included."
        }
    }
}

/// User preferences, persisted in UserDefaults.
@MainActor
final class AppSettings: ObservableObject {
    private enum Key {
        static let destination = "destinationPath"
        static let autoBackup = "autoBackupEnabled"
        static let detection = "cardDetection"
        static let verify = "verifyCopies"
        static let eject = "ejectWhenDone"
        static let notifications = "notificationsEnabled"
        static let photoApp = "photoApp"
        static let lastMigration = "lastMigrationPath"
    }

    private let defaults: UserDefaults

    @Published var destinationPath: String {
        didSet { defaults.set(destinationPath, forKey: Key.destination) }
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
        autoBackupEnabled = defaults.bool(forKey: Key.autoBackup)
        detection = CardDetection(rawValue: defaults.string(forKey: Key.detection) ?? "") ?? .memoryCards
        verifyCopies = defaults.bool(forKey: Key.verify)
        ejectWhenDone = defaults.bool(forKey: Key.eject)
        notificationsEnabled = defaults.bool(forKey: Key.notifications)
        photoApp = PhotoApp(rawValue: defaults.string(forKey: Key.photoApp) ?? "") ?? .none
    }

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
}
