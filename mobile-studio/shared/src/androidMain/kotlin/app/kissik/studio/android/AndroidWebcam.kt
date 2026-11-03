package app.kissik.studio.android

import android.content.Context
import android.content.Intent
import android.content.IntentFilter
import android.graphics.Bitmap
import android.media.MediaCodec
import android.media.MediaFormat
import android.os.BatteryManager
import android.os.Build
import app.kissik.studio.platform.KeyValueStore
import app.kissik.studio.platform.WebcamService
import app.kissik.studio.platform.WebcamState
import app.kissik.studio.webcam.LinkStatus
import app.kissik.studio.webcam.Pairing
import app.kissik.studio.webcam.PcCommand
import app.kissik.studio.webcam.PhoneProtocol
import kotlinx.coroutines.flow.MutableStateFlow
import okhttp3.OkHttpClient
import okhttp3.Request
import okhttp3.Response
import okhttp3.WebSocket
import okhttp3.WebSocketListener
import okio.ByteString
import okio.ByteString.Companion.toByteString
import java.io.ByteArrayOutputStream
import java.nio.ByteBuffer
import java.security.MessageDigest
import java.security.SecureRandom
import java.security.cert.CertificateException
import java.security.cert.X509Certificate
import java.util.Timer
import java.util.concurrent.TimeUnit
import javax.net.ssl.SSLContext
import javax.net.ssl.X509TrustManager
import kotlin.concurrent.timerTask

/**
 * Use as a PC webcam: connects to KISSIK on the PC with the phone protocol (WebSocket; JPEG or H.264 'V' frames,
 * 'A' audio, status JSON) and obeys its commands. Wi-Fi uses TLS pinned to the PC's certificate on first use.
 */
