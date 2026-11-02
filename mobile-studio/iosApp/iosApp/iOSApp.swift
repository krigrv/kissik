import SwiftUI
import Shared

@main
struct iOSApp: App {
    init() { IosSystem.companion.installCrashLog() }
    var body: some Scene {
        WindowGroup { ContentView().ignoresSafeArea(.keyboard) }
    }
}
