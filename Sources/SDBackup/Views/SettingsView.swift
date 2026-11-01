import AppKit
import SwiftUI

struct SettingsView: View {
    @EnvironmentObject private var model: AppModel
    @EnvironmentObject private var settings: AppSettings

    var body: some View {
        Form {
            Section("Backup folder") {
                HStack {
                    Image(systemName: "folder.fill")
                        .foregroundStyle(.secondary)
                    Text(settings.abbreviatedDestination)
                        .lineLimit(1)
                        .truncationMode(.middle)
                    Spacer()
                    Button("Change…") { model.chooseDestination() }
                }
                Text("Each card is copied into its own folder here, named after the card. Re-inserting a card only copies files that are new.")
                    .font(.caption)
                    .foregroundStyle(.secondary)
            }

            Section("Cards") {
                Toggle("Back up automatically when a card is inserted", isOn: $settings.autoBackupEnabled)
                Picker("Back up", selection: $settings.detection) {
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
                Toggle("Eject the card when the backup finishes", isOn: $settings.ejectWhenDone)
            }

            Section("General") {
                Toggle("Open at login", isOn: $model.launchAtLogin)
                Toggle("Show notifications", isOn: $settings.notificationsEnabled)
                HStack {
                    Button("Open Backup Log") { model.openLog() }
                    Button("Clear Recent Backups") { model.clearHistory() }
                        .disabled(model.history.isEmpty)
                }
            }
        }
        .formStyle(.grouped)
        .frame(width: 500, height: 600)
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
