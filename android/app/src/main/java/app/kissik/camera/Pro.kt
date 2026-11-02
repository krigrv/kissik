package app.kissik.camera

import android.hardware.camera2.CameraCharacteristics as CC
import android.hardware.camera2.CaptureRequest as CR
import android.util.Range
import androidx.annotation.OptIn
import androidx.camera.camera2.interop.Camera2CameraInfo
import androidx.camera.camera2.interop.CaptureRequestOptions
import androidx.camera.camera2.interop.ExperimentalCamera2Interop
import androidx.camera.core.CameraInfo
import kotlinx.coroutines.flow.MutableStateFlow
import org.json.JSONArray
import org.json.JSONObject

/** One camera the phone exposes. Labels come from focal length relative to the main back camera. */
data class Lens(val id: String, val facing: String, val label: String, val focal: Float)

/** What the open lens can do, read from its Camera2 characteristics. */
data class Caps(
    val evMin: Int, val evMax: Int, val evStep: Float,
    val manual: Boolean, val isoMin: Int, val isoMax: Int, val shutterMin: Long, val shutterMax: Long,
    val minFocus: Float, val wbModes: List<String>, val fpsRanges: List<Range<Int>>,
    val videoStab: Boolean, val ois: Boolean, val hdr: Boolean,
) {
    val fpsChoices get() = fpsRanges.map { it.upper }.distinct().sorted()
}

private val WB = linkedMapOf(
    "auto" to CR.CONTROL_AWB_MODE_AUTO, "daylight" to CR.CONTROL_AWB_MODE_DAYLIGHT,
    "cloudy" to CR.CONTROL_AWB_MODE_CLOUDY_DAYLIGHT, "shade" to CR.CONTROL_AWB_MODE_SHADE,
    "incandescent" to CR.CONTROL_AWB_MODE_INCANDESCENT, "fluorescent" to CR.CONTROL_AWB_MODE_FLUORESCENT,
    "warm-fluorescent" to CR.CONTROL_AWB_MODE_WARM_FLUORESCENT, "twilight" to CR.CONTROL_AWB_MODE_TWILIGHT,
)

/**
 * Pro camera controls, settable from the phone or from KISSIK on the PC. Null means "automatic".
 * Everything here goes into one Camera2 capture request (see [options]); EV goes through CameraX.
 */
object Pro {
    val lenses = MutableStateFlow<List<Lens>>(emptyList())
    /** A specific camera id, or null to follow the front/back choice. */
    val lensId = MutableStateFlow<String?>(null)
    val caps = MutableStateFlow<Caps?>(null)
    val ev = MutableStateFlow(0)
    val iso = MutableStateFlow<Int?>(null)
    val shutterNs = MutableStateFlow<Long?>(null)
    /** 0 = farthest (infinity) … 1 = nearest the lens can focus. */
    val focus = MutableStateFlow<Float?>(null)
    val wb = MutableStateFlow("auto")
    /** Target frame rate; 0 = automatic. */
    val fps = MutableStateFlow(0)
    /** Off by default: stabilization holds frames back to smooth motion, which adds delay. */
    val stab = MutableStateFlow(false)
    val hdr = MutableStateFlow(false)
    /** "jpeg" (works with every KISSIK) or "h264" (hardware encoder; only when the PC asks for it). */
    val codec = MutableStateFlow("jpeg")
    /** Formats this phone can send: "jpeg" plus what its encoder supports ("hevc-hdr" also needs a 10-bit camera). */
    val codecs = MutableStateFlow(listOf("jpeg", "h264"))
    /** Phone-maker camera effect: "none", or one of [effects] (what this phone offers for the current camera). */
    val effect = MutableStateFlow("none")
    val effects = MutableStateFlow<List<String>>(emptyList())
    val EFFECTS = listOf(
        "bokeh" to androidx.camera.extensions.ExtensionMode.BOKEH, "night" to androidx.camera.extensions.ExtensionMode.NIGHT,
        "hdr" to androidx.camera.extensions.ExtensionMode.HDR, "face" to androidx.camera.extensions.ExtensionMode.FACE_RETOUCH,
        "auto" to androidx.camera.extensions.ExtensionMode.AUTO,
    )

    /** Manual values don't carry across lenses: each has its own ranges. */
    fun resetManual() { ev.value = 0; iso.value = null; shutterNs.value = null; focus.value = null }

