import AppKit
import SDBackupCore
import SwiftUI

struct SettingsView: View {
    @EnvironmentObject private var model: AppModel
    @EnvironmentObject private var settings: AppSettings
    @State private var years: [Int] = []
    @State private var selectedYear = 0
    @State private var structure: FolderStructure = .standard
    @State private var editingStructure = false
    @State private var yearSource: AppModel.YearSource = .photos

    var body: some View {
        Form {
            Section("Projects folder") {
                HStack {
                    Image(systemName: "photo.on.rectangle")
                        .foregroundStyle(.secondary)
                        .frame(width: 20)
                    Text("Photos")
                    Text(settings.abbreviatedDestination)
                        .foregroundStyle(.secondary)
                        .lineLimit(1)
                        .truncationMode(.middle)
                    Spacer()
                    Button("Change…") { model.chooseDestination() }
                }
                HStack {
                    Image(systemName: "video")
                        .foregroundStyle(.secondary)
                        .frame(width: 20)
                    Text("Videos")
                    Text(settings.abbreviatedVideos ?? "Same as photos")
                        .foregroundStyle(.secondary)
                        .lineLimit(1)
                        .truncationMode(.middle)
                    Spacer()
                    if settings.videosURL != nil {
                        Button("Same as Photos") { model.keepVideosWithPhotos() }
                    }
                    Button("Change…") { model.chooseVideosFolder() }
                }
                HStack(alignment: .top) {
                    Text(verbatim: structureCaption)
                        .font(.caption)
                        .foregroundStyle(.secondary)
                        .fixedSize(horizontal: false, vertical: true)
                    Spacer()
                    Button("Folder Structure…") {
                        structure = model.folderStructure()
                        editingStructure = true
                    }
                }
            }

            Section("Move a year to another drive") {
                if let migration = model.migration {
                    VStack(alignment: .leading, spacing: 6) {
                        Text(verbatim: "Moving \(migration.yearText) to “\(migration.driveName)”…")
                        ProgressView(value: migration.progress.phase == .copying ? migration.progress.fractionCompleted : nil)
                        Button("Stop") { model.cancelMigration() }
                    }
                } else {
                    if settings.videosURL != nil {
                        Picker("From", selection: $yearSource) {
                            Text("Photos folder").tag(AppModel.YearSource.photos)
                            Text("Videos folder").tag(AppModel.YearSource.videos)
                        }
                        .pickerStyle(.segmented)
                    }
                    if years.isEmpty {
                        Text(verbatim: yearSource == .videos && settings.videosURL != nil
                            ? "There are no year folders in the videos folder yet."
                            : "There are no year folders in the projects folder yet.")
                            .foregroundStyle(.secondary)
                    } else {
                        HStack {
                            Picker("Year", selection: $selectedYear) {
                                ForEach(years, id: \.self) { year in
                                    Text(verbatim: String(year)).tag(year)
                                }
                            }
                            .fixedSize()
                            Spacer()
                            Button("Move to Drive…") { model.moveYear(selectedYear, from: yearSource) }
                                .disabled(!years.contains(selectedYear) || model.active != nil)
                        }
                    }
                }
                Text("Moves the whole year (photos, videos and editing projects) to a folder you choose, for example on your SSD, keeping the same structure. Each file is verified before it’s deleted from this Mac. Moved projects keep working from the drive; new projects still go to this Mac.")
                    .font(.caption)
                    .foregroundStyle(.secondary)
                    .fixedSize(horizontal: false, vertical: true)
            }

            Section("Cards") {
                Toggle("Ask for a project as soon as a card is inserted", isOn: $settings.autoBackupEnabled)
                Picker("Cards to import", selection: $settings.detection) {
                    ForEach(CardDetection.allCases) { mode in
                        Text(mode.title).tag(mode)
                    }
                }
                Text(settings.detection.explanation)
                    .font(.caption)
                    .foregroundStyle(.secondary)
            }

            Section("Safety") {
                Toggle("Verify every copied file", isOn: $settings.verifyCopies)
                Text("Reads each copy back and compares its SHA-256 checksum with the original. Slightly slower, strongly recommended.")
                    .font(.caption)
                    .foregroundStyle(.secondary)
                Toggle("Eject the card when the import finishes", isOn: $settings.ejectWhenDone)
            }

            Section("After importing") {
                Picker("Open new photos in", selection: $settings.photoApp) {
                    ForEach(PhotoApp.allCases) { app in
                        Text(app.title).tag(app)
                    }
                }
                Text(settings.photoApp.explanation)
                    .font(.caption)
                    .foregroundStyle(.secondary)
                    .fixedSize(horizontal: false, vertical: true)
                if settings.photoApp != .none, PhotoAppLauncher.applicationURL(for: settings.photoApp) == nil {
                    Label("\(settings.photoApp.title) isn’t installed on this Mac.", systemImage: "exclamationmark.triangle")
                        .font(.caption)
                        .foregroundStyle(.orange)
                }
            }

            Section("General") {
                Toggle("Open at login", isOn: $model.launchAtLogin)
                Toggle("Show notifications", isOn: $settings.notificationsEnabled)
                HStack {
                    Button("Open Log") { model.openLog() }
                    Button("Clear Recent") { model.clearHistory() }
                        .disabled(model.history.isEmpty)
                }
            }

            Section {
                HStack {
                    Button("Forget Imported Files…") { model.forgetImportedFiles() }
                    Button("Forget Camera Names") { model.forgetCameraNames() }
                }
                Text("Notch remembers which files of each card were imported, so a card you insert again only offers new files. It also remembers the folder name you gave each camera.")
                    .font(.caption)
                    .foregroundStyle(.secondary)
                    .fixedSize(horizontal: false, vertical: true)
            } header: {
                Text("Memory")
            } footer: {
                Text(verbatim: Self.versionText)
                    .font(.caption)
                    .foregroundStyle(.secondary)
                    .frame(maxWidth: .infinity, alignment: .center)
                    .padding(.top, 8)
            }
        }
        .formStyle(.grouped)
        .frame(width: 520, height: 760)
        .onChange(of: settings.detection) { _ in model.refreshConnectedVolumes() }
        .onChange(of: model.migration == nil) { _ in reloadYears() }
        .onChange(of: settings.destinationPath) { _ in reloadYears() }
        .onChange(of: settings.videosPath) { _ in
            if settings.videosURL == nil { yearSource = .photos }
            reloadYears()
        }
        .onChange(of: yearSource) { _ in reloadYears() }
        .onAppear { reloadYears() }
        .sheet(isPresented: $editingStructure) {
            FolderStructureView(structure: structure) { edited in
                guard model.saveFolderStructure(edited) else { return false }
                structure = model.folderStructure()
                return true
            }
        }
    }

