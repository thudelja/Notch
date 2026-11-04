import Foundation

/// One level of folders below the year folder.
///
/// A flat record with a `kind` and the options that kind uses, so the JSON is the same on the Mac
/// and on Windows (PORTING.md §2).
public struct FolderLevel: Codable, Hashable, Sendable {
    public enum Kind: String, Codable, Sendable, CaseIterable {
        /// "Photo" or "Video", by file type.
        case mediaType
        /// The project's start month.
        case month
        /// A fixed folder name, e.g. "Clients".
        case text
        /// Imports go into `unedited`; an empty `exported` folder with the same path is made beside it.
        case split
        /// The project folder itself: its date and name.
        case project
    }

    public enum MonthStyle: String, Codable, Sendable, CaseIterable {
        /// "11 November"
        case numberAndName
        /// "11"
        case number
        /// "November"
        case name
    }

    public enum MonthLanguage: String, Codable, Sendable, CaseIterable {
        case english
        case slovenian
    }

    public enum DateFormat: String, Codable, Sendable, CaseIterable {
        /// "02.11.2026"
        case dayMonthYear
        /// "2026-11-02"
        case yearMonthDay
        /// "20261102"
        case compact
    }

    public enum NameOrder: String, Codable, Sendable, CaseIterable {
        /// "02.11.2026 Ana Wedding"
        case dateFirst
        /// "Ana Wedding 02.11.2026"
        case nameFirst
    }

    public var kind: Kind
    public var photo: String?
    public var video: String?
    public var monthStyle: MonthStyle?
    public var monthLanguage: MonthLanguage?
    public var text: String?
    public var unedited: String?
    public var exported: String?
    public var dateFormat: DateFormat?
    public var nameOrder: NameOrder?

    public init(kind: Kind) {
        self.kind = kind
    }

    public static func mediaType(photo: String = "Photo", video: String = "Video") -> FolderLevel {
        var level = FolderLevel(kind: .mediaType)
        level.photo = photo
        level.video = video
        return level
    }

    public static func month(style: MonthStyle = .numberAndName, language: MonthLanguage = .english) -> FolderLevel {
        var level = FolderLevel(kind: .month)
        level.monthStyle = style
        level.monthLanguage = language
        return level
    }

    /// A level with a fixed name (kind `text`).
    public static func folder(_ name: String) -> FolderLevel {
        var level = FolderLevel(kind: .text)
        level.text = name
        return level
    }

    public static func split(unedited: String = "Unedited", exported: String = "Exported") -> FolderLevel {
        var level = FolderLevel(kind: .split)
        level.unedited = unedited
        level.exported = exported
        return level
    }

    public static func project(dateFormat: DateFormat = .dayMonthYear, nameOrder: NameOrder = .dateFirst) -> FolderLevel {
        var level = FolderLevel(kind: .project)
        level.dateFormat = dateFormat
        level.nameOrder = nameOrder
        return level
    }
}

/// A sample path in the structure editor.
public struct FolderExample: Hashable, Sendable {
    public let title: String
    public let path: String
}

/// How a projects folder lays out its projects, below the year folder (which always comes first, so
/// a year can be moved to another drive as one folder).
///
/// Saved in the projects folder's `.sdbackup/library.json`, so the Mac and Windows versions build the
/// same folders. Each project keeps a copy of the structure it was created with, so changing the
/// structure never moves existing projects.
///
///     standard: 2026/Photo/11 November/02.11.2026 Ana Wedding/R5/IMG_0001.CR3
///               2026/Video/11 November/02.11.2026 Ana Wedding/Media/R5/C0001.MP4
///                                                            /Project  /Assets  /Export
public struct FolderStructure: Codable, Hashable, Sendable {
    public enum Branch: Sendable {
        case photo
        case video
    }

    /// The levels below the year folder, top first. Exactly one is the project folder; only the
    /// Unedited/Exported split may come after it.
    public var levels: [FolderLevel]
    /// A folder per camera for the files (inside the media folder, for videos).
    public var cameraFolders: Bool
    /// Folders made in every video project.
    public var videoFolders: [String]
    /// The one of `videoFolders` that receives videos and audio; "" puts them in the project folder.
    public var mediaFolder: String

    public init(levels: [FolderLevel], cameraFolders: Bool = true, videoFolders: [String] = ["Media", "Project", "Assets", "Export"], mediaFolder: String = "Media") {
        self.levels = levels
        self.cameraFolders = cameraFolders
        self.videoFolders = videoFolders
        self.mediaFolder = mediaFolder
    }

    /// The structure Notch has always used.
    public static let standard = FolderStructure(levels: [.mediaType(), .month(), .project()])

