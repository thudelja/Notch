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
    /// A new project in folders picked with Other… (1.4).
    case other
    /// A new project in a remembered Other… folder (1.4).
    case inFolder(UUID)
    /// The same project as the given day: the previous day's, whatever it is, or another day's new project.
    case sameAs(ShootDay)
    case existing(UUID)
    case skip

    /// A new project of any kind; days can be "same as" it.
    var isNewProject: Bool {
        switch self {
        case .newProject, .newInShoot, .newShoot, .other, .inFolder: return true
        default: return false
        }
    }

    /// In folders picked with Other…, now or before.
    var isOther: Bool {
        switch self {
        case .other, .inFolder: return true
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
    @Published var projectNames: [ShootDay: String] = [:]
    /// The name of a new multi-day shoot started on that day.
    @Published var shootNames: [ShootDay: String] = [:]
    /// Folders picked with Other… for a day's photos or videos.
    @Published private(set) var photoFolders: [ShootDay: URL] = [:]
    @Published private(set) var videoFolders: [ShootDay: URL] = [:]
    /// Shoots finished in this panel. They still take this import; every day in them shows them as finished.
    @Published var finishedShoots: Set<UUID> = []
    /// Other…: a new "date name" folder in the picked folders (the default), or the files straight in.
    @Published private var newFolders: [ShootDay: Bool] = [:]
    /// The choice before Other…, for when its folder picker is closed.
    private var beforeOther: [ShootDay: DayChoice] = [:]
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
        if choice == .other, previous != .other {
            // Other… asks for its folders (see `AppModel.chooseOther`); closing goes back to this.
            beforeOther[day] = previous ?? .unassigned
            photoFolders[day] = nil
            videoFolders[day] = nil
        }
        // Days that follow this day ("Same as") need a new answer when it no longer has a project, and, unless it is
        // their previous day, when it is no longer a new project (only new projects are offered for days further away).
        for (other, value) in choices where value == .sameAs(day) {
            if root(of: day) == nil || (!choice.isNewProject && previousDay(of: other) != day) {
                choices[other] = .unassigned
            }
        }
    }

    func folder(for branch: FolderStructure.Branch, day: ShootDay) -> URL? {
        // A remembered Other… folder brings its own folders.
        if case .inFolder(let id) = choice(for: day), let remembered = shoot(id)?.folder {
            return (branch == .photo ? remembered.photos : remembered.videos).map { URL(fileURLWithPath: $0, isDirectory: true) }
        }
        return branch == .photo ? photoFolders[day] : videoFolders[day]
    }

    /// Other…'s folders were picked: `photos` for photos and `videos` for videos (nil: their usual place).
    func setOther(photos: URL?, videos: URL?, for day: ShootDay) {
        guard choice(for: day) == .other else { return }
        photoFolders[day] = photos
        videoFolders[day] = videos
    }

    /// Other…'s first folder picker was closed: the day goes back to what it was.
    func cancelOther(for day: ShootDay) {
        guard choice(for: day) == .other, photoFolders[day] == nil, videoFolders[day] == nil else { return }
        setChoice(beforeOther[day] ?? .unassigned, for: day)
    }

    /// Other…: a new "date name" folder in the picked folders, or the files straight into them.
    func newFolder(for day: ShootDay) -> Bool {
        if let value = newFolders[day] { return value }
        if case .inFolder(let id) = choice(for: day), let remembered = shoot(id)?.folder { return remembered.newFolder }
        return true
    }

    func setNewFolder(_ value: Bool, for day: ShootDay) {
        newFolders[day] = value
    }

    /// The project's name: typed, or, straight into a picked folder, the folder's name.
    func projectName(for day: ShootDay) -> String {
        let typed = trimmedName(day)
        guard typed.isEmpty, choice(for: day).isOther, !newFolder(for: day) else { return typed }
        return otherFolder(for: day)?.name ?? ""
    }

    /// The Other… folders a day goes to, as they'll be remembered.
    func otherFolder(for day: ShootDay) -> OtherFolder? {
        guard choice(for: day).isOther else { return nil }
        let photos = folder(for: .photo, day: day)?.path
        let videos = folder(for: .video, day: day)?.path
        guard photos != nil || videos != nil else { return nil }
        return OtherFolder(photos: photos, videos: videos, newFolder: newFolder(for: day))
    }

    /// Other… folders this import uses, to remember (or remember again) once it starts.
    var foldersUsed: [OtherFolder] { days.compactMap { otherFolder(for: $0.day) } }

    private var knownProjects: [ProjectInfo] { recentProjects }

    func shoot(_ id: UUID) -> MultiDayShoot? {
        openShoots.first { $0.id == id }
    }

    func options(for day: ShootDay) -> [ChoiceOption] {
        var options = [
            // Picks folders for the project anywhere (Other); a day without a project shows it as a hint instead.
            ChoiceOption(choice: .other, title: String(localized: "Choose a project…")),
            ChoiceOption(choice: .newProject, title: String(localized: "New project")),
        ]
        for shoot in openShoots {
            options.append(shoot.folder == nil
                ? ChoiceOption(choice: .newInShoot(shoot.id), title: String(localized: "New in “\(shoot.name)”"))
                : ChoiceOption(choice: .inFolder(shoot.id), title: String(localized: "In “\(shoot.name)”")))
        }
        options.append(ChoiceOption(choice: .newShoot, title: String(localized: "New multi-day shoot…")))
        // The previous day's project, whatever it is (a new or existing project, a shoot, a folder), right after New project.
        let previous = previousDay(of: day)
        if let previous, let target = root(of: previous), target != day {
            options.insert(ChoiceOption(choice: .sameAs(previous), title: previousDayTitle(target)), at: 2)
        }
        for summary in days where summary.day != day && summary.day != previous && choice(for: summary.day).isNewProject {
            options.append(ChoiceOption(choice: .sameAs(summary.day), title: sameAsTitle(summary.day)))
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

    /// The day listed above this one in the panel (the day before it on the card), if any.
    func previousDay(of day: ShootDay) -> ShootDay? {
        guard let index = days.firstIndex(where: { $0.day == day }), index > 0 else { return nil }
        return days[index - 1].day
    }

    /// The day whose choice a day's "Same as" leads to (following "Same as" days), or nil when it leads to no project
    /// (a skipped or undecided day, or a loop).
    func root(of day: ShootDay) -> ShootDay? {
        var day = day
        for _ in 0...days.count {
            let current = choice(for: day)
            if case .sameAs(let owner) = current {
                day = owner
                continue
            }
            if current.isNewProject { return day }
            if case .existing = current { return day }
            return nil
        }
        return nil
    }

    /// "Same as the previous day (“Ana”)": the project's name when it has one.
    private func previousDayTitle(_ root: ShootDay) -> String {
        var name = projectName(for: root)
        if case .existing(let id) = choice(for: root) {
            name = knownProjects.first { $0.id == id }?.name ?? ""
        }
        return name.isEmpty
            ? String(localized: "Same as the previous day")
            : String(localized: "Same as the previous day (“\(name)”)")
    }

    /// "Same as “Ana” (new)", or "Same as the new project on 02.11.2026" before it has a name.
    private func sameAsTitle(_ owner: ShootDay) -> String {
        let name = projectName(for: owner)
        return name.isEmpty
            ? String(localized: "Same as the new project on \(owner.folderDate)")
            : String(localized: "Same as “\(name)” (new)")
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
            case .other where otherFolder(for: summary.day) == nil:
                return String(localized: "Choose a folder for \(summary.day.folderDate).")
            case .newShoot where trimmedShootName(summary.day).isEmpty:
                return String(localized: "Type a name for the multi-day shoot (\(summary.day.folderDate)).")
            case .newProject, .newInShoot, .newShoot, .other, .inFolder:
                if projectName(for: summary.day).isEmpty {
                    return String(localized: "Type a name for the new project (\(summary.day.folderDate)).")
                }
            case .sameAs(let owner):
                guard let target = root(of: owner) else {
                    return String(localized: "Choose a project for \(owner.folderDate).")
                }
                if case .existing(let id) = choice(for: target) {
                    if let project = knownProjects.first(where: { $0.id == id }), !isAvailable(project, for: summary) {
                        return String(localized: "“\(project.name)” is on a drive that isn’t connected. Connect it or choose another project.")
                    }
                } else if projectName(for: target).isEmpty {
                    return String(localized: "Type a name for the new project (\(target.folderDate)).")
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
            case .newProject, .newInShoot, .newShoot, .other, .inFolder:
                newProjectDays[summary.day, default: []].append(summary.day)
            case .sameAs(let owner):
                guard let target = root(of: owner) else { break }
                if case .existing(let id) = choice(for: target) {
                    if let project = store.project(id: id) {
                        result[summary.day] = project
                    }
                } else {
                    newProjectDays[target, default: []].append(summary.day)
                }
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
            // Other… goes to the folders picked for it; other new projects to their default places (a shoot's in its folder).
            let ownerChoice = choice(for: owner)
            let chosen = ownerChoice.isOther
            let newFolder = !ownerChoice.isOther || self.newFolder(for: owner)
            guard let project = store.newProject(
                projectName(for: owner), startDay: start, series: shootName.isEmpty ? nil : shootName,
                photos: LibraryStore.Place(root: photosRoot, chosenFolder: chosen ? folder(for: .photo, day: owner) : nil, newFolder: newFolder),
                videos: LibraryStore.Place(root: videosRoot, chosenFolder: chosen ? folder(for: .video, day: owner) : nil, newFolder: newFolder)
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
        case .newProject, .other, .inFolder: return ""
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
            case .newInShoot(let id): if let shoot = shoot(id), shoot.folder == nil { continued.append(shoot.name) }
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
