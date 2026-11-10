import Foundation
import SDBackupCore

/// What the user picked for one shoot day.
enum DayChoice: Hashable {
    case unassigned
    /// A new project, in the photos' and videos' default places or in folders chosen for it.
    case newProject
    /// A new project in an open multi-day shoot: it goes in the shoot's folder.
    case newInShoot(UUID)
    /// A new multi-day shoot, with this day's project as its first.
    case newShoot
    /// The same new project as the given (earlier or later) day.
    case sameAs(ShootDay)
    case existing(UUID)
    case skip

    /// A new project of any kind; days can be "same as" it.
    var isNewProject: Bool {
        switch self {
        case .newProject, .newInShoot, .newShoot: return true
        default: return false
        }
    }
}

struct ChoiceOption: Hashable, Identifiable {
    let choice: DayChoice
    let title: String
    var id: DayChoice { choice }
}

/// A card that has been read and is waiting for the user to say which project each shoot day
/// belongs to.
@MainActor
final class ImportDraft: ObservableObject, Identifiable {
    let id = UUID()
    let volume: VolumeInfo
    let analysis: CardAnalysis
    let days: [DaySummary]
    let recentProjects: [ProjectInfo]
    /// Multi-day shoots offered as "New in “…”".
    let openShoots: [MultiDayShoot]
    /// Camera models found on the card, most files first, plus "" for files without camera info.
    let cameraKeys: [String]
    let cameraFileCounts: [String: Int]
    let totalBytes: Int64
    let hasPhotos: Bool
    let hasVideos: Bool

    @Published private(set) var choices: [ShootDay: DayChoice] = [:]
    /// Existing projects picked by choosing their folder, beyond the recent ones.
    @Published private(set) var chosenProjects: [ProjectInfo] = []
    @Published var projectNames: [ShootDay: String] = [:]
    /// The name of a new multi-day shoot started on that day.
    @Published var shootNames: [ShootDay: String] = [:]
    /// Folders chosen for a new project's photos or videos (the project folder is made inside).
    @Published private(set) var photoFolders: [ShootDay: URL] = [:]
    @Published private(set) var videoFolders: [ShootDay: URL] = [:]
    /// Shoots finished in this panel. They still take this import; every day in them shows them as finished.
    @Published var finishedShoots: Set<UUID> = []
    @Published var cameraNames: [String: String]

    init(volume: VolumeInfo, analysis: CardAnalysis, recentProjects: [ProjectInfo], openShoots: [MultiDayShoot], cameraNames: [String: String]) {
        let days = analysis.days
        let keys = analysis.cameraModels + (analysis.hasUnknownCamera ? [""] : [])
        var counts: [String: Int] = [:]
        for item in analysis.items {
            counts[item.cameraModel ?? "", default: 0] += 1
        }

        self.volume = volume
        self.analysis = analysis
        self.days = days
        self.recentProjects = recentProjects
        self.openShoots = openShoots
        self.cameraKeys = keys
        self.cameraFileCounts = counts
        self.totalBytes = analysis.totalBytes
        self.cameraNames = cameraNames
        hasPhotos = analysis.items.contains { $0.kind == .photo }
        hasVideos = analysis.items.contains { $0.kind != .photo }

        // Most multi-day cards are one shoot: start with one new project for every day.
        if let first = days.first?.day {
            var initial: [ShootDay: DayChoice] = [:]
            for summary in days {
                initial[summary.day] = summary.day == first ? .newProject : .sameAs(first)
            }
            choices = initial
        }
    }

    // MARK: - Choices

    func choice(for day: ShootDay) -> DayChoice {
        choices[day] ?? .unassigned
    }

    func setChoice(_ choice: DayChoice, for day: ShootDay) {
        let previous = choices[day]
        choices[day] = choice
        // Days that pointed at this day's new project need a new answer.
        if previous?.isNewProject == true, !choice.isNewProject {
            for (other, value) in choices where value == .sameAs(day) {
                choices[other] = .unassigned
            }
        }
    }