    /// "Notch 1.2.0"; builds that aren't releases are "0.0.0-dev".
    static var versionText: String {
        let version = Bundle.main.infoDictionary?["CFBundleShortVersionString"] as? String ?? ""
        return version.isEmpty || version.hasPrefix("0.0.0") ? "Notch (test build)" : "Notch \(version)"
    }

    /// The structure's example paths, and where videos go when they have their own folder.
    private var structureCaption: String {
        var lines = structure.summary
        if let videos = settings.abbreviatedVideos {
            lines.append("Videos are saved in \(videos), in the same structure.")
        }
        return lines.joined(separator: "\n")
    }

    private func reloadYears() {
        structure = model.folderStructure()
        years = model.availableYears(in: settings.videosURL == nil ? .photos : yearSource)
        if !years.contains(selectedYear), let oldest = years.first {
            selectedYear = oldest
        }
    }
}

/// Hosts SettingsView in a regular window. Used instead of a SwiftUI `Settings` scene, which
/// can't be opened reliably from a menu bar app across macOS 13–15.
@MainActor
final class SettingsWindowController {
    private var window: NSWindow?

    func show(model: AppModel) {
        if window == nil {
            let view = SettingsView()
                .environmentObject(model)
                .environmentObject(model.settings)
            let window = NSWindow(contentViewController: NSHostingController(rootView: view))
            window.title = "Notch Settings"
            window.styleMask = [.titled, .closable]
            window.isReleasedWhenClosed = false
            window.center()
            self.window = window
        }
        AppActivation.bringToFront()
        window?.makeKeyAndOrderFront(nil)
    }
}
