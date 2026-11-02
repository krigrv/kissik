package app.kissik.studio.ios

import androidx.compose.ui.window.ComposeUIViewController
import app.kissik.studio.ui.App
import platform.UIKit.UIViewController

/** Called from Swift (`MainViewControllerKt.MainViewController()`); hosts the shared Compose UI. */
fun MainViewController(): UIViewController {
    val platform = IosPlatform()
    return ComposeUIViewController { App(platform) }.also { platform.host = it }
}
