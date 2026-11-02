package app.kissik.studio.camera

data class Lens(val id: String, val front: Boolean, val label: String)

/**
 * What the current camera can do and how it is set. Field names follow the phone protocol's status JSON; a control
 * the lens lacks is null / false so the UI (and the PC) hide it.
 */
data class CameraState(
    val ready: Boolean = false,
    val error: String? = null,
    val front: Boolean = false,
    val lenses: List<Lens> = emptyList(),
    val lensId: String? = null,
    val width: Int = 0, val height: Int = 0,
    val maxWidth: Int = 0, val maxHeight: Int = 0, val maxFps: Int = 30,
    val hasTorch: Boolean = false, val torch: Boolean = false,
    val zoom: Float = 1f, val zoomMin: Float = 1f, val zoomMax: Float = 1f,
    val ev: Int = 0, val evMin: Int = 0, val evMax: Int = 0, val evStep: Float = 0f,
    val manual: Boolean = false,
    val isoMin: Int = 0, val isoMax: Int = 0, val shutterMin: Long = 0, val shutterMax: Long = 0,
    val iso: Int? = null, val shutter: Long? = null,
    val focusManual: Boolean = false, val focus: Float? = null,
    val wbModes: List<String> = listOf("auto"), val wb: String = "auto",
    val fpsList: List<Int> = emptyList(), val fps: Int = 0,
    val stab: Boolean? = null, val hdr: Boolean? = null,
)

/** Shared helpers for pro controls. */
object ProControls {
    /** Shutter speeds offered in the UI, in nanoseconds, filtered to the lens's range. */
    val shutterStops: List<Long> = listOf(1L, 2, 4, 8, 15, 30, 60, 125, 250, 500, 1000, 2000, 4000, 8000).map { 1_000_000_000L / it }

    fun shutterLabel(ns: Long): String = if (ns >= 1_000_000_000L) "${ns / 1_000_000_000L}s" else "1/${(1_000_000_000L / ns)}"

    /** ISO steps (one-third stops are overkill on a phone; full stops keep the slider readable). */
    fun isoStops(min: Int, max: Int): List<Int> = generateSequence(50) { it * 2 }.takeWhile { it <= 12800 }
        .filter { it in min..max }.toList().ifEmpty { listOf(min, max).distinct() }

    /** Maps an EV slider position to the camera's exposure index. */
    fun evIndex(ev: Float, step: Float, min: Int, max: Int): Int =
        if (step <= 0f) 0 else kotlin.math.round(ev / step).toInt().coerceIn(min, max)

    fun wbLabel(mode: String) = when (mode) {
        "auto" -> "Auto"; "daylight" -> "Daylight"; "cloudy" -> "Cloudy"; "shade" -> "Shade"; "tungsten", "incandescent" -> "Tungsten"
        "fluorescent" -> "Fluorescent"; "warm-fluorescent" -> "Warm fluorescent"; "twilight" -> "Twilight"; "locked" -> "Locked"
        else -> mode.replaceFirstChar { it.uppercase() }
    }
}
