package app.kissik.studio.android

import android.media.MediaCodec
import android.media.MediaFormat
import app.kissik.studio.platform.LiveService
import app.kissik.studio.stream.Destination
import app.kissik.studio.stream.LinkState
import app.kissik.studio.stream.StreamSettings
import app.kissik.studio.stream.VideoCodec
import com.pedro.common.ConnectChecker
import com.pedro.rtmp.rtmp.RtmpClient
import kotlinx.coroutines.flow.MutableStateFlow
import java.nio.ByteBuffer

/**
 * Live streaming: one hardware encoder (H.264 or HEVC) and one AAC encoder feed an RTMP client per destination, so
 * streaming to several places costs one encode.
 */
class AndroidLive(private val camera: AndroidCamera, private val audio: AndroidAudio) : LiveService {
    override val states = MutableStateFlow<Map<String, LinkState>>(emptyMap())
    override val supportsHevc = VideoEncoder.supports(MediaFormat.MIMETYPE_VIDEO_HEVC)

    private var venc: VideoEncoder? = null
    private var aenc: AudioEncoder? = null
    private var frames: FrameSink? = null
    private var pcm: AndroidAudio.PcmListener? = null
    private val clients = mutableMapOf<String, RtmpClient>()

    private fun set(id: String, s: LinkState) {
        states.value = states.value + (id to s)
        // Every destination refused: stop encoding, rather than run the camera and encoders for nobody.
        if (s == LinkState.Failed && clients.isNotEmpty() && clients.keys.all { states.value[it] == LinkState.Failed })
            android.os.Handler(android.os.Looper.getMainLooper()).post { if (clients.isNotEmpty() && clients.keys.all { states.value[it] == LinkState.Failed }) stop() }
    }

    override fun start(destinations: List<Destination>, settings: StreamSettings) {
        stop()
        if (destinations.isEmpty()) return
        val hevc = settings.codec == VideoCodec.HEVC && supportsHevc
        val st = camera.state.value
        val short = settings.shortSide; val long = short * 16 / 9
        val (w, h) = if (st.height > st.width) short to long else long to short
        destinations.forEach { d ->
            val c = RtmpClient(object : ConnectChecker {
                override fun onConnectionStarted(url: String) = set(d.id, LinkState.Connecting)
                override fun onConnectionSuccess() = set(d.id, LinkState.Live)
                override fun onConnectionFailed(reason: String) = set(d.id, LinkState.Failed)
                override fun onDisconnect() { if (states.value[d.id] == LinkState.Live) set(d.id, LinkState.Idle) }
                override fun onAuthError() = set(d.id, LinkState.Failed)
                override fun onAuthSuccess() {}
            })
            c.setVideoCodec(if (hevc) com.pedro.common.VideoCodec.H265 else com.pedro.common.VideoCodec.H264)
            c.setAudioInfo(AndroidAudio.SAMPLE_RATE, false)
            c.setVideoResolution(w, h); c.setFps(settings.fps)
            clients[d.id] = c
            set(d.id, LinkState.Connecting)
        }
        val sink = object : SampleSink {
            override fun onFormat(audio: Boolean, format: MediaFormat) {
                if (audio) return
                val sps = format.getByteBuffer("csd-0"); val pps = format.getByteBuffer("csd-1")
                clients.values.forEach { c ->
                    if (hevc) c.setVideoInfo(sps!!.duplicate(), null, null) else c.setVideoInfo(sps!!.duplicate(), pps?.duplicate(), null)
                }
                destinations.forEach { d -> clients[d.id]?.connect(d.publishUrl) }
            }
            override fun onSample(audio: Boolean, data: ByteBuffer, info: MediaCodec.BufferInfo) {
                clients.values.forEach { c ->
                    val copy = ByteBuffer.allocate(info.size).put(data.duplicate()).also { it.flip() }
                    val i = MediaCodec.BufferInfo().apply { set(0, info.size, info.presentationTimeUs, info.flags) }
                    if (audio) c.sendAudio(copy, i) else c.sendVideo(copy, i)
                }
            }
        }
        runCatching {
            val v = VideoEncoder(w, h, settings.bitrateKbps * 1000, settings.fps,
                if (hevc) MediaFormat.MIMETYPE_VIDEO_HEVC else MediaFormat.MIMETYPE_VIDEO_AVC, listOf(sink))
            val a = AudioEncoder(AndroidAudio.SAMPLE_RATE, listOf(sink))
            venc = v; aenc = a
            frames = FrameSink { b, _ -> v.draw(b) }.also { camera.sinks += it }
            pcm = AndroidAudio.PcmListener { p, n, t -> a.feed(p, n, t) }.also { audio.addListener(it) }
        }.onFailure { destinations.forEach { d -> set(d.id, LinkState.Failed) }; stop() }
    }

    override fun stop() {
        frames?.let { camera.sinks -= it }; frames = null
        pcm?.let { audio.removeListener(it) }; pcm = null
        venc?.stop(); venc = null; aenc?.stop(); aenc = null
        clients.values.forEach { runCatching { it.disconnect() } }; clients.clear()
        states.value = states.value.mapValues { if (it.value == LinkState.Failed) LinkState.Failed else LinkState.Idle }
    }
}
