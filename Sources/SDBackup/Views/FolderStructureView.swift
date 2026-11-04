import SDBackupCore
import SwiftUI

/// Settings › Folder Structure…: how new projects are laid out below the year folder.
struct FolderStructureView: View {
    /// Saves the structure; false keeps the sheet open (the projects folder couldn't be written).
    let onSave: (FolderStructure) -> Bool
    @Environment(\.dismiss) private var dismiss

    private let original: FolderStructure
    @State private var levels: [EditableLevel]
    @State private var cameraFolders: Bool
    @State private var videoFolders: [EditableName]
    /// The video folder that receives videos and audio; nil puts them in the project folder.
    @State private var mediaFolderID: UUID?

    init(structure: FolderStructure, onSave: @escaping (FolderStructure) -> Bool) {
        self.onSave = onSave
        original = structure
        let state = Self.editableState(of: structure)
        _levels = State(initialValue: state.levels)
        _cameraFolders = State(initialValue: structure.cameraFolders)
        _videoFolders = State(initialValue: state.videoFolders)
        _mediaFolderID = State(initialValue: state.mediaFolderID)
    }

    /// The structure as edited.
    private var structure: FolderStructure {
        FolderStructure(
            levels: levels.map(\.level),
            cameraFolders: cameraFolders,
            videoFolders: videoFolders.map(\.name),
            mediaFolder: videoFolders.first { $0.id == mediaFolderID }?.name ?? ""
        )
    }

    var body: some View {
        VStack(spacing: 0) {
            Form {
                Section {
                    HStack {
                        Text("Year").fontWeight(.medium)
                        Spacer()
                        Text(verbatim: "2026").foregroundStyle(.secondary)
                    }
                    .help("The year always comes first, so a whole year can be moved to another drive.")
                    ForEach(levels) { item in
                        LevelRow(
                            level: binding(forLevel: item.id),
                            canMoveUp: levels.first?.id != item.id,
                            canMoveDown: levels.last?.id != item.id,
                            move: { offset in move(item.id, by: offset) },
                            remove: removal(of: item)
                        )
                    }
                    addMenu
                } header: {
                    Text("Folders, from the top")
                } footer: {
                    Text("New projects get this structure. Existing projects keep their folders.")
                        .font(.caption)
                        .foregroundStyle(.secondary)
                }

                Section("Files") {
                    Toggle("A folder for each camera", isOn: $cameraFolders)
                    VStack(alignment: .leading, spacing: 6) {
                        Text("Folders in each video project")
                        ForEach(videoFolders) { folder in
                            HStack {
                                TextField("Folder name", text: binding(forFolder: folder.id), prompt: Text("Folder name"))
                                    .labelsHidden()
                                Button {
                                    videoFolders.removeAll { $0.id == folder.id }
                                    if mediaFolderID == folder.id { mediaFolderID = nil }
                                } label: {
                                    Image(systemName: "minus.circle")
                                }
                                .buttonStyle(.borderless)
                                .help("Remove this folder")
                            }
                        }
                        Button("Add Folder") { videoFolders.append(EditableName(name: "")) }
                    }
                    Picker("Videos and audio go into", selection: $mediaFolderID) {
                        ForEach(videoFolders) { folder in
                            Text(verbatim: folder.name.isEmpty ? "Untitled folder" : folder.name).tag(Optional(folder.id))
                        }
                        Text("The project folder").tag(UUID?.none)
                    }
                }

                Section("Example") {
                    ForEach(structure.examples(separator: "/"), id: \.self) { example in
                        VStack(alignment: .leading, spacing: 2) {
                            Text(example.title)
                                .font(.caption)
                                .foregroundStyle(.secondary)
                            Text(verbatim: example.path)
                                .font(.system(.callout, design: .monospaced))
                                .textSelection(.enabled)
                                .fixedSize(horizontal: false, vertical: true)
                        }
                    }
                    ForEach(structure.problems, id: \.self) { problem in
                        Label(problem, systemImage: "exclamationmark.triangle")
                            .font(.caption)
                            .foregroundStyle(.orange)
                    }
                }
            }
            .formStyle(.grouped)

            Divider()
            HStack {
                Button("Reset to Standard") { reset(to: .standard) }
                    .disabled(structure == .standard)
                Spacer()
                Button("Cancel", role: .cancel) { dismiss() }
                    .keyboardShortcut(.cancelAction)
                Button("Save") {
                    if onSave(structure) { dismiss() }
                }
                .keyboardShortcut(.defaultAction)
                .disabled(!structure.isValid || structure == original)
            }
            .padding(16)
        }
        .frame(width: 500, height: 680)
    }