    @OptIn(ExperimentalCamera2Interop::class)
    fun listLenses(infos: List<CameraInfo>): List<Lens> {
        val raw = infos.mapNotNull { info ->
            runCatching {
                val c2 = Camera2CameraInfo.from(info)
                val facing = when (c2.getCameraCharacteristic(CC.LENS_FACING)) {
                    CC.LENS_FACING_FRONT -> "front"; CC.LENS_FACING_BACK -> "back"; else -> "external"
                }
                // A "logical" camera switches between physical lenses by itself as you zoom.
                val logical = CC.REQUEST_AVAILABLE_CAPABILITIES_LOGICAL_MULTI_CAMERA in (c2.getCameraCharacteristic(CC.REQUEST_AVAILABLE_CAPABILITIES) ?: IntArray(0))
                Triple(c2.cameraId, if (logical && facing == "back") "back-auto" else facing, c2.getCameraCharacteristic(CC.LENS_INFO_AVAILABLE_FOCAL_LENGTHS)?.minOrNull() ?: 0f)
            }.getOrNull()
        }
        val mainId = raw.firstOrNull { it.second == "back" }?.first
        val mainFocal = raw.firstOrNull { it.first == mainId }?.third ?: 0f
        val named = raw.map { (id, facing, focal) ->
            val label = when {
                facing == "back-auto" -> "Auto (all back lenses)"
                facing == "front" -> "Front"
                facing == "external" -> "External"
                id == mainId -> "Main"
                mainFocal > 0 && focal < mainFocal * 0.8f -> "Ultra-wide"
                mainFocal > 0 && focal > mainFocal * 1.5f -> "Telephoto"
                else -> "Back"
            }
            Lens(id, if (facing == "back-auto") "back" else facing, label, focal)
        }
        // Two lenses with the same label get numbered ("Front 2").
        return named.map { l -> if (named.count { it.label == l.label } > 1) l.copy(label = "${l.label} ${named.filter { it.label == l.label }.indexOf(l) + 1}") else l }
    }

    @OptIn(ExperimentalCamera2Interop::class)
    fun readCaps(info: CameraInfo): Caps? = runCatching {
        val c = Camera2CameraInfo.from(info)
        val ev = c.getCameraCharacteristic(CC.CONTROL_AE_COMPENSATION_RANGE) ?: Range(0, 0)
        val iso = c.getCameraCharacteristic(CC.SENSOR_INFO_SENSITIVITY_RANGE)
        val exp = c.getCameraCharacteristic(CC.SENSOR_INFO_EXPOSURE_TIME_RANGE)
        val capsList = c.getCameraCharacteristic(CC.REQUEST_AVAILABLE_CAPABILITIES) ?: IntArray(0)
        val awb = c.getCameraCharacteristic(CC.CONTROL_AWB_AVAILABLE_MODES) ?: IntArray(0)
        Caps(
            evMin = ev.lower, evMax = ev.upper, evStep = c.getCameraCharacteristic(CC.CONTROL_AE_COMPENSATION_STEP)?.toFloat() ?: 0f,
            manual = CC.REQUEST_AVAILABLE_CAPABILITIES_MANUAL_SENSOR in capsList && iso != null && exp != null,
            isoMin = iso?.lower ?: 0, isoMax = iso?.upper ?: 0, shutterMin = exp?.lower ?: 0, shutterMax = exp?.upper ?: 0,
            minFocus = c.getCameraCharacteristic(CC.LENS_INFO_MINIMUM_FOCUS_DISTANCE) ?: 0f,
            wbModes = WB.filterValues { it in awb }.keys.toList(),
            fpsRanges = c.getCameraCharacteristic(CC.CONTROL_AE_AVAILABLE_TARGET_FPS_RANGES)?.toList() ?: emptyList(),
            videoStab = CC.CONTROL_VIDEO_STABILIZATION_MODE_ON in (c.getCameraCharacteristic(CC.CONTROL_AVAILABLE_VIDEO_STABILIZATION_MODES) ?: IntArray(0)),
            ois = CC.LENS_OPTICAL_STABILIZATION_MODE_ON in (c.getCameraCharacteristic(CC.LENS_INFO_AVAILABLE_OPTICAL_STABILIZATION) ?: IntArray(0)),
            hdr = CR.CONTROL_SCENE_MODE_HDR in (c.getCameraCharacteristic(CC.CONTROL_AVAILABLE_SCENE_MODES) ?: IntArray(0)),
        )
    }.getOrNull()

