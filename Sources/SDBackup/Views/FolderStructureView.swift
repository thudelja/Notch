import SDBackupCore
import SwiftUI

/// One structure being edited (photos or videos).
@MainActor
final class StructureDraft: ObservableObject {
    let branch: FolderStructure.Branch
    let original: FolderStructure
    @Published var levels: [EditableLevel] = []
    @Published var cameraFolders = true
    @Published var videoFolders: [EditableName] = []
    /// The video folder that receives videos and audio; nil puts them in the project folder.
    @Published var mediaFolderID: UUID?

    init(_ branch: FolderStructure.Branch, structure: FolderStructure) {
        self.branch = branch
        original = structure
        reset(to: structure)
    }

    /// The structure as edited.
    var structure: FolderStructure {
        FolderStructure(
            levels: levels.map(\.level),
            cameraFolders: cameraFolders,
            videoFolders: videoFolders.map(\.name),
            mediaFolder: videoFolders.first { $0.id == mediaFolderID }?.name ?? ""
        )
    }

    var problems: [String] { structure.problems(for: branch) }
    var isChanged: Bool { structure != original }

    func reset(to structure: FolderStructure) {
        levels = structure.levels.map { EditableLevel(level: $0) }
        cameraFolders = structure.cameraFolders
        videoFolders = structure.videoFolders.map { EditableName(name: $0) }
        mediaFolderID = structure.mediaFolder.isEmpty
            ? nil
            : videoFolders.first { $0.name.caseInsensitiveCompare(structure.mediaFolder) == .orderedSame }?.id
    }

    /// New levels go just above the project folder; they can be moved from there.
    func add(_ level: FolderLevel) {
        let index = levels.firstIndex { $0.level.kind == .project } ?? levels.count
        levels.insert(EditableLevel(level: level), at: index)
    }

    func move(_ id: UUID, by offset: Int) {
        guard let index = levels.firstIndex(where: { $0.id == id }) else { return }
        let target = index + offset
        guard levels.indices.contains(target) else { return }
        levels.swapAt(index, target)
    }

    func remove(_ id: UUID) {
        levels.removeAll { $0.id == id }
    }

    // Bindings by ID, so removing a row never leaves a view holding a stale index.
    func binding(forLevel id: UUID) -> Binding<FolderLevel> {
        Binding(
            get: { self.levels.first { $0.id == id }?.level ?? FolderLevel(kind: .text) },
            set: { newValue in
                if let index = self.levels.firstIndex(where: { $0.id == id }) { self.levels[index].level = newValue }
            }
        )
    }

    func binding(forFolder id: UUID) -> Binding<String> {
        Binding(
            get: { self.videoFolders.first { $0.id == id }?.name ?? "" },
            set: { newValue in
                if let index = self.videoFolders.firstIndex(where: { $0.id == id }) { self.videoFolders[index].name = newValue }
            }
        )
    }
}

struct EditableLevel: Identifiable {
    let id = UUID()
    var level: FolderLevel
}

struct EditableName: Identifiable {
    let id = UUID()
    var name: String
}

/// Settings › Folder Structure…: how new projects' photos and videos are laid out, each with its
/// own structure (tabs). The year always comes first.
struct FolderStructureView: View {
    /// Photos and videos go to different folders: the Photo / Video level is left out.
    let separateFolders: Bool
    /// Saves both structures; false keeps the sheet open (a projects folder couldn't be written).
    let onSave: (_ photo: FolderStructure, _ video: FolderStructure) -> Bool
    @Environment(\.dismiss) private var dismiss

    @StateObject private var photos: StructureDraft
    @StateObject private var videos: StructureDraft
    @State private var tab: FolderStructure.Branch = .photo

    init(photo: FolderStructure, video: FolderStructure, separateFolders: Bool,
         onSave: @escaping (_ photo: FolderStructure, _ video: FolderStructure) -> Bool) {
        self.separateFolders = separateFolders
        self.onSave = onSave
        _photos = StateObject(wrappedValue: StructureDraft(.photo, structure: photo))
        _videos = StateObject(wrappedValue: StructureDraft(.video, structure: video))
    }

