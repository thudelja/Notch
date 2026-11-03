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
                Image(nsImage: model.isBusy ? MenuBarIcon.filled : MenuBarIcon.normal)
                    .renderingMode(.template)
            } else {
                // Cards waiting for a project.
                HStack(spacing: 2) {
                    Image(nsImage: MenuBarIcon.filled)
                        .renderingMode(.template)
                    Text("\(model.drafts.count)")
                }
            }
        }
        .menuBarExtraStyle(.window)
    }
}
