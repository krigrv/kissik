import SwiftUI
import Shared

@main
struct iOSApp: App {
    init() {
        IosSystem.companion.installCrashLog()
        KissikBridges.install()
    }
    var body: some Scene {
        WindowGroup { ContentView().ignoresSafeArea(.keyboard) }
    }
}
