@file:OptIn(kotlinx.cinterop.ExperimentalForeignApi::class, kotlinx.cinterop.BetaInteropApi::class)

package app.kissik.studio.ios

import app.kissik.studio.image.Frame
import app.kissik.studio.image.scaled
import app.kissik.studio.platform.*
import app.kissik.studio.recording.RecordingClock
import app.kissik.studio.recording.TimeLapse
import app.kissik.studio.webcam.LinkStatus
import app.kissik.studio.webcam.Pairing
import app.kissik.studio.webcam.PcCommand
import app.kissik.studio.webcam.PhoneProtocol
import kotlinx.cinterop.*
import kotlinx.coroutines.flow.MutableSharedFlow
import kotlinx.coroutines.flow.MutableStateFlow
import platform.AVFoundation.*
import platform.CoreMedia.*
import platform.CoreVideo.*
import platform.Foundation.*
import platform.Photos.*
import platform.UIKit.*
import platform.posix.memcpy

/** Writes processed frames to an .mp4 with AVAssetWriter (H.264, hardware). Audio is appended as AAC. */
class FrameWriter(val path: String, val width: Int, val height: Int, private val withAudio: Boolean, private val retimeFps: Int = 0) {
    private val writer = AVAssetWriter(NSURL.fileURLWithPath(path), AVFileTypeMPEG4, null)
    private val video = AVAssetWriterInput(AVMediaTypeVideo, mapOf<Any?, Any?>(AVVideoCodecKey to AVVideoCodecTypeH264, AVVideoWidthKey to width, AVVideoHeightKey to height))
        .apply { expectsMediaDataInRealTime = true }
    private val adaptor = AVAssetWriterInputPixelBufferAdaptor(video, mapOf<Any?, Any?>(kCVPixelBufferPixelFormatTypeKey to kCVPixelFormatType_32BGRA,
        kCVPixelBufferWidthKey to width, kCVPixelBufferHeightKey to height))
    private val audio = if (withAudio) AVAssetWriterInput(AVMediaTypeAudio, mapOf<Any?, Any?>(AVFormatIDKey to kAudioFormatMPEG4AAC, AVNumberOfChannelsKey to 1,
        AVSampleRateKey to 48000, AVEncoderBitRateKey to 128000)).apply { expectsMediaDataInRealTime = true } else null
    private var startUs = -1L
    private var frames = 0
    var paused = false
    var pausedUs = 0L

    init { writer.addInput(video); audio?.let { writer.addInput(it) }; writer.startWriting() }

    fun append(f: Frame, timeUs: Long) {
        if (paused || !video.readyForMoreMediaData) return
        if (startUs < 0) { startUs = timeUs; writer.startSessionAtSourceTime(CMTimeMake(0, 1_000_000)) }
        val t = if (retimeFps > 0) frames * 1_000_000L / retimeFps else timeUs - startUs - pausedUs
        val pool = adaptor.pixelBufferPool ?: return
        memScoped {
            val out = alloc<CVPixelBufferRefVar>()
            CVPixelBufferPoolCreatePixelBuffer(null, pool, out.ptr)
            val pb = out.value ?: return
            CVPixelBufferLockBaseAddress(pb, 0u)
            val stride = CVPixelBufferGetBytesPerRow(pb).toInt()
            val base = CVPixelBufferGetBaseAddress(pb)!!.reinterpret<ByteVar>()
            val src = if (f.width == width && f.height == height) f else f.scaled(width, height)
            src.pixels.usePinned { p -> for (y in 0 until height) memcpy(base + y * stride, p.addressOf(y * width), (width * 4).toULong()) }
            CVPixelBufferUnlockBaseAddress(pb, 0u)
            adaptor.appendPixelBuffer(pb, CMTimeMake(t, 1_000_000))
            CVPixelBufferRelease(pb)
        }
        frames++
    }

    fun finish(done: (Boolean) -> Unit) {
        video.markAsFinished(); audio?.markAsFinished()
        writer.finishWritingWithCompletionHandler { onMain { done(writer.status == AVAssetWriterStatusCompleted && frames > 0) } }
    }
}

