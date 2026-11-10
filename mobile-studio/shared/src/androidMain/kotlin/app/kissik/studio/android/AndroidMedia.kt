package app.kissik.studio.android

import android.Manifest
import android.annotation.SuppressLint
import android.content.ContentValues
import android.content.Context
import android.content.pm.PackageManager
import android.graphics.Bitmap
import android.net.Uri
import android.os.Build
import android.os.Environment
import android.provider.MediaStore
import android.view.WindowManager
import androidx.activity.ComponentActivity
import androidx.camera.video.MediaStoreOutputOptions
import androidx.camera.video.Quality as CxQuality
import androidx.camera.video.QualitySelector
import androidx.camera.video.FallbackStrategy
import androidx.camera.video.Recorder
import androidx.camera.video.Recording
import androidx.camera.video.VideoCapture
import androidx.camera.video.VideoRecordEvent
import app.kissik.studio.platform.MediaService
import app.kissik.studio.platform.MediaState
import app.kissik.studio.platform.RecordOptions
import app.kissik.studio.recording.Quality
import app.kissik.studio.recording.RecordingClock
import app.kissik.studio.recording.TimeLapse
import kotlinx.coroutines.flow.MutableSharedFlow
import kotlinx.coroutines.flow.MutableStateFlow
import java.io.File
import java.text.SimpleDateFormat
import java.util.Date
import java.util.Locale
import java.util.Timer
import kotlin.concurrent.timerTask

/**
 * Recording, replay buffer, snapshots and time-lapse. What you see is what's recorded: processed frames (effects,
 * framing, scene) go through the hardware encoder. 4K without effects records the camera directly with CameraX.
 */
class AndroidMedia(private val activity: ComponentActivity, private val camera: AndroidCamera, private val audio: AndroidAudio) : MediaService {
    private val ctx: Context = activity.applicationContext
    override val state = MutableStateFlow(MediaState())
    override val messages = MutableSharedFlow<String>(extraBufferCapacity = 8)
    private val clock = RecordingClock()
    private var ticker: Timer? = null

    // Processed recording.
    private var venc: VideoEncoder? = null
    private var aenc: AudioEncoder? = null
    private var muxer: MuxerSink? = null
    private var frameSink: FrameSink? = null
    private var pcm: AndroidAudio.PcmListener? = null
    // Direct 4K.
    private var direct: Recording? = null

    private fun say(s: String) { messages.tryEmit(s) }
    private fun now() = System.currentTimeMillis()
    private fun stamp() = SimpleDateFormat("yyyyMMdd-HHmmss", Locale.US).format(Date())
    private fun hasMic() = ctx.checkSelfPermission(Manifest.permission.RECORD_AUDIO) == PackageManager.PERMISSION_GRANTED

    override fun startRecording(options: RecordOptions) {
        if (state.value.recording) return
        val fx = camera.state.value
        if (options.quality == Quality.UHD && !options.vertical) { startDirect(); return }
        val (w, h) = size(options)
        if (!hasMic()) say("Recording without sound: KISSIK isn't allowed to use the microphone.")
        runCatching {
            val file = File(ctx.cacheDir, "KISSIK-${stamp()}.mp4")
            val mux = MuxerSink(file, expectAudio = hasMic())
            val v = VideoEncoder(w, h, if (options.quality == Quality.UHD) 35_000_000 else 12_000_000, 30, sinks = listOf(mux))
            val a = if (hasMic()) AudioEncoder(AndroidAudio.SAMPLE_RATE, listOf(mux)) else null
            muxer = mux; venc = v; aenc = a
            frameSink = FrameSink { b, _ -> v.draw(b) }.also { camera.sinks += it }
            if (a != null) pcm = AndroidAudio.PcmListener { p, n, t -> a.feed(p, n, t) }.also { audio.addListener(it) }
            clock.start(now()); keepAwake(true); tick()
            state.value = state.value.copy(recording = true, paused = false, elapsedMs = 0)
        }.onFailure { say("Recording couldn't start: ${it.message}"); cleanupProcessed() }
        fx.width // keep reference (no-op)
    }

    /** Output size: the camera's upright shape at the chosen quality (even numbers for the encoder). */
    private fun size(o: RecordOptions): Pair<Int, Int> {
        val s = camera.state.value
        val portrait = o.vertical || s.height > s.width
        val short = o.quality.shortSide.coerceAtMost(1080)
        val long = short * 16 / 9
        return if (portrait) short to long else long to short
    }

