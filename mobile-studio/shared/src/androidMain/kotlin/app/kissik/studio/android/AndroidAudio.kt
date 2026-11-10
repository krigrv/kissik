package app.kissik.studio.android

import android.Manifest
import android.annotation.SuppressLint
import android.content.Context
import android.content.pm.PackageManager
import android.media.AudioDeviceInfo
import android.media.AudioFormat
import android.media.AudioManager
import android.media.AudioRecord
import android.media.MediaRecorder
import android.media.audiofx.NoiseSuppressor
import app.kissik.studio.platform.AudioService
import app.kissik.studio.platform.Mic
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import java.util.concurrent.CopyOnWriteArrayList
import kotlin.math.abs

/**
 * The microphone: one AudioRecord shared by the level meter, recordings, the replay buffer, live streams and the PC
 * link. Listeners get 16-bit mono PCM at [SAMPLE_RATE] with a wall-clock timestamp; muting sends silence.
 */
class AndroidAudio(private val ctx: Context) : AudioService {
    companion object { const val SAMPLE_RATE = 48_000 }

    fun interface PcmListener { fun onPcm(pcm: ShortArray, count: Int, timeUs: Long) }

    private val am = ctx.getSystemService(AudioManager::class.java)
    private val listeners = CopyOnWriteArrayList<PcmListener>()
    private var meterUsers = 0
    private var record: AudioRecord? = null
    private var ns: NoiseSuppressor? = null
    @Volatile private var thread: Thread? = null

    override val mics = MutableStateFlow(listMics())
    override val selectedMic = MutableStateFlow<String?>(null)
    override val level = MutableStateFlow(0f)
    override val muted = MutableStateFlow(false)
    override val noiseSuppression = MutableStateFlow(NoiseSuppressor.isAvailable())
    override val noiseSuppressionAvailable: Boolean = NoiseSuppressor.isAvailable()

    private fun listMics(): List<Mic> = am.getDevices(AudioManager.GET_DEVICES_INPUTS).map { d ->
        val kind = when (d.type) {
            AudioDeviceInfo.TYPE_BUILTIN_MIC -> "Phone mic" + (d.address.takeIf { it.isNotBlank() }?.let { " · ${it.replaceFirstChar(Char::uppercase)}" } ?: "")
            AudioDeviceInfo.TYPE_WIRED_HEADSET -> "Wired headset"
            AudioDeviceInfo.TYPE_USB_DEVICE, AudioDeviceInfo.TYPE_USB_HEADSET -> "USB · ${d.productName}"
            AudioDeviceInfo.TYPE_BLUETOOTH_SCO -> "Bluetooth · ${d.productName}"
            else -> d.productName.toString()
        }
        Mic(d.id.toString(), kind)
    }.distinctBy { it.label }

    override fun selectMic(id: String?) {
        selectedMic.value = id
        record?.preferredDevice = id?.let { s -> am.getDevices(AudioManager.GET_DEVICES_INPUTS).firstOrNull { it.id.toString() == s } }
    }
    override fun setMuted(muted: Boolean) { this.muted.value = muted }
    override fun setNoiseSuppression(on: Boolean) { noiseSuppression.value = on; ns?.enabled = on }
    override fun startMeter() { meterUsers++; ensure() }
    override fun stopMeter() { meterUsers = (meterUsers - 1).coerceAtLeast(0); maybeStop() }

    fun addListener(l: PcmListener) { listeners += l; ensure() }
    fun removeListener(l: PcmListener) { listeners -= l; maybeStop() }

    @SuppressLint("MissingPermission")
    @Synchronized private fun ensure() {
        if (thread != null) return
        if (ctx.checkSelfPermission(Manifest.permission.RECORD_AUDIO) != PackageManager.PERMISSION_GRANTED) return
        mics.value = listMics()
        val min = AudioRecord.getMinBufferSize(SAMPLE_RATE, AudioFormat.CHANNEL_IN_MONO, AudioFormat.ENCODING_PCM_16BIT)
        val r = runCatching { AudioRecord(MediaRecorder.AudioSource.CAMCORDER, SAMPLE_RATE, AudioFormat.CHANNEL_IN_MONO, AudioFormat.ENCODING_PCM_16BIT, maxOf(min, 9600) * 2) }
            .getOrNull()?.takeIf { it.state == AudioRecord.STATE_INITIALIZED } ?: return
        record = r
        selectMic(selectedMic.value)
        if (NoiseSuppressor.isAvailable()) ns = NoiseSuppressor.create(r.audioSessionId)?.apply { enabled = noiseSuppression.value }
        r.startRecording()
        thread = Thread({
            val buf = ShortArray(960) // 20 ms
            var smooth = 0f
            while (thread === Thread.currentThread()) {
                val n = r.read(buf, 0, buf.size)
                // A microphone that has gone away answers with an error at once: wait, don't spin a whole core.
                if (n <= 0) { if (n < 0) Thread.sleep(200); continue }
                val t = System.nanoTime() / 1000 - n * 1_000_000L / SAMPLE_RATE
                var peak = 0
                for (i in 0 until n) peak = maxOf(peak, abs(buf[i].toInt()))
                smooth = maxOf(peak / 32768f, smooth * 0.85f)
                level.value = smooth
                if (muted.value) java.util.Arrays.fill(buf, 0, n, 0)
                listeners.forEach { it.onPcm(buf, n, t) }
            }
        }, "kissik-mic").apply { start() }
    }

    @Synchronized private fun maybeStop() {
        if (meterUsers > 0 || listeners.isNotEmpty()) return
        val t = thread ?: return
        thread = null
        t.join(300)
        runCatching { record?.stop() }; record?.release(); record = null
        ns?.release(); ns = null
        level.value = 0f
    }

    fun release() { listeners.clear(); meterUsers = 0; maybeStop() }
}