fun saveToPhotos(path: String, video: Boolean, done: (Boolean) -> Unit) {
    PHPhotoLibrary.requestAuthorizationForAccessLevel(PHAccessLevelAddOnly) { _ ->
        PHPhotoLibrary.sharedPhotoLibrary().performChanges({
            val url = NSURL.fileURLWithPath(path)
            if (video) PHAssetChangeRequest.creationRequestForAssetFromVideoAtFileURL(url) else PHAssetChangeRequest.creationRequestForAssetFromImageAtFileURL(url)
        }) { ok, _ -> onMain { done(ok) } }
    }
}

class IosMedia(private val camera: IosCamera, private val audio: IosAudio) : MediaService {
    override val state = MutableStateFlow(MediaState())
    override val messages = MutableSharedFlow<String>(extraBufferCapacity = 8)
    private val clock = RecordingClock()
    private var writer: FrameWriter? = null
    private var sink: IosFrameSink? = null
    private fun now() = (NSDate().timeIntervalSince1970 * 1000).toLong()
    private fun tmp(ext: String) = NSTemporaryDirectory() + "KISSIK-${now()}.$ext"

    override fun startRecording(options: RecordOptions) {
        val f = camera.lastFrame ?: run { messages.tryEmit("The camera isn't ready yet"); return }
        val short = options.quality.shortSide.coerceAtMost(2160); val long = short * 16 / 9
        val (w, h) = if (f.height > f.width || options.vertical) short to long else long to short
        val wr = FrameWriter(tmp("mp4"), w, h, withAudio = false)
        writer = wr
        sink = IosFrameSink { fr, t -> wr.append(fr, t) }.also { camera.sinks += it }
        clock.start(now())
        UIApplication.sharedApplication.idleTimerDisabled = true
        state.value = state.value.copy(recording = true, paused = false)
    }
    override fun pause() { clock.pause(now()); writer?.paused = true; state.value = state.value.copy(paused = true) }
    override fun resume() { clock.resume(now()); writer?.let { it.pausedUs = clock.pausedUs(); it.paused = false }; state.value = state.value.copy(paused = false) }
    override fun stopRecording() {
        sink?.let { camera.sinks -= it }; sink = null
        val wr = writer ?: return; writer = null
        clock.stop(); UIApplication.sharedApplication.idleTimerDisabled = false
        state.value = state.value.copy(recording = false, paused = false, elapsedMs = 0)
        wr.finish { ok -> if (ok) saveToPhotos(wr.path, true) { s -> messages.tryEmit(if (s) "Saved video to Photos" else "Couldn't save to Photos") } }
    }

    /** Replay on iOS keeps the last N seconds as rolling 5-second files and joins them on save. */
    private val chunks = ArrayDeque<String>()
    private var replayWriter: FrameWriter? = null
    private var replaySink: IosFrameSink? = null
    private var chunkStart = 0L
    override fun setReplay(on: Boolean, seconds: Int) {
        replaySink?.let { camera.sinks -= it }; replaySink = null; replayWriter = null
        state.value = state.value.copy(replayOn = on, replaySeconds = seconds)
        if (!on) return
        replaySink = IosFrameSink { f, t ->
            if (replayWriter == null || t - chunkStart > 5_000_000) {
                replayWriter?.let { old -> old.finish { ok -> if (ok) { chunks.addLast(old.path); while (chunks.size > seconds / 5 + 1) NSFileManager.defaultManager.removeItemAtPath(chunks.removeFirst(), null) } } }
                replayWriter = FrameWriter(tmp("mp4"), f.width and 1.inv(), f.height and 1.inv(), false); chunkStart = t
            }
            replayWriter?.append(f, t)
            state.value = state.value.copy(replayBufferedSec = minOf(seconds, chunks.size * 5))
        }.also { camera.sinks += it }
    }
    override fun saveReplay() {
        val parts = chunks.toList(); if (parts.isEmpty()) { messages.tryEmit("Nothing to save yet"); return }
        val comp = AVMutableComposition()
        val track = comp.addMutableTrackWithMediaType(AVMediaTypeVideo, kCMPersistentTrackID_Invalid)
        var at = kCMTimeZero.readValue()
        parts.forEach { p ->
            val a = AVURLAsset(NSURL.fileURLWithPath(p), null)
            (a.tracksWithMediaType(AVMediaTypeVideo).firstOrNull() as? AVAssetTrack)?.let { t ->
                track?.insertTimeRange(CMTimeRangeMake(kCMTimeZero.readValue(), a.duration), t, at, null); at = CMTimeAdd(at, a.duration)
            }
        }
        val out = tmp("mp4")
        AVAssetExportSession(comp, AVAssetExportPresetPassthrough)?.apply { outputURL = NSURL.fileURLWithPath(out); outputFileType = AVFileTypeMPEG4 }
            ?.exportAsynchronouslyWithCompletionHandler { saveToPhotos(out, true) { ok -> messages.tryEmit(if (ok) "Saved the replay to Photos" else "Couldn't save the replay") } }
    }

