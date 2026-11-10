import Foundation
import SDBackupCore

/// What the user picked for one shoot day.
enum DayChoice: Hashable {
    case unassigned
    /// A new project in the projects folder.
    case newProject
    /// A new project in a folder the user chose: its dated folder is made inside it.
    case newProjectIn(URL)
    /// The same new project as the given (earlier or later) day.
    case sameAs(ShootDay)
    case existing(UUID)
    /// "New project in a folder…": asks for the folder. Never stays selected.
    case chooseFolder
    case skip

    /// A new project, in the projects folder or in a chosen folder.
    var isNewProject: Bool {
        switch self {
        case .newProject, .newProjectIn: return true
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
    /// Camera models found on the card, most files first, plus "" for files without camera info.
    let cameraKeys: [String]
    let cameraFileCounts: [String: Int]
    let totalBytes: Int64

    @Published private(set) var choices: [ShootDay: DayChoice] = [:]
    /// Existing projects picked by choosing their folder, beyond the recent ones.
    @Published private(set) var chosenProjects: [ProjectInfo] = []
    @Published var projectNames: [ShootDay: String] = [:]
    @Published var cameraNames: [String: String]

    init(volume: VolumeInfo, analysis: CardAnalysis, recentProjects: [ProjectInfo], cameraNames: [String: String]) {
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
        self.cameraKeys = keys
        self.cameraFileCounts = counts
        self.totalBytes = analysis.totalBytes
        self.cameraNames = cameraNames

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
        guard choice != .chooseFolder else { return }
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

    private var knownProjects: [ProjectInfo] { recentProjects + chosenProjects }

    func options(for day: ShootDay) -> [ChoiceOption] {
        var options = [
            ChoiceOption(choice: .unassigned, title: "Choose a project…"),
            ChoiceOption(choice: .newProject, title: "New project"),
        ]
        if case .newProjectIn(let folder) = choice(for: day) {
            options.append(ChoiceOption(choice: .newProjectIn(folder), title: "New project in “\(folder.lastPathComponent)”"))
        }
        options.append(ChoiceOption(choice: .chooseFolder, title: "New project in a folder…"))
        for summary in days where summary.day != day && choice(for: summary.day).isNewProject {
            options.append(ChoiceOption(choice: .sameAs(summary.day), title: "Same as \(newProjectTitle(summary.day))"))
        }
        for project in knownProjects {
            let missing = isOnConnectedDrive(project) ? "" : " (drive not connected)"
            options.append(ChoiceOption(choice: .existing(project.id), title: "\(project.name) · \(project.startDay.folderDate)\(missing)"))
        }
        options.append(ChoiceOption(choice: .skip, title: "Skip for now"))
        return options
    }

    private func newProjectTitle(_ owner: ShootDay) -> String {
        let name = trimmedName(owner)
        return name.isEmpty ? "the new project on \(owner.folderDate)" : "“\(name)” (new)"
    }

    private func trimmedName(_ day: ShootDay) -> String {
        (projectNames[day] ?? "").trimmingCharacters(in: .whitespacesAndNewlines)
    }

    // MARK: - Validation

    var isReady: Bool { blockingReason == nil }

    /// Why Import is disabled, if it is.
    var blockingReason: String? {
        var importsSomething = false
        for summary in days {
            switch choice(for: summary.day) {
            case .unassigned, .chooseFolder:
                return "Choose a project for \(summary.day.folderDate)."
            case .skip:
                continue
            case .newProject, .newProjectIn:
                if trimmedName(summary.day).isEmpty {
                    return "Type a name for the new project (\(summary.day.folderDate))."
                }
            case .sameAs(let owner):
                if !choice(for: owner).isNewProject || trimmedName(owner).isEmpty {
                    return "Type a name for the new project (\(owner.folderDate))."
                }
            case .existing(let id):
                if let project = knownProjects.first(where: { $0.id == id }), !isAvailable(project, for: summary) {
                    return "“\(project.name)” is on a drive that isn’t connected. Connect it or choose another project."
                }
            }
            importsSomething = true
        }
        return importsSomething ? nil : "Every date is skipped."
    }

    // MARK: - Result

    /// The project for every day that isn't skipped. A new project starts on the earliest day
    /// assigned to it and is created in the projects folder at `root` (videos in `videosRoot`, when
    /// separate) or in the folder chosen for it. Nil when a folder isn't available.
    func resolvedProjects(store: LibraryStore, root: URL, videosRoot: URL?) -> [ShootDay: ProjectInfo]? {
        var result: [ShootDay: ProjectInfo] = [:]
        var newProjectDays: [ShootDay: [ShootDay]] = [:]
        for summary in days {
            switch choice(for: summary.day) {
            case .newProject, .newProjectIn:
                newProjectDays[summary.day, default: []].append(summary.day)
            case .sameAs(let owner):
                newProjectDays[owner, default: []].append(summary.day)
            case .existing(let id):
                if let project = store.project(id: id) {
                    result[summary.day] = project
                }
            case .unassigned, .chooseFolder, .skip:
                break
            }
        }
        for (owner, members) in newProjectDays {
            guard let start = members.min() else { continue }
            let project: ProjectInfo?
            switch choice(for: owner) {
            case .newProject:
                project = store.projectNamed(trimmedName(owner), startDay: start, in: root, videosRoot: videosRoot)
            case .newProjectIn(let folder):
                project = store.projectNamed(trimmedName(owner), startDay: start, inFolder: folder, structure: store.structure(at: root))
            default:
                continue
            }
            guard let project else { return nil }
            for day in members {
                result[day] = project
            }
        }
        return result
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
        return name.isEmpty ? "Camera" : name
    }

    func cameraLabel(_ key: String) -> String {
        key.isEmpty ? "No camera info" : key
    }
}
