import AppKit

/// A photo app Notch can open new photos in after an import (Settings › After importing).
enum PhotoApp: String, CaseIterable, Identifiable {
    case none
    case lightroomClassic
    case lightroom

    var id: String { rawValue }

    var title: String {
        switch self {
        case .none: return String(localized: "Don’t open")
        case .lightroomClassic: return "Lightroom Classic"
        case .lightroom: return "Lightroom"
        }
    }

    var explanation: String {
        switch self {
        case .none:
            return String(localized: "Notch only copies the photos into your projects folder.")
        case .lightroomClassic:
            return String(localized: "After each import, Lightroom Classic opens its Import window with the new photos. Choose “Add” at the top of that window to keep them where Notch put them.")
        case .lightroom:
            return String(localized: "After each import, Lightroom opens its Add Photos window with the new photos. Lightroom copies them into its own library and syncs them to Adobe’s cloud.")
        }
    }

    /// How macOS knows the app.
    fileprivate var bundleIdentifiers: [String] {
        switch self {
        case .none: return []
        case .lightroomClassic: return ["com.adobe.LightroomClassicCC7"]
        case .lightroom: return ["com.adobe.lightroomCC"]
        }
    }

    /// Looked for in the Applications folders when macOS doesn't know the bundle identifier.
    fileprivate var appNames: [String] {
        switch self {
        case .none: return []
        case .lightroomClassic: return ["Adobe Lightroom Classic.app"]
        case .lightroom: return ["Adobe Lightroom.app"]
        }
    }
}

/// Opens folders of new photos in a photo app, which then shows its own import window with them.
@MainActor
enum PhotoAppLauncher {
    enum Outcome {
        case opened
        case notInstalled
        case failed(String)
    }

    /// Where the app is installed, or nil. Adobe installs into a subfolder of /Applications.
    static func applicationURL(for app: PhotoApp) -> URL? {
        for identifier in app.bundleIdentifiers {
            if let url = NSWorkspace.shared.urlForApplication(withBundleIdentifier: identifier) {
                return url
            }
        }
        let fileManager = FileManager.default
        let roots = [
            URL(fileURLWithPath: "/Applications", isDirectory: true),
            fileManager.homeDirectoryForCurrentUser.appendingPathComponent("Applications", isDirectory: true),
        ]
        for root in roots {
            let subfolders = (try? fileManager.contentsOfDirectory(
                at: root, includingPropertiesForKeys: nil, options: [.skipsHiddenFiles])) ?? []
            let places = [root] + subfolders.filter { $0.pathExtension.isEmpty }
            for place in places {
                for name in app.appNames {
                    let candidate = place.appendingPathComponent(name, isDirectory: true)
                    if fileManager.fileExists(atPath: candidate.path) {
                        return candidate
                    }
                }
            }
        }
        return nil
    }

    /// Opens `folders` in `app`. An app that isn't running is started first and given time to
    /// open its catalog: Lightroom Classic 15 shows no Import window for files it receives while
    /// it is still launching.
    static func open(_ folders: [URL], in app: PhotoApp) async -> Outcome {
        guard app != .none, !folders.isEmpty else { return .opened }
        guard let appURL = applicationURL(for: app) else { return .notInstalled }
        let workspace = NSWorkspace.shared
        let configuration = NSWorkspace.OpenConfiguration()
        configuration.activates = true
        do {
            if !isRunning(appURL) {
                let running = try await workspace.openApplication(at: appURL, configuration: configuration)
                await waitUntilReady(running)
            }
            _ = try await workspace.open(folders, withApplicationAt: appURL, configuration: configuration)
            return .opened
        } catch {
            return .failed(error.localizedDescription)
        }
    }

    private static func isRunning(_ appURL: URL) -> Bool {
        guard let identifier = Bundle(url: appURL)?.bundleIdentifier else { return false }
        return !NSRunningApplication.runningApplications(withBundleIdentifier: identifier).isEmpty
    }

    private static func waitUntilReady(_ app: NSRunningApplication) async {
        let deadline = Date().addingTimeInterval(90)
        while !app.isFinishedLaunching, !app.isTerminated, Date() < deadline {
            try? await Task.sleep(nanoseconds: 500_000_000)
        }
        // The app opens its catalog or library after it has finished launching.
        try? await Task.sleep(nanoseconds: 5_000_000_000)
    }
}