    override fun snapshot() {
        val img = camera.lastFrame?.toUIImage() ?: return
        val path = tmp("jpg")
        UIImageJPEGRepresentation(img, 0.95)?.writeToFile(path, true)
        saveToPhotos(path, false) { ok -> messages.tryEmit(if (ok) "Saved photo to Photos" else "Couldn't save the photo") }
    }

    private var tl: FrameWriter? = null
    private var tlSink: IosFrameSink? = null
    override fun startTimeLapse(spec: TimeLapse) {
        var last = -1L; var n = 0
        tlSink = IosFrameSink { f, _ ->
            val t = now()
            if (spec.shouldCapture(last, t)) {
                last = t; n++
                val w = tl ?: FrameWriter(tmp("mp4"), f.width and 1.inv(), f.height and 1.inv(), false, spec.playbackFps).also { tl = it }
                w.append(f, 0); state.value = state.value.copy(timeLapseFrames = n)
            }
        }.also { camera.sinks += it }
        state.value = state.value.copy(timeLapse = true, timeLapseFrames = 0)
    }
    override fun stopTimeLapse() {
        tlSink?.let { camera.sinks -= it }; tlSink = null
        state.value = state.value.copy(timeLapse = false)
        val w = tl ?: return; tl = null
        w.finish { ok -> if (ok) saveToPhotos(w.path, true) { s -> messages.tryEmit(if (s) "Saved time-lapse to Photos" else "Couldn't save the time-lapse") } }
    }
}

/** Use as a PC webcam over NSURLSession WebSockets (JPEG frames + status JSON + commands). */
class IosWebcam(private val camera: IosCamera, private val audio: IosAudio, private val store: KeyValueStore) : WebcamService {
    override val state = MutableStateFlow(WebcamState.Off)
    override val detail = MutableStateFlow("")
    private var task: NSURLSessionWebSocketTask? = null
    private var sink: IosFrameSink? = null
    private var pcm: IosAudio.PcmListener? = null
    private var lastMs = 0L
    private var codec = "jpeg"
    private var encoderStarted = false

    override fun connect(pairing: Pairing, useUsb: Boolean) {
        disconnect()
        state.value = WebcamState.Connecting
        // Wi-Fi uses the PC's self-made certificate, pinned on first use.
        val session = NSURLSession.sessionWithConfiguration(NSURLSessionConfiguration.defaultSessionConfiguration, PinningDelegate(store, pairing.host), null)
        val t = session.webSocketTaskWithURL(NSURL.URLWithString(if (useUsb) pairing.usbUrl else pairing.wifiUrl)!!)
        task = t; t.resume()
        state.value = WebcamState.Connected; detail.value = if (useUsb) "Over USB" else "Over Wi-Fi to ${pairing.host}"
        sendStatus(); receive()
        sink = IosFrameSink { f, t ->
            val now = (NSDate().timeIntervalSince1970 * 1000).toLong()
            if (now - lastMs < 33) return@IosFrameSink
            lastMs = now
            val enc = Bridges.h264
            if (codec == "h264" && enc != null) {
                if (!encoderStarted) {
                    encoderStarted = true
                    enc.start(f.width and 1.inv(), f.height and 1.inv(), 30, 4000) { data, key, us ->
                        send(NSURLSessionWebSocketMessage(data = PhoneProtocol.videoPacket(data.toByteArray(), key, us).toNSData()))
                    }
                }
                enc.encode(f.toBgraData(), f.width, f.height, t)
            } else f.toUIImage()?.let { UIImageJPEGRepresentation(it, 0.8) }?.let { send(NSURLSessionWebSocketMessage(data = it)) }
        }.also { camera.sinks += it }
        pcm = IosAudio.PcmListener { s, rate, _ -> send(NSURLSessionWebSocketMessage(data = PhoneProtocol.audioPacket(s, rate).toNSData())) }.also { audio.addListener(it) }
    }