    @SuppressLint("MissingPermission")
    private fun startDirect() {
        val recorder = Recorder.Builder().setQualitySelector(QualitySelector.from(CxQuality.UHD, FallbackStrategy.lowerQualityOrHigherThan(CxQuality.FHD))).build()
        val vc = VideoCapture.withOutput(recorder)
        if (!camera.bindDirect(vc)) { say("This camera can't record 4K here. Try 1080p."); return }
        val out = MediaStoreOutputOptions.Builder(ctx.contentResolver, MediaStore.Video.Media.EXTERNAL_CONTENT_URI)
            .setContentValues(mediaValues("KISSIK-${stamp()}.mp4", "video/mp4", Environment.DIRECTORY_MOVIES)).build()
        val pending = recorder.prepareRecording(ctx, out).let { if (hasMic()) it.withAudioEnabled() else it }
        direct = pending.start(activity.mainExecutor) { e ->
            if (e is VideoRecordEvent.Finalize) {
                if (e.hasError()) say("Recording failed (${e.error})") else say("Saved 4K video to Gallery")
                direct = null; camera.rebind()
            }
        }
        clock.start(now()); keepAwake(true); tick()
        state.value = state.value.copy(recording = true, paused = false, elapsedMs = 0)
    }

    override fun pause() {
        direct?.pause(); muxer?.paused = true; clock.pause(now())
        state.value = state.value.copy(paused = true)
    }

    override fun resume() {
        clock.resume(now())
        direct?.resume(); muxer?.let { it.pausedUs = clock.pausedUs(); it.paused = false }
        state.value = state.value.copy(paused = false)
    }

    // Away from the app the camera stops, so the recording would hold a frozen picture: pause it, and carry on
    // when the person comes back.
    private var pausedForBackground = false
    fun onBackground() { if (state.value.recording && !state.value.paused) { pause(); pausedForBackground = true } }
    fun onForeground() {
        if (!pausedForBackground) return
        pausedForBackground = false
        if (state.value.recording && state.value.paused) { resume(); say("The recording paused while KISSIK was in the background. It is recording again.") }
    }

    override fun stopRecording() {
        if (!state.value.recording) return
        pausedForBackground = false
        clock.stop(); ticker?.cancel(); ticker = null; keepAwake(false)
        state.value = state.value.copy(recording = false, paused = false, elapsedMs = 0)
        direct?.let { it.stop(); return }
        val mux = muxer
        Thread {
            cleanupProcessed()
            if (mux != null && mux.finish()) {
                val uri = publish(mux.file, "video/mp4", Environment.DIRECTORY_MOVIES)
                say(if (uri != null) "Saved video to Gallery" else "Couldn't save the video")
            } else say("Nothing was recorded")
        }.start()
    }

    private fun cleanupProcessed() {
        frameSink?.let { camera.sinks -= it }; frameSink = null
        pcm?.let { audio.removeListener(it) }; pcm = null
        venc?.stop(); venc = null; aenc?.stop(); aenc = null; muxer = null
    }

    private fun tick() {
        ticker?.cancel()
        ticker = Timer().apply { scheduleAtFixedRate(timerTask { state.value = state.value.copy(elapsedMs = clock.elapsedMs(now())) }, 0, 500) }
    }

    // ── Replay buffer ──────────────────────────────────────────────────────
    private var replay: ReplaySink? = null
    private var replayV: VideoEncoder? = null
    private var replayA: AudioEncoder? = null
    private var replayFrames: FrameSink? = null
    private var replayPcm: AndroidAudio.PcmListener? = null
    private var replayTicker: Timer? = null

    override fun setReplay(on: Boolean, seconds: Int) {
        stopReplay()
        if (!on) { state.value = state.value.copy(replayOn = false); return }
        runCatching {
            val sink = ReplaySink(seconds)
            val s = camera.state.value
            val (w, h) = if (s.height > s.width) 720 to 1280 else 1280 to 720
            val v = VideoEncoder(w, h, 6_000_000, 30, sinks = listOf(sink))
            val a = if (hasMic()) AudioEncoder(AndroidAudio.SAMPLE_RATE, listOf(sink)) else null
            replay = sink; replayV = v; replayA = a
            replayFrames = FrameSink { b, _ -> v.draw(b) }.also { camera.sinks += it }
            if (a != null) replayPcm = AndroidAudio.PcmListener { p, n, t -> a.feed(p, n, t) }.also { audio.addListener(it) }
            replayTicker = Timer().apply { scheduleAtFixedRate(timerTask {
                state.value = state.value.copy(replayBufferedSec = (sink.buffer.durationUs / 1_000_000).toInt()) }, 500, 1000) }
            state.value = state.value.copy(replayOn = true, replaySeconds = seconds, replayBufferedSec = 0)
        }.onFailure { say("Replay buffer couldn't start: ${it.message}"); stopReplay() }
    }

    private fun stopReplay() {
        replayTicker?.cancel(); replayTicker = null
        replayFrames?.let { camera.sinks -= it }; replayFrames = null
        replayPcm?.let { audio.removeListener(it) }; replayPcm = null
        replayV?.stop(); replayV = null; replayA?.stop(); replayA = null; replay = null
    }

