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
            Image(systemName: model.isBusy ? "sdcard.fill" : "sdcard")
        }
        .menuBarExtraStyle(.window)
    }
}