    private var addMenu: some View {
        let used = Set(levels.map(\.level.kind))
        return Menu("Add a Level") {
            Button("Photo / Video") { add(.mediaType()) }
                .disabled(used.contains(.mediaType))
            Button("Month") { add(.month()) }
                .disabled(used.contains(.month))
            Button("Folder with a Fixed Name") { add(.folder("Folder")) }
            Button("Unedited / Exported") { add(.split()) }
                .disabled(used.contains(.split))
        }
        .fixedSize()
    }

    // Bindings by ID, so removing a row never leaves a view holding a stale index.
    private func binding(forLevel id: UUID) -> Binding<FolderLevel> {
        Binding(
            get: { levels.first { $0.id == id }?.level ?? FolderLevel(kind: .text) },
            set: { newValue in
                if let index = levels.firstIndex(where: { $0.id == id }) { levels[index].level = newValue }
            }
        )
    }

    private func binding(forFolder id: UUID) -> Binding<String> {
        Binding(
            get: { videoFolders.first { $0.id == id }?.name ?? "" },
            set: { newValue in
                if let index = videoFolders.firstIndex(where: { $0.id == id }) { videoFolders[index].name = newValue }
            }
        )
    }

    /// Nil for the project folder, which every structure has.
    private func removal(of item: EditableLevel) -> (() -> Void)? {
        guard item.level.kind != .project else { return nil }
        let id = item.id
        return { levels.removeAll { $0.id == id } }
    }

    /// New levels go just above the project folder; they can be moved from there.
    private func add(_ level: FolderLevel) {
        let index = levels.firstIndex { $0.level.kind == .project } ?? levels.count
        levels.insert(EditableLevel(level: level), at: index)
    }

    private func move(_ id: UUID, by offset: Int) {
        guard let index = levels.firstIndex(where: { $0.id == id }) else { return }
        let target = index + offset
        guard levels.indices.contains(target) else { return }
        levels.swapAt(index, target)
    }

    private func reset(to structure: FolderStructure) {
        let state = Self.editableState(of: structure)
        levels = state.levels
        cameraFolders = structure.cameraFolders
        videoFolders = state.videoFolders
        mediaFolderID = state.mediaFolderID
    }

    private static func editableState(of structure: FolderStructure) -> (levels: [EditableLevel], videoFolders: [EditableName], mediaFolderID: UUID?) {
        let folders = structure.videoFolders.map { EditableName(name: $0) }
        let media = structure.mediaFolder.isEmpty
            ? nil
            : folders.first { $0.name.caseInsensitiveCompare(structure.mediaFolder) == .orderedSame }?.id
        return (structure.levels.map { EditableLevel(level: $0) }, folders, media)
    }
}

private struct EditableLevel: Identifiable {
    let id = UUID()
    var level: FolderLevel
}

private struct EditableName: Identifiable {
    let id = UUID()
    var name: String
}

/// One level of the structure, with its options and buttons to move or remove it.
private struct LevelRow: View {
    @Binding var level: FolderLevel
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
                .help("Move up")
            Button { move(1) } label: { Image(systemName: "chevron.down") }
                .disabled(!canMoveDown)
                .help("Move down")
            Button { remove?() } label: { Image(systemName: "minus.circle") }
                .disabled(remove == nil)
                .help(removeHelp)
        }
        .buttonStyle(.borderless)
    }

    private var removeHelp: String {
        remove == nil ? "Every structure has a project folder" : "Remove this level"
    }

    private var title: String {
        switch level.kind {
        case .mediaType: return "Photo / Video"
        case .month: return "Month"
        case .text: return "Folder"
        case .split: return "Unedited / Exported"
        case .project: return "Project"
        }
    }

    @ViewBuilder
    private var options: some View {
        switch level.kind {
        case .mediaType:
            HStack {
                field("Photo", \.photo)
                field("Video", \.video)
            }
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
            field("Folder name", \.text)
        case .split:
            HStack {
                field("Unedited", \.unedited)
                field("Exported", \.exported)
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