    /** The Camera2 request settings for the current choices (anything left automatic is simply not set). */
    @OptIn(ExperimentalCamera2Interop::class)
    fun options(c: Caps): CaptureRequestOptions {
        val b = CaptureRequestOptions.Builder()
        // Ask the camera for its video tuning (the same processing the built-in camera uses when filming).
        b.setCaptureRequestOption(CR.CONTROL_CAPTURE_INTENT, CR.CONTROL_CAPTURE_INTENT_VIDEO_RECORD)
        // A steady frame rate: the chosen one, or a fixed 30 when automatic (so low light doesn't make it stutter).
        val target = if (fps.value > 0) fps.value else 30
        c.fpsRanges.filter { it.upper == target }.maxByOrNull { it.lower }
            ?.let { b.setCaptureRequestOption(CR.CONTROL_AE_TARGET_FPS_RANGE, it) }
        if (c.manual && (iso.value != null || shutterNs.value != null)) {
            b.setCaptureRequestOption(CR.CONTROL_AE_MODE, CR.CONTROL_AE_MODE_OFF)
            b.setCaptureRequestOption(CR.SENSOR_SENSITIVITY, (iso.value ?: 400).coerceIn(c.isoMin, c.isoMax))
            b.setCaptureRequestOption(CR.SENSOR_EXPOSURE_TIME, (shutterNs.value ?: 16_666_667L).coerceIn(c.shutterMin, c.shutterMax))
        }
        focus.value?.takeIf { c.minFocus > 0f }?.let {
            b.setCaptureRequestOption(CR.CONTROL_AF_MODE, CR.CONTROL_AF_MODE_OFF)
            b.setCaptureRequestOption(CR.LENS_FOCUS_DISTANCE, it.coerceIn(0f, 1f) * c.minFocus)
        }
        if (wb.value != "auto") WB[wb.value]?.let { b.setCaptureRequestOption(CR.CONTROL_AWB_MODE, it) }
        if (c.videoStab) b.setCaptureRequestOption(CR.CONTROL_VIDEO_STABILIZATION_MODE,
            if (stab.value) CR.CONTROL_VIDEO_STABILIZATION_MODE_ON else CR.CONTROL_VIDEO_STABILIZATION_MODE_OFF)
        if (c.ois) b.setCaptureRequestOption(CR.LENS_OPTICAL_STABILIZATION_MODE,
            if (stab.value) CR.LENS_OPTICAL_STABILIZATION_MODE_ON else CR.LENS_OPTICAL_STABILIZATION_MODE_OFF)
        if (c.hdr && hdr.value) {
            b.setCaptureRequestOption(CR.CONTROL_MODE, CR.CONTROL_MODE_USE_SCENE_MODE)
            b.setCaptureRequestOption(CR.CONTROL_SCENE_MODE, CR.CONTROL_SCENE_MODE_HDR)
        }
        return b.build()
    }

    /** Handles the pro text commands from the PC. Returns false for anything that isn't one. */
    fun command(text: String): Boolean {
        val (key, value) = text.substringBefore(':') to text.substringAfter(':', "")
        val c = caps.value
        when (key) {
            "lens" -> lenses.value.firstOrNull { it.id == value }?.let { lensId.value = it.id; Live.front.value = it.facing == "front" }
            "ev" -> value.toIntOrNull()?.let { ev.value = if (c != null) it.coerceIn(c.evMin, c.evMax) else it }
            "iso" -> iso.value = if (value == "auto") null else value.toIntOrNull()?.let { if (c != null) it.coerceIn(c.isoMin, c.isoMax) else it }
            "shutter" -> shutterNs.value = if (value == "auto") null else value.toLongOrNull()?.let { if (c != null) it.coerceIn(c.shutterMin, c.shutterMax) else it }
            "exposure" -> if (value == "auto") { iso.value = null; shutterNs.value = null }
            "focus" -> if (value == "auto") focus.value = null else value.toFloatOrNull()?.let { focus.value = it.coerceIn(0f, 1f) }
            "wb" -> if (value in WB) wb.value = value
            "fps" -> fps.value = if (value == "auto") 0 else value.toIntOrNull() ?: return true
            "stab" -> stab.value = value == "on"
            "hdr" -> hdr.value = value == "on"
            "codec" -> if (value in codecs.value) codec.value = value
            "effect" -> if (value == "none" || value in effects.value) effect.value = value
            else -> return false
        }
        return true
    }

    /** Adds the pro fields to the status JSON. Unsupported controls are null so the PC can hide them. */
    fun putStatus(json: JSONObject) {
        val n = JSONObject.NULL
        json.put("lenses", JSONArray(lenses.value.map { JSONObject().put("id", it.id).put("facing", it.facing).put("label", it.label) }))
        json.put("lens", lensId.value ?: n)
        json.put("codecs", JSONArray(codecs.value)).put("codec", codec.value)
        json.put("effects", JSONArray(effects.value)).put("effect", effect.value)
        val c = caps.value ?: return
        json.put("evMin", c.evMin).put("evMax", c.evMax).put("evStep", c.evStep.toDouble()).put("ev", ev.value)
        json.put("manual", c.manual)
        if (c.manual) json.put("isoMin", c.isoMin).put("isoMax", c.isoMax).put("shutterMin", c.shutterMin).put("shutterMax", c.shutterMax)
        json.put("iso", iso.value ?: n).put("shutter", shutterNs.value ?: n)
        json.put("focusManual", c.minFocus > 0f).put("focus", focus.value?.toDouble() ?: n)
        json.put("wbModes", JSONArray(c.wbModes)).put("wb", wb.value)
        json.put("fpsList", JSONArray(c.fpsChoices)).put("fps", fps.value)
        json.put("stab", if (c.videoStab || c.ois) stab.value else n)
        json.put("hdr", if (c.hdr) hdr.value else n)
    }
}
