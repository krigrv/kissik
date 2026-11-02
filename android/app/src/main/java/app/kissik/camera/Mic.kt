package app.kissik.camera

import android.annotation.SuppressLint
import android.content.Context
import android.media.AudioDeviceInfo
import android.media.AudioFormat
import android.media.AudioManager
import android.media.AudioRecord
import android.media.MediaRecorder
import android.media.audiofx.AcousticEchoCanceler
import android.media.audiofx.NoiseSuppressor
import android.os.Build
import kotlinx.coroutines.flow.MutableStateFlow
import org.json.JSONArray
import org.json.JSONObject

/** One microphone the phone offers: built-in (bottom, top, back…), wired, USB or Bluetooth. */
data class MicDevice(val id: Int, val label: String, val bluetooth: Boolean)

/**
 * Microphone choices, settable from the phone or the PC. The mic is still only opened while it's switched on.
 *  - device: a specific mic, or null for the phone's default
 *  - mode:   "standard" (plain mic), "voice" (the phone's call processing: echo and noise cancelling),
 *            "camera" (the mic tuned for video, usually facing the same way as the camera), "raw" (no processing)
 *  - ns:     extra noise suppression on top of the mode
 */
object Mic {
    val devices = MutableStateFlow<List<MicDevice>>(emptyList())
    val deviceId = MutableStateFlow<Int?>(null)
    val mode = MutableStateFlow("standard")
    val ns = MutableStateFlow(false)
    val modes = listOf("standard", "voice", "camera", "raw")

    fun refresh(ctx: Context) {
        val am = ctx.getSystemService(AudioManager::class.java)
        val inputs = am.getDevices(AudioManager.GET_DEVICES_INPUTS).filter {
            it.type in setOf(AudioDeviceInfo.TYPE_BUILTIN_MIC, AudioDeviceInfo.TYPE_WIRED_HEADSET, AudioDeviceInfo.TYPE_USB_DEVICE,
                AudioDeviceInfo.TYPE_USB_HEADSET, AudioDeviceInfo.TYPE_BLUETOOTH_SCO)
        }
        var builtin = 0
        devices.value = inputs.map { d ->
            val label = when (d.type) {
                AudioDeviceInfo.TYPE_BUILTIN_MIC -> {
                    builtin++
                    // Phones name their mics by address ("bottom", "back"…) when they say anything at all.
                    val where = d.address.takeIf { it.isNotBlank() }?.replaceFirstChar(Char::uppercase)
                    "Phone mic" + (where?.let { " · $it" } ?: if (inputs.count { it.type == AudioDeviceInfo.TYPE_BUILTIN_MIC } > 1) " $builtin" else "")
                }
                AudioDeviceInfo.TYPE_WIRED_HEADSET -> "Wired headset"
                AudioDeviceInfo.TYPE_BLUETOOTH_SCO -> "Bluetooth · ${d.productName}"
                else -> "USB · ${d.productName}"
            }
            MicDevice(d.id, label, d.type == AudioDeviceInfo.TYPE_BLUETOOTH_SCO)
        }
        if (deviceId.value != null && devices.value.none { it.id == deviceId.value }) deviceId.value = null
    }

    val nsAvailable get() = NoiseSuppressor.isAvailable()

    /** Opens the recorder for the current choices. Bluetooth mics go through the phone's call path. */
    @SuppressLint("MissingPermission")
    fun open(ctx: Context, rate: Int): AudioRecord? {
        val am = ctx.getSystemService(AudioManager::class.java)
        val device = am.getDevices(AudioManager.GET_DEVICES_INPUTS).firstOrNull { it.id == deviceId.value }
        val bt = device?.type == AudioDeviceInfo.TYPE_BLUETOOTH_SCO
        val source = when {
            bt || mode.value == "voice" -> MediaRecorder.AudioSource.VOICE_COMMUNICATION
            mode.value == "camera" -> MediaRecorder.AudioSource.CAMCORDER
            mode.value == "raw" -> MediaRecorder.AudioSource.UNPROCESSED
            else -> MediaRecorder.AudioSource.MIC
        }
        if (bt && Build.VERSION.SDK_INT >= 31) runCatching { am.setCommunicationDevice(device!!) }
        val min = AudioRecord.getMinBufferSize(rate, AudioFormat.CHANNEL_IN_MONO, AudioFormat.ENCODING_PCM_FLOAT)
        val rec = runCatching { AudioRecord(source, rate, AudioFormat.CHANNEL_IN_MONO, AudioFormat.ENCODING_PCM_FLOAT, maxOf(min, 4 * 4800)) }
            .recoverCatching { AudioRecord(MediaRecorder.AudioSource.MIC, rate, AudioFormat.CHANNEL_IN_MONO, AudioFormat.ENCODING_PCM_FLOAT, maxOf(min, 4 * 4800)) }
            .getOrNull() ?: return null
        if (rec.state != AudioRecord.STATE_INITIALIZED) { rec.release(); return null }
        if (device != null) rec.preferredDevice = device
        if (ns.value && NoiseSuppressor.isAvailable()) runCatching { NoiseSuppressor.create(rec.audioSessionId)?.enabled = true }
        if (mode.value == "voice" && AcousticEchoCanceler.isAvailable()) runCatching { AcousticEchoCanceler.create(rec.audioSessionId)?.enabled = true }
        return rec
    }

    /** Undo anything [open] changed for the whole phone (the Bluetooth call route). */
    fun close(ctx: Context) {
        if (Build.VERSION.SDK_INT >= 31) runCatching { ctx.getSystemService(AudioManager::class.java).clearCommunicationDevice() }
    }

    /** PC commands: micdev:<id|auto>, micmode:<standard|voice|camera|raw>, ns:on|off. */
    fun command(text: String): Boolean {
        val (key, value) = text.substringBefore(':') to text.substringAfter(':', "")
        when (key) {
            "micdev" -> deviceId.value = if (value == "auto") null else value.toIntOrNull()?.takeIf { id -> devices.value.any { it.id == id } }
            "micmode" -> if (value in modes) mode.value = value
            "ns" -> ns.value = value == "on"
            else -> return false
        }
        return true
    }

    fun putStatus(json: JSONObject) {
        json.put("mics", JSONArray(devices.value.map { JSONObject().put("id", it.id).put("label", it.label) }))
        json.put("micDev", deviceId.value ?: JSONObject.NULL)
        json.put("micModes", JSONArray(modes)).put("micMode", mode.value)
        json.put("ns", if (nsAvailable) ns.value else JSONObject.NULL)
    }
}
