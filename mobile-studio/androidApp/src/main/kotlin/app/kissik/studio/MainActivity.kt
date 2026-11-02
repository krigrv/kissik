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
import app.kissik.studio.android.PermissionGate
import app.kissik.studio.ui.App

class MainActivity : ComponentActivity() {
    private lateinit var platform: AndroidPlatform
    private var granted by mutableStateOf(false)

    private val ask = registerForActivityResult(ActivityResultContracts.RequestMultiplePermissions()) { r ->
        granted = r[Manifest.permission.CAMERA] == true
    }

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        CrashLog.install(applicationContext)
        platform = AndroidPlatform(this)
        granted = checkSelfPermission(Manifest.permission.CAMERA) == PackageManager.PERMISSION_GRANTED
        if (!granted || checkSelfPermission(Manifest.permission.RECORD_AUDIO) != PackageManager.PERMISSION_GRANTED)
            ask.launch(arrayOf(Manifest.permission.CAMERA, Manifest.permission.RECORD_AUDIO))
        setContent {
            if (granted) App(platform)
            else PermissionGate(onAsk = { ask.launch(arrayOf(Manifest.permission.CAMERA, Manifest.permission.RECORD_AUDIO)) },
                onSettings = { platform.system.openPrivacySettings() })
        }
    }

    override fun onDestroy() { platform.release(); super.onDestroy() }
}