    private var current: StructureDraft { tab == .photo ? photos : videos }

    /// In one folder, photos and videos need different Photo / Video folder names.
    private var clash: String? {
        guard !separateFolders,
              let photo = photos.structure.levels.first(where: { $0.kind == .mediaType })?.photo,
              let video = videos.structure.levels.first(where: { $0.kind == .mediaType })?.video,
              photo.trimmingCharacters(in: .whitespaces).caseInsensitiveCompare(video.trimmingCharacters(in: .whitespaces)) == .orderedSame
        else { return nil }
        return String(localized: "Photos and videos go to the same folder, so their Photo and Video folders need different names.")
    }

    private var canSave: Bool {
        photos.problems.isEmpty && videos.problems.isEmpty && clash == nil && (photos.isChanged || videos.isChanged)
    }

    var body: some View {
        VStack(spacing: 0) {
            Picker("Files", selection: $tab) {
                Text("Photos").tag(FolderStructure.Branch.photo)
                Text("Videos").tag(FolderStructure.Branch.video)
            }
            .pickerStyle(.segmented)
            .labelsHidden()
            .padding([.horizontal, .top], 16)
            .padding(.bottom, 4)

            StructurePane(draft: current, separateFolders: separateFolders, clash: clash)
                .id(tab)

            Divider()
            HStack {
                Button(tab == .photo ? String(localized: "Reset Photos to Standard") : String(localized: "Reset Videos to Standard")) {
                    // The standard structure, with folder names in Notch's language.
                    let standard = FolderStructure.defaultFor(language: AppLanguage.current)
                    current.reset(to: separateFolders ? standard.withoutMediaType : standard)
                }
                Spacer()
                Button("Cancel", role: .cancel) { dismiss() }
                    .keyboardShortcut(.cancelAction)
                Button("Save") {
                    if onSave(photos.structure, videos.structure) { dismiss() }
                }
                .keyboardShortcut(.defaultAction)
                .disabled(!canSave)
            }
            .padding(16)
        }
        .frame(width: 520, height: 700)
    }
}

/// The editor for one structure: its levels, files and example paths.
private struct StructurePane: View {
    @ObservedObject var draft: StructureDraft
    let separateFolders: Bool
    let clash: String?