    private fun send(m: NSURLSessionWebSocketMessage) = task?.sendMessage(m) { e -> if (e != null) onMain { fail(e.localizedDescription) } }

    private fun receive() {
        task?.receiveMessageWithCompletionHandler { m, e ->
            if (e != null) { onMain { fail(e.localizedDescription) }; return@receiveMessageWithCompletionHandler }
            m?.string?.let { onMain { handle(PcCommand.parse(it)) } }
            receive()
        }
    }

    private fun sendStatus() {
        UIDevice.currentDevice.batteryMonitoringEnabled = true
        val link = LinkStatus(UIDevice.currentDevice.model, UIDevice.currentDevice.batteryLevel, UIDevice.currentDevice.batteryState == UIDeviceBatteryState.UIDeviceBatteryStateCharging,
            codec = codec, mics = audio.mics.value.map { it.id to it.label }, micDev = audio.selectedMic.value, ns = audio.noiseSuppression.value)
        send(NSURLSessionWebSocketMessage(string = PhoneProtocol.status(camera.state.value, link)))
    }

    private fun handle(c: PcCommand) {
        when (c) {
            is PcCommand.Facing -> if (camera.state.value.front != c.front) camera.switchFacing()
            is PcCommand.Lens -> camera.selectLens(c.id)
            is PcCommand.Quality -> camera.setQuality(c.shortSide)
            is PcCommand.Torch -> camera.setTorch(c.on)
            is PcCommand.Zoom -> camera.setZoom(c.factor)
            is PcCommand.Ev -> camera.setEv(c.index)
            is PcCommand.Iso -> camera.setIso(c.value)
            is PcCommand.Shutter -> camera.setShutter(c.ns)
            PcCommand.ExposureAuto -> { camera.setIso(null); camera.setShutter(null) }
            is PcCommand.Focus -> camera.setFocus(c.value)
            is PcCommand.FocusTap -> camera.tapFocus(c.x, c.y)
            is PcCommand.WhiteBalance -> camera.setWhiteBalance(c.mode)
            is PcCommand.Fps -> camera.setFps(c.fps)
            is PcCommand.Stabilization -> camera.setStabilization(c.on)
            is PcCommand.Hdr -> camera.setHdr(c.on)
            is PcCommand.MicDevice -> audio.selectMic(c.id)
            is PcCommand.NoiseSuppression -> audio.setNoiseSuppression(c.on)
            is PcCommand.Mic -> audio.setMuted(!c.on)
            is PcCommand.Codec -> {
                codec = if (c.name == "h264" && Bridges.h264 != null) "h264" else "jpeg"
                if (codec == "jpeg" && encoderStarted) { Bridges.h264?.stop(); encoderStarted = false }
            }
            PcCommand.KeyFrame -> Bridges.h264?.requestKeyFrame()
            else -> {}
        }
        sendStatus()
    }

    private fun fail(msg: String) { cleanup(); state.value = WebcamState.Failed; detail.value = msg }
    private fun cleanup() { if (encoderStarted) { Bridges.h264?.stop(); encoderStarted = false }; codec = "jpeg"; sink?.let { camera.sinks -= it }; sink = null; pcm?.let { audio.removeListener(it) }; pcm = null }
    override fun disconnect() { cleanup(); task?.cancelWithCloseCode(1000, null); task = null; state.value = WebcamState.Off; detail.value = "" }
}
