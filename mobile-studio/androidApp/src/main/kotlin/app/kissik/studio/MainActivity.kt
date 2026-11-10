package app.kissik.studio

import android.Manifest
import android.content.pm.PackageManager
import android.os.Bundle
import androidx.activity.ComponentActivity
import androidx.activity.compose.setContent
import androidx.activity.result.contract.ActivityResultContracts
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.setValue
import app.kissik.studio.android.AndroidPlatform
import app.kissik.studio.android.CrashLog
import app.kissik.studio.android.KeepScreenOn
import app.kissik.studio.android.PermissionGate
import app.kissik.studio.ui.App

class MainActivity : ComponentActivity() {
    private lateinit var platform: AndroidPlatform
    private var granted by mutableStateOf(false)

    /** True once Android has stopped showing its permission question (refused twice). */
    private var blocked by mutableStateOf(false)
    private var couldAsk = true

    private val ask = registerForActivityResult(ActivityResultContracts.RequestMultiplePermissions()) { r ->
        granted = r[Manifest.permission.CAMERA] == true
        // Refused and no more questions allowed: either this was the second refusal, or Android answered by itself at once.
        val canAskAgain = shouldShowRequestPermissionRationale(Manifest.permission.CAMERA)
        blocked = !granted && !canAskAgain && (couldAsk || System.currentTimeMillis() - askedAt < 500)
    }
    private var askedAt = 0L

    private fun askPermissions() {
        couldAsk = shouldShowRequestPermissionRationale(Manifest.permission.CAMERA)
        askedAt = System.currentTimeMillis()
        ask.launch(arrayOf(Manifest.permission.CAMERA, Manifest.permission.RECORD_AUDIO))
    }

    // Coming back from the settings page with the camera allowed opens the studio straight away.
    override fun onResume() {
        super.onResume()
        granted = checkSelfPermission(Manifest.permission.CAMERA) == PackageManager.PERMISSION_GRANTED
        if (granted) blocked = false
    }

    override fun onStart() { super.onStart(); platform.foreground() }
    override fun onStop() { platform.background(); super.onStop() }

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        CrashLog.install(applicationContext)
        platform = AndroidPlatform(this)
        granted = checkSelfPermission(Manifest.permission.CAMERA) == PackageManager.PERMISSION_GRANTED
        if (savedInstanceState == null && (!granted || checkSelfPermission(Manifest.permission.RECORD_AUDIO) != PackageManager.PERMISSION_GRANTED))
            askPermissions()
        setContent {
            KeepScreenOn(platform, this)
            if (granted) App(platform)
            else PermissionGate(blocked, onAsk = { askPermissions() }, onSettings = { platform.system.openPrivacySettings() })
        }
    }

    override fun onDestroy() { platform.release(); super.onDestroy() }
}
