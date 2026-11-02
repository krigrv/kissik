package app.kissik.camera

import android.content.Context
import kotlinx.coroutines.flow.MutableSharedFlow
import kotlinx.coroutines.flow.MutableStateFlow

object Prefs {
    fun of(ctx: Context) = ctx.getSharedPreferences("kissik", Context.MODE_PRIVATE)
}

/** Rejected: the PC refused this pairing (new code, or its certificate changed). Waits for a new scan. */
enum class Link { Off, Connecting, Wifi, Usb, Rejected }

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
    /** Whether the current lens has a flash LED to use as a torch (null until the camera opens). */
    val hasTorch = MutableStateFlow<Boolean?>(null)
    /** Zoom the lens allows (min below 1 means it can widen, e.g. 0.5 on phones with an ultra-wide). */
    val zoomRange = MutableStateFlow(1f to 1f)
    /** A tap on the preview, as 0..1 coordinates in the picture: focus and meter there. */
    val focusTap = MutableSharedFlow<Pair<Float, Float>>(extraBufferCapacity = 1)
    /** Zoom factor the PC asked for (1 = no zoom); clamped to what the lens offers. */
    val zoom = MutableStateFlow(1f)
    val saver = MutableStateFlow(true)
    /** True while the saver has dropped quality to 480p (hot phone or low battery). */
    val saving = MutableStateFlow(false)
    /** Where the phone's own screen shows the camera (a hardware preview), while the app is open. */
    val previewSurface = MutableStateFlow<androidx.camera.core.Preview.SurfaceProvider?>(null)
    /** A tap on the phone's preview, already turned into a camera metering point. */
    val meteringTap = MutableSharedFlow<androidx.camera.core.MeteringPoint>(extraBufferCapacity = 1)
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
