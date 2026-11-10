import AppKit
import SDBackupCore
import SwiftUI

struct SettingsView: View {
    @EnvironmentObject private var model: AppModel
    @EnvironmentObject private var settings: AppSettings
    @EnvironmentObject private var shoots: MultiDayShootStore
    @State private var years: [Int] = []
    @State private var selectedYear = 0
    @State private var photoStructure: FolderStructure = .standard
    @State private var videoStructure: FolderStructure = .standard
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
                        reloadStructures()
                        editingStructure = true
                    }
                }
            }

            Section("Multi-day shoots") {
                if shoots.all.isEmpty {
                    Text("A multi-day shoot (for example a calendar) keeps all its projects in one folder. Start one in the import panel with “New multi-day shoot…”.")
                        .font(.caption)
                        .foregroundStyle(.secondary)
                        .fixedSize(horizontal: false, vertical: true)
                } else {
                    ForEach(shoots.all.sorted { $0.lastUsed > $1.lastUsed }) { shoot in
                        HStack {
                            VStack(alignment: .leading, spacing: 2) {
                                Text(verbatim: shoot.name)
                                Text(shootStatus(shoot))
                                    .font(.caption)
                                    .foregroundStyle(.secondary)
                            }
                            Spacer()
                            if shoot.isOpen {
                                HoldToFinishButton(title: String(localized: "Hold to finish"), doneTitle: String(localized: "Finished")) {
                                    model.finishShoot(shoot.id)
                                }
                                .id(shoot.id)
                            } else {
                                Button("Reopen") { model.reopenShoot(shoot.id) }
                            }
                        }
                    }
                    Text("Open shoots appear in the import panel as “New in …”. A shoot not used for 100 days leaves the panel; reopen it here.")
                        .font(.caption)
                        .foregroundStyle(.secondary)
                        .fixedSize(horizontal: false, vertical: true)
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
            FolderStructureView(photo: photoStructure, video: videoStructure, separateFolders: model.hasSeparateFolders) { photo, video in
                guard model.saveFolderStructures(photo: photo, video: video) else { return false }
                reloadStructures()
                return true
            }
        }
    }

    /// "Notch 1.2.0"; builds that aren't releases are "0.0.0-dev".
    static var versionText: String {
        let version = Bundle.main.infoDictionary?["CFBundleShortVersionString"] as? String ?? ""
        return version.isEmpty || version.hasPrefix("0.0.0") ? "Notch (test build)" : "Notch \(version)"
    }

    /// Where a new project's photo and video go, as example paths.
    private var structureCaption: String {
        let separate = model.hasSeparateFolders
        let photo = separate ? photoStructure.withoutMediaType : photoStructure
        let video = separate ? videoStructure.withoutMediaType : videoStructure
        var lines = [
            String(localized: "Photos: \(photo.examplePath(.photo).joined(separator: " › "))"),
            String(localized: "Videos: \(video.examplePath(.video).joined(separator: " › "))"),
        ]
        if let videos = settings.abbreviatedVideos {
            lines.append(String(localized: "Videos are saved in \(videos). Photo and Video folders are left out because photos and videos go to different folders."))
        }
        return lines.joined(separator: "\n")
    }

    private func shootStatus(_ shoot: MultiDayShoot) -> String {
        let used = shoot.lastUsed.formatted(date: .abbreviated, time: .omitted)
        if shoot.finished { return String(localized: "Finished · last used \(used)") }
        if !shoot.isOpen { return String(localized: "Not used for 100 days · last used \(used)") }
        return String(localized: "Open · last used \(used)")
    }

    private func reloadStructures() {
        photoStructure = model.folderStructure(.photo)
        videoStructure = model.folderStructure(.video)
    }

    private func reloadYears() {
        reloadStructures()
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
                .environmentObject(model.library.shoots)
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