    public var hasSplit: Bool { levels.contains { $0.kind == .split } }

    // MARK: - Folder names

    static let englishMonths = [
        "January", "February", "March", "April", "May", "June",
        "July", "August", "September", "October", "November", "December",
    ]
    static let slovenianMonths = [
        "januar", "februar", "marec", "april", "maj", "junij",
        "julij", "avgust", "september", "oktober", "november", "december",
    ]

    /// "11 November", "11" or "November" (or the Slovenian name).
    public static func monthFolderName(_ day: ShootDay, style: FolderLevel.MonthStyle = .numberAndName, language: FolderLevel.MonthLanguage = .english) -> String {
        let index = min(max(day.month, 1), 12) - 1
        let name = (language == .slovenian ? slovenianMonths : englishMonths)[index]
        switch style {
        case .numberAndName: return "\(ShootDay.twoDigits(day.month)) \(name)"
        case .number: return ShootDay.twoDigits(day.month)
        case .name: return name
        }
    }

    /// "02.11.2026 Ana Wedding" with the standard project level.
    public static func projectFolderName(name: String, startDay: ShootDay, level: FolderLevel = .project()) -> String {
        let date: String
        switch level.dateFormat ?? .dayMonthYear {
        case .dayMonthYear: date = startDay.folderDate
        case .yearMonthDay: date = "\(startDay.year)-\(ShootDay.twoDigits(startDay.month))-\(ShootDay.twoDigits(startDay.day))"
        case .compact: date = "\(startDay.year)\(ShootDay.twoDigits(startDay.month))\(ShootDay.twoDigits(startDay.day))"
        }
        let safeName = BackupEngine.sanitizedFolderName(name, fallback: "Untitled")
        return (level.nameOrder ?? .dateFirst) == .nameFirst ? "\(safeName) \(date)" : "\(date) \(safeName)"
    }

    /// This structure's project folder name for a project.
    public func projectFolderName(name: String, startDay: ShootDay) -> String {
        Self.projectFolderName(name: name, startDay: startDay, level: levels.first { $0.kind == .project } ?? .project())
    }

    // MARK: - Paths (components below the projects folder; the same on every OS)

    /// Where a project's files go: for photos the folder holding the camera folders, for videos the
    /// folder holding the video folders. Ends in the Unedited folder when the split comes after the
    /// project folder.
    public func filesPath(_ branch: Branch, name: String, startDay: ShootDay) -> [String] {
        components(branch, name: name, startDay: startDay, exported: false)
    }

    /// The empty Exported folder for a project, or nil without a split: the same path as the files
    /// down to the project folder, with the Exported name in place of the Unedited one.
    public func exportedPath(_ branch: Branch, name: String, startDay: ShootDay) -> [String]? {
        hasSplit ? components(branch, name: name, startDay: startDay, exported: true) : nil
    }

    private func components(_ branch: Branch, name: String, startDay: ShootDay, exported: Bool) -> [String] {
        var path = [String(startDay.year)]
        var passedSplit = false
        var passedProject = false
        for level in levels {
            switch level.kind {
            case .mediaType:
                path.append(branch == .photo ? Self.safe(level.photo, fallback: "Photo") : Self.safe(level.video, fallback: "Video"))
            case .month:
                path.append(Self.monthFolderName(startDay, style: level.monthStyle ?? .numberAndName, language: level.monthLanguage ?? .english))
            case .text:
                path.append(Self.safe(level.text, fallback: "Folder"))
            case .split:
                path.append(exported ? Self.safe(level.exported, fallback: "Exported") : Self.safe(level.unedited, fallback: "Unedited"))
                passedSplit = true
                // Split inside the project folder: …/Name/Exported
                if exported, passedProject { return path }
            case .project:
                path.append(Self.projectFolderName(name: name, startDay: startDay, level: level))
                passedProject = true
                // Split above the project folder: …/Exported/…/Name
                if exported, passedSplit { return path }
            }
        }
        return path
    }

    static func safe(_ name: String?, fallback: String) -> String {
        BackupEngine.sanitizedFolderName(name ?? "", fallback: fallback)
    }

    // MARK: - Checking