    /// The user chose the folder of an existing project for this day.
    func useProject(_ project: ProjectInfo, for day: ShootDay) {
        if !knownProjects.contains(where: { $0.id == project.id }) {
            chosenProjects.append(project)
        }
        setChoice(.existing(project.id), for: day)
    }

    /// A folder chosen for a new project's photos or videos; nil goes back to the default place.
    func setFolder(_ folder: URL?, for branch: FolderStructure.Branch, day: ShootDay) {
        switch branch {
        case .photo: photoFolders[day] = folder
        case .video: videoFolders[day] = folder
        }
    }

    func folder(for branch: FolderStructure.Branch, day: ShootDay) -> URL? {
        branch == .photo ? photoFolders[day] : videoFolders[day]
    }

    private var knownProjects: [ProjectInfo] { recentProjects + chosenProjects }

    func shoot(_ id: UUID) -> MultiDayShoot? {
        openShoots.first { $0.id == id }
    }

    func options(for day: ShootDay) -> [ChoiceOption] {
        var options = [
            ChoiceOption(choice: .unassigned, title: String(localized: "Choose a project…")),
            ChoiceOption(choice: .newProject, title: String(localized: "New project")),
        ]
        for shoot in openShoots {
            options.append(ChoiceOption(choice: .newInShoot(shoot.id), title: String(localized: "New in “\(shoot.name)”")))
        }
        options.append(ChoiceOption(choice: .newShoot, title: String(localized: "New multi-day shoot…")))
        for summary in days where summary.day != day && choice(for: summary.day).isNewProject {
            options.append(ChoiceOption(choice: .sameAs(summary.day), title: String(localized: "Same as \(newProjectTitle(summary.day))")))
        }
        for project in knownProjects {
            let title = isOnConnectedDrive(project)
                ? "\(project.name) · \(project.startDay.folderDate)"
                : String(localized: "\(project.name) · \(project.startDay.folderDate) (drive not connected)")
            options.append(ChoiceOption(choice: .existing(project.id), title: title))
        }
        options.append(ChoiceOption(choice: .skip, title: String(localized: "Skip for now")))
        return options
    }

    private func newProjectTitle(_ owner: ShootDay) -> String {
        let name = trimmedName(owner)
        return name.isEmpty
            ? String(localized: "the new project on \(owner.folderDate)")
            : String(localized: "“\(name)” (new)")
    }

    private func trimmedName(_ day: ShootDay) -> String {
        (projectNames[day] ?? "").trimmingCharacters(in: .whitespacesAndNewlines)
    }

    private func trimmedShootName(_ day: ShootDay) -> String {
        (shootNames[day] ?? "").trimmingCharacters(in: .whitespacesAndNewlines)
    }

    // MARK: - Validation

    var isReady: Bool { blockingReason == nil }

    /// Why Import is disabled, if it is.
    var blockingReason: String? {
        var importsSomething = false
        for summary in days {
            switch choice(for: summary.day) {
            case .unassigned:
                return String(localized: "Choose a project for \(summary.day.folderDate).")
            case .skip:
                continue
            case .newShoot where trimmedShootName(summary.day).isEmpty:
                return String(localized: "Type a name for the multi-day shoot (\(summary.day.folderDate)).")
            case .newProject, .newInShoot, .newShoot:
                if trimmedName(summary.day).isEmpty {
                    return String(localized: "Type a name for the new project (\(summary.day.folderDate)).")
                }
            case .sameAs(let owner):
                if !choice(for: owner).isNewProject || trimmedName(owner).isEmpty {
                    return String(localized: "Type a name for the new project (\(owner.folderDate)).")
                }
            case .existing(let id):
                if let project = knownProjects.first(where: { $0.id == id }), !isAvailable(project, for: summary) {
                    return String(localized: "“\(project.name)” is on a drive that isn’t connected. Connect it or choose another project.")
                }
            }
            importsSomething = true
        }
        return importsSomething ? nil : String(localized: "Every date is skipped.")
    }

