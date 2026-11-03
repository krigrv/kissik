import SwiftUI
import UIKit
import Shared

/// Hosts the shared Compose UI (shared/src/iosMain/.../MainViewController.kt).
struct ComposeView: UIViewControllerRepresentable {
    func makeUIViewController(context: Context) -> UIViewController { MainViewControllerKt.MainViewController() }
    func updateUIViewController(_ uiViewController: UIViewController, context: Context) {}
}

struct ContentView: View {
    var body: some View { ComposeView().ignoresSafeArea(.all) }
}
