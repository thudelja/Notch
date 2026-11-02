import Foundation
import SDBackupCore

/// What the user picked for one shoot day.
enum DayChoice: Hashable {
    case unassigned
    case newProject
    /// The same new project as the given (earlier or later) day.
    case sameAs(ShootDay)
    case existing(UUID)
    case skip
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
        let previous = choices[day]
        choices[day] = choice
        // Days that pointed at this day's new project need a new answer.
        if previous == .newProject, choice != .newProject {
            for (other, value) in choices where value == .sameAs(day) {
                choices[other] = .unassigned
            }
        }
    }

    func options(for day: ShootDay) -> [ChoiceOption] {
        var options = [
            ChoiceOption(choice: .unassigned, title: "Choose a project…"),
            ChoiceOption(choice: .newProject, title: "New project"),
        ]
        for summary in days where summary.day != day && choice(for: summary.day) == .newProject {
            options.append(ChoiceOption(choice: .sameAs(summary.day), title: "Same as \(newProjectTitle(summary.day))"))
        }
        for project in recentProjects {
            options.append(ChoiceOption(choice: .existing(project.id), title: "\(project.name) · \(project.startDay.folderDate)"))
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
            case .unassigned:
                return "Choose a project for \(summary.day.folderDate)."
            case .skip:
                continue
            case .newProject:
                if trimmedName(summary.day).isEmpty {
                    return "Type a name for the new project (\(summary.day.folderDate))."
                }
            case .sameAs(let owner):
                if choice(for: owner) != .newProject || trimmedName(owner).isEmpty {
                    return "Type a name for the new project (\(owner.folderDate))."
                }
            case .existing:
                break
            }
            importsSomething = true
        }
        return importsSomething ? nil : "Every date is skipped."
    }

    // MARK: - Result

    /// The project for every day that isn't skipped. A new project starts on the earliest day
    /// assigned to it.
    func resolvedProjects(store: LibraryStore, rootPath: String) -> [ShootDay: ProjectInfo] {
        var result: [ShootDay: ProjectInfo] = [:]
        var newProjectDays: [ShootDay: [ShootDay]] = [:]
        for summary in days {
            switch choice(for: summary.day) {
            case .newProject:
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
            guard choice(for: owner) == .newProject, let start = members.min() else { continue }
            let project = store.projectNamed(trimmedName(owner), startDay: start, rootPath: rootPath)
            for day in members {
                result[day] = project
            }
        }
        return result
    }

    func cameraName(for item: MediaItem) -> String {
        let name = (cameraNames[item.cameraModel ?? ""] ?? "").trimmingCharacters(in: .whitespacesAndNewlines)
        return name.isEmpty ? "Camera" : name
    }

    func cameraLabel(_ key: String) -> String {
        key.isEmpty ? "No camera info" : key
    }
}