    // MARK: - Result

    /// The project for every day that isn't skipped. A new project starts on the earliest day
    /// assigned to it; its photos and videos go to their default places (`photosRoot`,
    /// `videosRoot`) or the folders chosen for them, and into its shoot's folder for a multi-day
    /// shoot. Nil when a folder isn't available.
    func resolvedProjects(store: LibraryStore, photosRoot: URL, videosRoot: URL) -> [ShootDay: ProjectInfo]? {
        var result: [ShootDay: ProjectInfo] = [:]
        var newProjectDays: [ShootDay: [ShootDay]] = [:]
        for summary in days {
            switch choice(for: summary.day) {
            case .newProject, .newInShoot, .newShoot:
                newProjectDays[summary.day, default: []].append(summary.day)
            case .sameAs(let owner):
                newProjectDays[owner, default: []].append(summary.day)
            case .existing(let id):
                if let project = store.project(id: id) {
                    result[summary.day] = project
                }
            case .unassigned, .skip:
                break
            }
        }
        for (owner, members) in newProjectDays {
            guard let start = members.min(), let shootName = series(for: owner) else { continue }
            // Folders can be chosen for a plain new project; a shoot's projects go in the shoot's folder.
            let isPlain = choice(for: owner) == .newProject
            guard let project = store.newProject(
                trimmedName(owner), startDay: start, series: shootName.isEmpty ? nil : shootName,
                photos: LibraryStore.Place(root: photosRoot, chosenFolder: isPlain ? photoFolders[owner] : nil),
                videos: LibraryStore.Place(root: videosRoot, chosenFolder: isPlain ? videoFolders[owner] : nil)
            ) else { return nil }
            for day in members {
                result[day] = project
            }
        }
        return result
    }

    /// The shoot name for a day's new project: "" for a plain project, nil when the day doesn't make one.
    private func series(for owner: ShootDay) -> String? {
        switch choice(for: owner) {
        case .newProject: return ""
        case .newInShoot(let id): return shoot(id)?.name ?? ""
        case .newShoot: return trimmedShootName(owner)
        default: return nil
        }
    }

    /// Multi-day shoots this import starts, and those it adds to (by name).
    var shootsUsed: (started: [String], continued: [String]) {
        var started: [String] = []
        var continued: [String] = []
        for summary in days {
            switch choice(for: summary.day) {
            case .newShoot: started.append(trimmedShootName(summary.day))
            case .newInShoot(let id): if let name = shoot(id)?.name { continued.append(name) }
            default: break
            }
        }
        return (started, continued)
    }

    /// Projects moved to an external drive are only available while it is connected.
    private func isOnConnectedDrive(_ project: ProjectInfo) -> Bool {
        Self.exists(project.rootPath) && (!project.hasSeparateVideoFolder || Self.exists(project.videoRoot))
    }

    /// Whether the folders this day's files go to are connected: the photos' and the videos'.
    private func isAvailable(_ project: ProjectInfo, for summary: DaySummary) -> Bool {
        (summary.photos == 0 || Self.exists(project.rootPath))
            && (summary.videos + summary.audio == 0 || Self.exists(project.videoRoot))
    }

    private static func exists(_ path: String) -> Bool {
        !path.isEmpty && FileManager.default.fileExists(atPath: path)
    }

    func cameraName(for item: MediaItem) -> String {
        let name = (cameraNames[item.cameraModel ?? ""] ?? "").trimmingCharacters(in: .whitespacesAndNewlines)
        return name.isEmpty ? String(localized: "Camera") : name
    }

    func cameraLabel(_ key: String) -> String {
        key.isEmpty ? String(localized: "No camera info") : key
    }
}