class AndroidWebcam(private val ctx: Context, private val camera: AndroidCamera, private val audio: AndroidAudio,
                    private val store: KeyValueStore) : WebcamService {
    override val state = MutableStateFlow(WebcamState.Off)
    override val detail = MutableStateFlow("")

    private var ws: WebSocket? = null
    private var frames: FrameSink? = null
    private var pcm: AndroidAudio.PcmListener? = null
    private var timer: Timer? = null
    @Volatile private var codec = "jpeg"
    private var h264: VideoEncoder? = null
    private var micOn = true
    private var lastSent = 0L

    override fun connect(pairing: Pairing, useUsb: Boolean) {
        disconnect()
        state.value = WebcamState.Connecting; detail.value = ""
        val pinKey = "pin:${pairing.host}"
        val trust = object : X509TrustManager {
            override fun checkClientTrusted(c: Array<out X509Certificate>, a: String) = throw CertificateException()
            override fun checkServerTrusted(chain: Array<out X509Certificate>, a: String) {
                val fp = MessageDigest.getInstance("SHA-256").digest(chain[0].encoded).joinToString("") { "%02x".format(it) }
                val pinned = store.get(pinKey)
                if (pinned == null) store.put(pinKey, fp) else if (pinned != fp) throw CertificateException("PC certificate changed. Scan the code again.")
            }
            override fun getAcceptedIssuers() = arrayOf<X509Certificate>()
        }
        val ssl = SSLContext.getInstance("TLS").apply { init(null, arrayOf(trust), SecureRandom()) }
        val client = OkHttpClient.Builder().sslSocketFactory(ssl.socketFactory, trust).hostnameVerifier { _, _ -> true }
            .pingInterval(10, TimeUnit.SECONDS).build()
        val url = if (useUsb) pairing.usbUrl else pairing.wifiUrl
        ws = client.newWebSocket(Request.Builder().url(url).build(), object : WebSocketListener() {
            override fun onOpen(webSocket: WebSocket, response: Response) {
                state.value = WebcamState.Connected; detail.value = if (useUsb) "Over USB" else "Over Wi-Fi to ${pairing.host}"
                sendStatus()
                frames = FrameSink { b, t -> sendFrame(b, t) }.also { camera.sinks += it }
                pcm = AndroidAudio.PcmListener { p, n, _ -> if (micOn) sendAudio(p, n) }.also { audio.addListener(it) }
                timer = Timer().apply { scheduleAtFixedRate(timerTask { sendStatus() }, 15_000, 15_000) }
            }
            override fun onMessage(webSocket: WebSocket, text: String) = handle(PcCommand.parse(text))
            override fun onFailure(webSocket: WebSocket, t: Throwable, response: Response?) {
                cleanup(); state.value = WebcamState.Failed; detail.value = t.message ?: "Connection failed"
            }
            override fun onClosed(webSocket: WebSocket, code: Int, reason: String) { cleanup(); state.value = WebcamState.Off }
        })
    }

    private fun sendFrame(b: Bitmap, timeNs: Long) {
        val now = System.currentTimeMillis()
        if (now - lastSent < 33) return // ≤30 fps
        lastSent = now
        if (codec == "h264") {
            val enc = h264 ?: VideoEncoder(b.width and 1.inv(), b.height and 1.inv(), 4_000_000, 30, sinks = listOf(H264Sink())).also { h264 = it }
            enc.draw(b)
        } else {
            val out = ByteArrayOutputStream(64 * 1024)
            b.compress(Bitmap.CompressFormat.JPEG, 80, out)
            ws?.send(out.toByteArray().toByteString())
        }
    }

    /** Packs encoder output as 'V' packets; keyframes carry SPS+PPS in front so decoding can start there. */
    private inner class H264Sink : SampleSink {
        private var config = ByteArray(0)
        override fun onFormat(audio: Boolean, format: MediaFormat) {
            fun bytes(k: String) = format.getByteBuffer(k)?.let { val a = ByteArray(it.remaining()); it.duplicate().get(a); a } ?: ByteArray(0)
            config = bytes("csd-0") + bytes("csd-1")
        }
        override fun onSample(audio: Boolean, data: ByteBuffer, info: MediaCodec.BufferInfo) {
            val key = info.flags and MediaCodec.BUFFER_FLAG_KEY_FRAME != 0
            val body = ByteArray(info.size).also { data.get(it) }
            ws?.send(PhoneProtocol.videoPacket(if (key) config + body else body, key, info.presentationTimeUs).toByteString())
        }
    }

    private fun sendAudio(p: ShortArray, n: Int) {
        val f = FloatArray(n) { p[it] / 32768f }
        ws?.send(PhoneProtocol.audioPacket(f, AndroidAudio.SAMPLE_RATE).toByteString())
    }

    private fun sendStatus() {
        val bm = ctx.registerReceiver(null, IntentFilter(Intent.ACTION_BATTERY_CHANGED))
        val level = bm?.let { it.getIntExtra(BatteryManager.EXTRA_LEVEL, 0) / it.getIntExtra(BatteryManager.EXTRA_SCALE, 100).toFloat() } ?: 1f
        val charging = bm?.getIntExtra(BatteryManager.EXTRA_PLUGGED, 0)?.let { it != 0 } ?: false
        val link = LinkStatus(device = Build.MODEL, battery = level, charging = charging, codec = codec, mic = micOn,
            mics = audio.mics.value.map { it.id to it.label }, micDev = audio.selectedMic.value,
            ns = if (audio.noiseSuppressionAvailable) audio.noiseSuppression.value else null)
        ws?.send(PhoneProtocol.status(camera.state.value, link))
    }

    private fun handle(c: PcCommand) {
        val cam = camera
        android.os.Handler(android.os.Looper.getMainLooper()).post {
            when (c) {
                is PcCommand.Facing -> if (cam.state.value.front != c.front) cam.switchFacing()
                is PcCommand.Lens -> cam.selectLens(c.id)
                is PcCommand.Quality -> cam.setQuality(c.shortSide)
                is PcCommand.Torch -> cam.setTorch(c.on)
                is PcCommand.Zoom -> cam.setZoom(c.factor)
                is PcCommand.Ev -> cam.setEv(c.index)
                is PcCommand.Iso -> cam.setIso(c.value)
                is PcCommand.Shutter -> cam.setShutter(c.ns)
                PcCommand.ExposureAuto -> { cam.setIso(null); cam.setShutter(null) }
                is PcCommand.Focus -> cam.setFocus(c.value)
                is PcCommand.FocusTap -> cam.tapFocus(c.x, c.y)
                is PcCommand.WhiteBalance -> cam.setWhiteBalance(c.mode)
                is PcCommand.Fps -> cam.setFps(c.fps)
                is PcCommand.Stabilization -> cam.setStabilization(c.on)
                is PcCommand.Hdr -> cam.setHdr(c.on)
                is PcCommand.Codec -> { codec = c.name; if (c.name == "jpeg") { h264?.stop(); h264 = null } }
                PcCommand.KeyFrame -> h264?.requestKey = true
                is PcCommand.Mic -> micOn = c.on
                is PcCommand.MicDevice -> audio.selectMic(c.id)
                is PcCommand.NoiseSuppression -> audio.setNoiseSuppression(c.on)
                is PcCommand.MicMode, is PcCommand.Effect, is PcCommand.Unknown -> {}
            }
            android.os.Handler(android.os.Looper.getMainLooper()).postDelayed({ sendStatus() }, 300)
        }
    }

    private fun cleanup() {
        frames?.let { camera.sinks -= it }; frames = null
        pcm?.let { audio.removeListener(it) }; pcm = null
        timer?.cancel(); timer = null
        h264?.stop(); h264 = null
    }

    override fun disconnect() { cleanup(); ws?.close(1000, "bye"); ws = null; state.value = WebcamState.Off; detail.value = "" }
}