    var body: some View {
        Form {
            Section {
                HStack {
                    Text("Year").fontWeight(.medium)
                    Spacer()
                    Text(verbatim: "2026").foregroundStyle(.secondary)
                }
                .help(String(localized: "The year always comes first, so a whole year can be moved to another drive."))
                ForEach(draft.levels) { item in
                    LevelRow(
                        level: draft.binding(forLevel: item.id),
                        branch: draft.branch,
                        separateFolders: separateFolders,
                        canMoveUp: draft.levels.first?.id != item.id,
                        canMoveDown: draft.levels.last?.id != item.id,
                        move: { offset in draft.move(item.id, by: offset) },
                        remove: removal(of: item)
                    )
                }
                addMenu
            } header: {
                Text("Folders, from the top")
            } footer: {
                Text(draft.branch == .photo
                     ? String(localized: "New projects’ photos get this structure. Existing projects keep their folders.")
                     : String(localized: "New projects’ videos get this structure. Existing projects keep their folders."))
                    .font(.caption)
                    .foregroundStyle(.secondary)
            }

            Section("Files") {
                Toggle("A folder for each camera", isOn: $draft.cameraFolders)
                if draft.branch == .video {
                    VStack(alignment: .leading, spacing: 6) {
                        Text("Folders in each video project")
                        ForEach(draft.videoFolders) { folder in
                            HStack {
                                TextField("Folder name", text: draft.binding(forFolder: folder.id), prompt: Text("Folder name"))
                                    .labelsHidden()
                                Button {
                                    draft.videoFolders.removeAll { $0.id == folder.id }
                                    if draft.mediaFolderID == folder.id { draft.mediaFolderID = nil }
                                } label: {
                                    Image(systemName: "minus.circle")
                                }
                                .buttonStyle(.borderless)
                                .help(String(localized: "Remove this folder"))
                            }
                        }
                        Button("Add Folder") { draft.videoFolders.append(EditableName(name: "")) }
                    }
                    Picker("Videos and audio go into", selection: $draft.mediaFolderID) {
                        ForEach(draft.videoFolders) { folder in
                            Text(verbatim: folder.name.isEmpty ? String(localized: "Untitled folder") : folder.name).tag(Optional(folder.id))
                        }
                        Text("The project folder").tag(UUID?.none)
                    }
                }
            }

            Section("Example") {
                example(draft.branch == .photo ? String(localized: "A photo") : String(localized: "A video"),
                        shown.examplePath(draft.branch).joined(separator: "/"))
                if let exports = shown.exampleExports(draft.branch) {
                    example(String(localized: "Exports"), exports.joined(separator: "/") + "/")
                }
                example(String(localized: "In a multi-day shoot"),
                        shown.examplePath(draft.branch, series: "AMD Koledar 2026").joined(separator: "/"))
                ForEach(draft.problems + (clash.map { [$0] } ?? []), id: \.self) { problem in
                    Label(problem, systemImage: "exclamationmark.triangle")
                        .font(.caption)
                        .foregroundStyle(.orange)
                }
            }
        }
        .formStyle(.grouped)
    }

    private func example(_ title: String, _ path: String) -> some View {
        VStack(alignment: .leading, spacing: 2) {
            Text(title)
                .font(.caption)
                .foregroundStyle(.secondary)
            Text(verbatim: path)
                .font(.system(.callout, design: .monospaced))
                .textSelection(.enabled)
                .fixedSize(horizontal: false, vertical: true)
        }
    }

    /// The structure as new projects will get it: without the Photo / Video level when photos and
    /// videos go to different folders.
    private var shown: FolderStructure {
        separateFolders ? draft.structure.withoutMediaType : draft.structure
    }

    /// Nil for the project folder, which every structure has.
    private func removal(of item: EditableLevel) -> (() -> Void)? {
        guard item.level.kind != .project else { return nil }
        let id = item.id
        return { [draft] in draft.remove(id) }
    }

    private var addMenu: some View {
        let used = Set(draft.levels.map(\.level.kind))
        return Menu("Add a Level") {
            Button(draft.branch == .photo ? String(localized: "Photo Folder") : String(localized: "Video Folder")) { draft.add(.newMediaType()) }
                .disabled(used.contains(.mediaType))
            Button("Month") { draft.add(.newMonth()) }
                .disabled(used.contains(.month))
            Button("Folder with a Fixed Name") { draft.add(.newFolder()) }
            Button("Unedited / Exported") { draft.add(.newSplit()) }
                .disabled(used.contains(.split))
        }
        .fixedSize()
    }
}

/// One level of the structure, with its options and buttons to move or remove it.
private struct LevelRow: View {
    @Binding var level: FolderLevel
    let branch: FolderStructure.Branch
    let separateFolders: Bool
    let canMoveUp: Bool
    let canMoveDown: Bool
    let move: (Int) -> Void
    /// Nil for the project folder, which every structure has.
    let remove: (() -> Void)?

    private static let sampleDay = ShootDay(year: 2026, month: 11, day: 2)

    var body: some View {
        HStack(alignment: .center, spacing: 8) {
            VStack(alignment: .leading, spacing: 6) {
                Text(title).fontWeight(.medium)
                options
            }
            Spacer(minLength: 8)
            Button { move(-1) } label: { Image(systemName: "chevron.up") }
                .disabled(!canMoveUp)
                .help(String(localized: "Move up"))
            Button { move(1) } label: { Image(systemName: "chevron.down") }
                .disabled(!canMoveDown)
                .help(String(localized: "Move down"))
            Button { remove?() } label: { Image(systemName: "minus.circle") }
                .disabled(remove == nil)
                .help(removeHelp)
        }
        .buttonStyle(.borderless)
    }