    override fun saveReplay() {
        val sink = replay ?: run { say("Turn on the replay buffer first"); return }
        Thread {
            val f = File(ctx.cacheDir, "KISSIK-replay-${stamp()}.mp4")
            val ok = runCatching { sink.save(f) }.getOrDefault(false)
            say(if (ok && publish(f, "video/mp4", Environment.DIRECTORY_MOVIES) != null) "Saved the last ${state.value.replaySeconds} seconds to Gallery" else "Nothing to save yet")
        }.start()
    }

    // ── Snapshot ───────────────────────────────────────────────────────────
    override fun snapshot() {
        val once = object : FrameSink {
            override fun onFrame(bmp: Bitmap, timeNs: Long) {
                camera.sinks -= this
                val f = File(ctx.cacheDir, "KISSIK-${stamp()}.jpg")
                f.outputStream().use { bmp.compress(Bitmap.CompressFormat.JPEG, 95, it) }
                say(if (publish(f, "image/jpeg", Environment.DIRECTORY_PICTURES) != null) "Saved photo to Gallery" else "Couldn't save the photo")
            }
        }
        camera.sinks += once
    }

    // ── Time-lapse ─────────────────────────────────────────────────────────
    private var tlEnc: VideoEncoder? = null
    private var tlMux: MuxerSink? = null
    private var tlSink: FrameSink? = null

    override fun startTimeLapse(spec: TimeLapse) {
        if (state.value.timeLapse) return
        runCatching {
            val s = camera.state.value
            val (w, h) = if (s.height > s.width) 1080 to 1920 else 1920 to 1080
            val mux = MuxerSink(File(ctx.cacheDir, "KISSIK-timelapse-${stamp()}.mp4"), expectAudio = false, retimeFps = spec.playbackFps)
            val enc = VideoEncoder(w, h, 10_000_000, spec.playbackFps, sinks = listOf(mux))
            var last = -1L; var frames = 0
            tlMux = mux; tlEnc = enc
            tlSink = FrameSink { b, _ ->
                val t = System.currentTimeMillis()
                if (spec.shouldCapture(last, t)) {
                    last = t; frames++
                    // Each kept frame is drawn twice: some encoders hold the last surface frame until the next one arrives.
                    enc.draw(b); enc.requestKey = frames % 30 == 0
                    state.value = state.value.copy(timeLapseFrames = frames)
                }
            }.also { camera.sinks += it }
            keepAwake(true)
            state.value = state.value.copy(timeLapse = true, timeLapseFrames = 0)
        }.onFailure { say("Time-lapse couldn't start: ${it.message}") }
    }

    override fun stopTimeLapse() {
        tlSink?.let { camera.sinks -= it }; tlSink = null
        val enc = tlEnc; val mux = tlMux; tlEnc = null; tlMux = null
        state.value = state.value.copy(timeLapse = false)
        keepAwake(false)
        Thread {
            enc?.stop()
            if (mux != null && mux.finish()) say(if (publish(mux.file, "video/mp4", Environment.DIRECTORY_MOVIES) != null) "Saved time-lapse to Gallery" else "Couldn't save the time-lapse")
            else say("Time-lapse too short to save")
        }.start()
    }

    // ── Gallery ────────────────────────────────────────────────────────────
    private fun mediaValues(name: String, mime: String, dir: String) = ContentValues().apply {
        put(MediaStore.MediaColumns.DISPLAY_NAME, name)
        put(MediaStore.MediaColumns.MIME_TYPE, mime)
        if (Build.VERSION.SDK_INT >= 29) put(MediaStore.MediaColumns.RELATIVE_PATH, "$dir/KISSIK")
    }

    /** Copies a finished file into the gallery (Movies/KISSIK or Pictures/KISSIK) and deletes the temp file. */
    fun publish(file: File, mime: String, dir: String): Uri? = runCatching {
        val coll = if (mime.startsWith("video")) MediaStore.Video.Media.EXTERNAL_CONTENT_URI else MediaStore.Images.Media.EXTERNAL_CONTENT_URI
        val values = mediaValues(file.name, mime, dir)
        if (Build.VERSION.SDK_INT >= 29) values.put(MediaStore.MediaColumns.IS_PENDING, 1)
        val uri = ctx.contentResolver.insert(coll, values) ?: return null
        ctx.contentResolver.openOutputStream(uri)!!.use { o -> file.inputStream().use { it.copyTo(o) } }
        if (Build.VERSION.SDK_INT >= 29) ctx.contentResolver.update(uri, ContentValues().apply { put(MediaStore.MediaColumns.IS_PENDING, 0) }, null, null)
        file.delete()
        uri
    }.getOrNull()

    /** The screen is kept on by KeepScreenOn (AndroidPlatform.kt), which also knows about streams and the PC link. */
    @Suppress("UNUSED_PARAMETER") private fun keepAwake(on: Boolean) {}

    fun release() { if (state.value.recording) stopRecording(); stopReplay(); if (state.value.timeLapse) stopTimeLapse() }
}
