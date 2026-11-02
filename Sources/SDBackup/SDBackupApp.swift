import SwiftUI

@main
struct SDBackupApp: App {
    @StateObject private var model = AppModel()

    var body: some Scene {
        MenuBarExtra {
            MenuContentView()
                .environmentObject(model)
                .environmentObject(model.settings)
        } label: {
            if model.drafts.isEmpty {
                Image(systemName: model.isBusy ? "sdcard.fill" : "sdcard")
            } else {
                // Cards waiting for a project.
                HStack(spacing: 2) {
                    Image(systemName: "sdcard.fill")
                    Text("\(model.drafts.count)")
                }
            }
        }
        .menuBarExtraStyle(.window)
    }
}
