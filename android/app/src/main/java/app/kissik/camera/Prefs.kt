package app.kissik.camera

import android.content.Context
import kotlinx.coroutines.flow.MutableStateFlow

object Prefs {
    fun of(ctx: Context) = ctx.getSharedPreferences("kissik", Context.MODE_PRIVATE)
}

enum class Link { Off, Connecting, Wifi, Usb }

/** Shared between the screen and the streaming service. Choices persist, so a reconnect keeps lens and mic. */
object Live {
    val link = MutableStateFlow(Link.Off)
    val message = MutableStateFlow("")
    val front = MutableStateFlow(false)
    val mic = MutableStateFlow(false)
    val torch = MutableStateFlow(false)
    val quality = MutableStateFlow(720)
    /** Largest capture size and frame rate the current lens offers (0 until the camera opens). */
    val maxSize = MutableStateFlow(0 to 0)
    val maxFps = MutableStateFlow(0)
    /** Zoom factor the PC asked for (1 = no zoom); clamped to what the lens offers. */
    val zoom = MutableStateFlow(1f)
    val saver = MutableStateFlow(true)
    /** True while the saver has dropped quality to 480p (hot phone or low battery). */
    val saving = MutableStateFlow(false)
    val preview = MutableStateFlow<android.graphics.Bitmap?>(null)
    val frameSize = MutableStateFlow(0 to 0)
    val running = MutableStateFlow(false)

    fun load(ctx: Context) {
        val p = Prefs.of(ctx)
        front.value = p.getBoolean("front", false); mic.value = p.getBoolean("mic", false)
        quality.value = p.getInt("quality", 720); saver.value = p.getBoolean("saver", true)
    }

    fun save(ctx: Context) = Prefs.of(ctx).edit().putBoolean("front", front.value).putBoolean("mic", mic.value)
        .putInt("quality", quality.value).putBoolean("saver", saver.value).apply()
}