    /// What is wrong with the structure, in words for the editor; empty when it can be used.
    public var problems: [String] {
        var problems: [String] = []
        let projectIndices = levels.indices.filter { levels[$0].kind == .project }
        if projectIndices.count != 1 {
            problems.append("The structure needs exactly one project folder.")
        }
        if let project = projectIndices.first, levels[(project + 1)...].contains(where: { $0.kind != .split }) {
            problems.append("Only the Unedited / Exported folders can go inside the project folder.")
        }
        let once: [(FolderLevel.Kind, String)] = [(.mediaType, "Photo / Video"), (.month, "the month"), (.split, "Unedited / Exported")]
        for (kind, label) in once where levels.filter({ $0.kind == kind }).count > 1 {
            problems.append("Use \(label) only once.")
        }
        func blank(_ text: String?) -> Bool { (text ?? "").trimmingCharacters(in: .whitespacesAndNewlines).isEmpty }
        func same(_ a: String?, _ b: String?) -> Bool {
            (a ?? "").trimmingCharacters(in: .whitespaces).caseInsensitiveCompare((b ?? "").trimmingCharacters(in: .whitespaces)) == .orderedSame
        }
        for level in levels {
            switch level.kind {
            case .mediaType:
                if blank(level.photo) || blank(level.video) {
                    problems.append("The photo and video folders need names.")
                } else if same(level.photo, level.video) {
                    problems.append("The photo and video folders need different names.")
                }
            case .text:
                if blank(level.text) { problems.append("A folder level has no name.") }
            case .split:
                if blank(level.unedited) || blank(level.exported) {
                    problems.append("The Unedited and Exported folders need names.")
                } else if same(level.unedited, level.exported) {
                    problems.append("The Unedited and Exported folders need different names.")
                }
            case .month, .project:
                break
            }
        }
        if videoFolders.contains(where: { blank($0) }) {
            problems.append("Every video project folder needs a name.")
        }
        let lowered = videoFolders.map { $0.trimmingCharacters(in: .whitespaces).lowercased() }
        if Set(lowered).count != lowered.count {
            problems.append("Video project folders need different names.")
        }
        if !mediaFolder.isEmpty, !videoFolders.contains(where: { same($0, mediaFolder) }) {
            problems.append("The folder for videos must be one of the video project folders.")
        }
        return problems
    }

    public var isValid: Bool { problems.isEmpty }

    // MARK: - Describing

    /// The lines Settings shows under the projects folder, with a sample project.
    public var summary: [String] {
        let day = ShootDay(year: 2026, month: 11, day: 2)
        let camera = cameraFolders ? ["Camera"] : []
        var lines = ["Photos: " + Self.arrow(filesPath(.photo, name: "Name", startDay: day) + camera)]
        let media = mediaFolder.isEmpty ? [] : [mediaFolder]
        var videos = "Videos: " + Self.arrow(filesPath(.video, name: "Name", startDay: day) + media + camera)
        let others = videoFolders.filter { $0.caseInsensitiveCompare(mediaFolder) != .orderedSame }
        if !others.isEmpty {
            videos += ", plus " + Self.list(others) + (others.count == 1 ? " folder." : " folders.")
        }
        lines.append(videos)
        if let photoExports = exportedPath(.photo, name: "Name", startDay: day),
           let videoExports = exportedPath(.video, name: "Name", startDay: day) {
            lines.append(photoExports == videoExports
                ? "Exports: " + Self.arrow(photoExports)
                : "Exports: " + Self.arrow(photoExports) + " and " + Self.arrow(videoExports))
        }
        return lines
    }

    /// Sample paths for the structure editor: where a photo and a video go, and with a split the
    /// empty Exported folders.
    public func examples(separator: String) -> [FolderExample] {
        let day = ShootDay(year: 2026, month: 11, day: 2)
        let name = "Ana Wedding"
        let camera: [String] = cameraFolders ? ["R5"] : []
        let media: [String] = mediaFolder.isEmpty ? [] : [Self.safe(mediaFolder, fallback: "Media")]
        var examples = [
            FolderExample(title: "A photo", path: (filesPath(.photo, name: name, startDay: day) + camera + ["IMG_0001.CR3"]).joined(separator: separator)),
            FolderExample(title: "A video", path: (filesPath(.video, name: name, startDay: day) + media + camera + ["C0001.MP4"]).joined(separator: separator)),
        ]
        if let photo = exportedPath(.photo, name: name, startDay: day), let video = exportedPath(.video, name: name, startDay: day) {
            if photo == video {
                examples.append(FolderExample(title: "Exports", path: photo.joined(separator: separator) + separator))
            } else {
                examples.append(FolderExample(title: "Photo exports", path: photo.joined(separator: separator) + separator))
                examples.append(FolderExample(title: "Video exports", path: video.joined(separator: separator) + separator))
            }
        }
        return examples
    }

    static func arrow(_ components: [String]) -> String {
        components.joined(separator: " › ")
    }

    static func list(_ names: [String]) -> String {
        switch names.count {
        case 0: return ""
        case 1: return names[0]
        default: return names.dropLast().joined(separator: ", ") + " and " + names[names.count - 1]
        }
    }
}
