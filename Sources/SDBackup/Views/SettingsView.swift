import AppKit
import SwiftUI

struct SettingsView: View {
    @EnvironmentObject private var model: AppModel
    @EnvironmentObject private var settings: AppSettings

    var body: some View {
        Form {
            Section("Projects folder") {
                HStack {
                    Image(systemName: "folder.fill")
                        .foregroundStyle(.secondary)
                    Text(settings.abbreviatedDestination)
                        .lineLimit(1)
                        .truncationMode(.middle)
                    Spacer()
                    Button("Change…") { model.chooseDestination() }
                }
                Text("Photos: 2026 › Photo › 11 November › 02.11.2026 Name › Camera\nVideos: 2026 › Video › 11 November › 02.11.2026 Name › Media › Camera, plus Project, Assets and Export folders.")
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

            Section("General") {
                Toggle("Open at login", isOn: $model.launchAtLogin)
                Toggle("Show notifications", isOn: $settings.notificationsEnabled)
                HStack {
                    Button("Open Log") { model.openLog() }
                    Button("Clear Recent") { model.clearHistory() }
                        .disabled(model.history.isEmpty)
                }
            }

            Section("Memory") {
                HStack {
                    Button("Forget Imported Files…") { model.forgetImportedFiles() }
                    Button("Forget Camera Names") { model.forgetCameraNames() }
                }
                Text("SD Backup remembers which files of each card were imported, so a card you insert again only offers new files. It also remembers the folder name you gave each camera.")
                    .font(.caption)
                    .foregroundStyle(.secondary)
                    .fixedSize(horizontal: false, vertical: true)
            }
        }
        .formStyle(.grouped)
        .frame(width: 520, height: 680)
        .onChange(of: settings.detection) { _ in model.refreshConnectedVolumes() }
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
            window.title = "SD Backup Settings"
            window.styleMask = [.titled, .closable]
            window.isReleasedWhenClosed = false
            window.center()
            self.window = window
        }
        AppActivation.bringToFront()
        window?.makeKeyAndOrderFront(nil)
    }
}