    private var removeHelp: String {
        remove == nil ? String(localized: "Every structure has a project folder") : String(localized: "Remove this level")
    }

    private var title: String {
        switch level.kind {
        case .mediaType: return branch == .photo ? String(localized: "Photo folder") : String(localized: "Video folder")
        case .month: return String(localized: "Month")
        case .text: return String(localized: "Folder")
        case .split: return String(localized: "Unedited / Exported")
        case .project: return String(localized: "Project")
        }
    }

    @ViewBuilder
    private var options: some View {
        switch level.kind {
        case .mediaType:
            if branch == .photo {
                field(String(localized: "Photo"), \.photo)
            } else {
                field(String(localized: "Video"), \.video)
            }
            Text(separateFolders
                 ? String(localized: "Left out: photos and videos go to different folders.")
                 : String(localized: "Keeps photos and videos apart in the same folder."))
                .font(.caption)
                .foregroundStyle(.secondary)
                .fixedSize(horizontal: false, vertical: true)
        case .month:
            HStack {
                Picker("Month style", selection: choice(\.monthStyle, default: .numberAndName)) {
                    ForEach(FolderLevel.MonthStyle.allCases, id: \.self) { style in
                        Text(verbatim: FolderStructure.monthFolderName(Self.sampleDay, style: style, language: level.monthLanguage ?? .english)).tag(style)
                    }
                }
                .labelsHidden()
                Picker("Month names", selection: choice(\.monthLanguage, default: .english)) {
                    Text("English names").tag(FolderLevel.MonthLanguage.english)
                    Text("Slovenian names").tag(FolderLevel.MonthLanguage.slovenian)
                }
                .labelsHidden()
            }
        case .text:
            field(String(localized: "Folder name"), \.text)
        case .split:
            HStack {
                field(String(localized: "Unedited"), \.unedited)
                field(String(localized: "Exported"), \.exported)
            }
            Text("Imports go into the first folder; the second is made beside it, empty, for your exports.")
                .font(.caption)
                .foregroundStyle(.secondary)
                .fixedSize(horizontal: false, vertical: true)
        case .project:
            HStack {
                Picker("Date", selection: choice(\.dateFormat, default: .dayMonthYear)) {
                    ForEach(FolderLevel.DateFormat.allCases, id: \.self) { format in
                        Text(verbatim: Self.sampleDate(format)).tag(format)
                    }
                }
                .labelsHidden()
                Picker("Order", selection: choice(\.nameOrder, default: .dateFirst)) {
                    Text("Date, then name").tag(FolderLevel.NameOrder.dateFirst)
                    Text("Name, then date").tag(FolderLevel.NameOrder.nameFirst)
                }
                .labelsHidden()
            }
        }
    }

    private func field(_ prompt: String, _ keyPath: WritableKeyPath<FolderLevel, String?>) -> some View {
        TextField(prompt, text: Binding(
            get: { level[keyPath: keyPath] ?? "" },
            set: { level[keyPath: keyPath] = $0 }
        ), prompt: Text(prompt))
        .labelsHidden()
        .help(prompt)
    }

    private func choice<Value>(_ keyPath: WritableKeyPath<FolderLevel, Value?>, default fallback: Value) -> Binding<Value> {
        Binding(
            get: { level[keyPath: keyPath] ?? fallback },
            set: { level[keyPath: keyPath] = $0 }
        )
    }

    private static func sampleDate(_ format: FolderLevel.DateFormat) -> String {
        // "02.11.2026 Name" → "02.11.2026"
        let name = FolderStructure.projectFolderName(name: "x", startDay: sampleDay, level: .project(dateFormat: format))
        return String(name.dropLast(2))
    }
}
