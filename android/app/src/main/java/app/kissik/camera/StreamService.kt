package app.kissik.camera

import android.Manifest
import android.annotation.SuppressLint
import android.app.Notification
import android.app.NotificationChannel
import android.app.NotificationManager
import android.app.PendingIntent
import android.content.Context
import android.content.Intent
import android.content.IntentFilter
import android.content.pm.PackageManager
import android.content.pm.ServiceInfo
import android.graphics.Bitmap
import android.graphics.Matrix
import android.media.AudioFormat
import android.media.AudioRecord
import android.media.MediaRecorder
import android.net.wifi.WifiManager
import android.os.BatteryManager
import android.os.Build
import android.os.PowerManager
import android.util.Size
import android.view.OrientationEventListener
import android.view.Surface
import android.graphics.ImageFormat
import android.hardware.camera2.CameraCharacteristics
import androidx.camera.camera2.interop.Camera2CameraControl
import androidx.camera.camera2.interop.Camera2CameraInfo
import androidx.camera.core.FocusMeteringAction
import androidx.camera.core.SurfaceOrientedMeteringPointFactory
import androidx.camera.core.Preview
import androidx.camera.core.UseCase
import androidx.camera.extensions.ExtensionsManager
import androidx.camera.video.VideoCapture
import kotlinx.coroutines.flow.merge
import androidx.camera.camera2.interop.ExperimentalCamera2Interop
import androidx.camera.core.Camera
import androidx.camera.core.CameraSelector
import androidx.camera.core.ImageAnalysis
import androidx.camera.core.ImageProxy
import androidx.camera.core.resolutionselector.ResolutionSelector
import androidx.camera.core.resolutionselector.ResolutionStrategy
import androidx.camera.lifecycle.ProcessCameraProvider
import androidx.core.app.NotificationCompat
import androidx.core.content.ContextCompat
import androidx.lifecycle.LifecycleService
import androidx.lifecycle.lifecycleScope
import kotlinx.coroutines.CompletableDeferred
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.Job
import kotlinx.coroutines.delay
import kotlinx.coroutines.flow.combine
import kotlinx.coroutines.flow.distinctUntilChanged
import kotlinx.coroutines.isActive
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext
import okhttp3.OkHttpClient
import okhttp3.Request
import okhttp3.Response
import okhttp3.WebSocket
import okhttp3.WebSocketListener
import okio.ByteString
import okio.ByteString.Companion.toByteString
import org.json.JSONObject
import java.io.ByteArrayOutputStream
import java.nio.ByteBuffer
import java.nio.ByteOrder
import java.util.concurrent.Executors
import java.util.concurrent.TimeUnit

/**
 * Streams the camera (JPEG frames, or hardware H.264 when the PC asks for it) and, when switched on, the mic to KISSIK on the PC. Runs as a foreground service so
 * it keeps going with the screen off; the notification says so and has a Stop button.
 */
class StreamService : LifecycleService() {
    companion object {
        private const val CHANNEL = "stream"
        private const val STOP = "app.kissik.camera.STOP"
        private const val RECONNECT = "app.kissik.camera.RECONNECT"
        fun start(ctx: Context) = ContextCompat.startForegroundService(ctx, Intent(ctx, StreamService::class.java))
        /** Starts streaming, or if already streaming, drops the link so the next connect uses the new pairing. */
        fun reconnect(ctx: Context) = ContextCompat.startForegroundService(ctx, Intent(ctx, StreamService::class.java).setAction(RECONNECT))
        fun stop(ctx: Context) { ctx.startService(Intent(ctx, StreamService::class.java).setAction(STOP)) }
    }

    @Volatile private var ws: WebSocket? = null
    /** The socket being opened or in use, so a re-pair can drop it even mid-connect. */
    @Volatile private var attempt: WebSocket? = null
    private var camera: Camera? = null
    private val analysisExecutor = Executors.newSingleThreadExecutor()
    private var wake: PowerManager.WakeLock? = null
    private var wifiLock: WifiManager.WifiLock? = null
    private var micJob: Job? = null
    private var started = false
    private var analysis: ImageAnalysis? = null
    private var previewUse: Preview? = null
    /** Our own bound camera streams (never the scanner's), so they can be released precisely. */
    private var bound: List<UseCase> = emptyList()
    @Volatile private var lastRotation = 0
    @Volatile private var encoder: H264Encoder? = null
    /** After dropping video on a slow link, skip until the next keyframe so the PC never sees a broken picture. */
    @Volatile private var needKey = true
    private val encoderExecutor = Executors.newSingleThreadExecutor()
    /** Which way up the phone is held (Surface.ROTATION_*). A service has no screen to ask, so the motion sensor tells us. */
    @Volatile private var deviceRotation = Surface.ROTATION_0
    private var orientation: OrientationEventListener? = null

    override fun onStartCommand(intent: Intent?, flags: Int, startId: Int): Int {
        super.onStartCommand(intent, flags, startId)
        if (intent?.action == STOP) { stopSelf(); return START_NOT_STICKY }
        startForegroundNow()
        if (!started) { started = true; begin() } else if (intent?.action == RECONNECT) attempt?.cancel()
        // Not sticky: Android 14+ forbids a camera service restarting itself from the background. The app resumes on open.
        return START_NOT_STICKY
    }

    private fun startForegroundNow() {
        val nm = getSystemService(NotificationManager::class.java)
        nm.createNotificationChannel(NotificationChannel(CHANNEL, "Streaming to your PC", NotificationManager.IMPORTANCE_LOW))
        val n = notification("Connecting to your PC…")
        if (Build.VERSION.SDK_INT >= 30) {
            var type = ServiceInfo.FOREGROUND_SERVICE_TYPE_CAMERA
            if (hasMic()) type = type or ServiceInfo.FOREGROUND_SERVICE_TYPE_MICROPHONE
            startForeground(1, n, type)
        } else startForeground(1, n)
    }

    private fun notification(text: String): Notification {
        val open = PendingIntent.getActivity(this, 0, Intent(this, MainActivity::class.java), PendingIntent.FLAG_IMMUTABLE)
        val stop = PendingIntent.getService(this, 1, Intent(this, StreamService::class.java).setAction(STOP), PendingIntent.FLAG_IMMUTABLE)
        return NotificationCompat.Builder(this, CHANNEL)
            .setSmallIcon(R.drawable.ic_stat).setContentTitle("KISSIK is using your camera")
            .setContentText(text).setOngoing(true).setContentIntent(open)
            .addAction(0, "Stop", stop).setForegroundServiceBehavior(NotificationCompat.FOREGROUND_SERVICE_IMMEDIATE).build()
    }

    private fun updateNotification(text: String) =
        getSystemService(NotificationManager::class.java).notify(1, notification(text))

    private fun hasMic() = ContextCompat.checkSelfPermission(this, Manifest.permission.RECORD_AUDIO) == PackageManager.PERMISSION_GRANTED

    @SuppressLint("WakelockTimeout")
    private fun begin() {
        Live.load(this)
        Live.running.value = true
        wake = getSystemService(PowerManager::class.java).newWakeLock(PowerManager.PARTIAL_WAKE_LOCK, "kissik:stream").apply { acquire() }
        @Suppress("DEPRECATION")
        wifiLock = (applicationContext.getSystemService(WIFI_SERVICE) as WifiManager)
            .createWifiLock(WifiManager.WIFI_MODE_FULL_HIGH_PERF, "kissik:stream").apply { acquire() }

        // Rebind the camera whenever the lens, the effective quality or the video format changes.
        lifecycleScope.launch {
            // Hardware video sets its frame rate when the stream is built, so a new rate rebuilds it (JPEG doesn't need to).
            combine(combine(Live.front, Pro.lensId, Live.quality, Live.saving, Pro.codec) { f, l, q, s, c -> BindKey(f, l, if (s) minOf(q, 480) else q, c, "none") },
                Pro.effect, Pro.fps) { k, e, fps -> k.copy(effect = e, fps = if (k.codec == "jpeg") 0 else fps) }
                .distinctUntilChanged().collect { bindCamera(it) }
        }
        lifecycleScope.launch { merge(Pro.ev, Pro.iso, Pro.shutterNs, Pro.focus, Pro.wb, Pro.fps, Pro.stab, Pro.hdr, Pro.lowLatency).collect { applyPro() } }
        lifecycleScope.launch { Live.focusTap.collect { (x, y) -> focusAt(x, y) } }
        lifecycleScope.launch { Live.meteringTap.collect { focusOn(it) } }
        // The phone's screen preview comes and goes with the app; the stream to the PC carries on either way.
        lifecycleScope.launch { Live.previewSurface.collect { previewUse?.surfaceProvider = it } }
        lifecycleScope.launch { Live.torch.collect { camera?.cameraControl?.enableTorch(it); sendStatus() } }
        lifecycleScope.launch { Live.zoom.collect { camera?.cameraControl?.setZoomRatio(it); sendStatus() } }
        lifecycleScope.launch {
            // Reopen the mic whenever it's switched, or its device, mode or noise suppression changes.
            combine(Live.mic, Live.link, Mic.deviceId, Mic.mode, Mic.ns) { m, l, d, md, n -> listOf(m && (l == Link.Wifi || l == Link.Usb), d, md, n) }
                .distinctUntilChanged().collect { setMic(it[0] as Boolean); sendStatus() }
        }
        // Keep the picture upright as the phone turns: frames are rotated by sensor angle + how the phone is held.
        orientation = object : OrientationEventListener(this) {
            override fun onOrientationChanged(deg: Int) {
                if (deg == ORIENTATION_UNKNOWN) return
                val r = when (deg) { in 45..134 -> Surface.ROTATION_270; in 135..224 -> Surface.ROTATION_180; in 225..314 -> Surface.ROTATION_90; else -> Surface.ROTATION_0 }
                if (r == deviceRotation) return
                deviceRotation = r
                analysis?.targetRotation = r
                previewUse?.targetRotation = r
                lifecycleScope.launch { delay(400); sendStatus() } // after a frame at the new size
            }
        }.apply { if (canDetectOrientation()) enable() }
        // Keep the mic list current as headsets and USB mics come and go.
        Mic.refresh(this)
        getSystemService(android.media.AudioManager::class.java).registerAudioDeviceCallback(object : android.media.AudioDeviceCallback() {
            override fun onAudioDevicesAdded(added: Array<out android.media.AudioDeviceInfo>) { Mic.refresh(this@StreamService); sendStatus() }
            override fun onAudioDevicesRemoved(removed: Array<out android.media.AudioDeviceInfo>) { Mic.refresh(this@StreamService); sendStatus() }
        }, null)
        lifecycleScope.launch(Dispatchers.IO) { connectLoop() }
        lifecycleScope.launch { while (isActive) { checkSaver(); sendStatus(); delay(5_000) } }
        lifecycleScope.launch {
            while (isActive) {
                delay(1000)
                txFps = txSent; val dropped = txDropped; txSent = 0; txDropped = 0
                if (ws != null) netLog("tx ${txFps} fps, dropped $dropped, queue ${(ws?.queueSize() ?: 0) / 1024} KB, bitrate ${(encoder?.currentBitrate ?: 0) / 1000} kbps, capture->encoded ${encoder?.captureToEncodedUs?.averageMs()?.let { "%.0f ms".format(it) } ?: "-"}")
            }
        }
    }

    private data class BindKey(val front: Boolean, val lensId: String?, val quality: Int, val codec: String, val effect: String, val fps: Int = 0)
    private var boundLens: Pair<Boolean, String?>? = null

    private fun sizeFor(shortSide: Int) = ResolutionSelector.Builder()
        .setAllowedResolutionMode(ResolutionSelector.PREFER_HIGHER_RESOLUTION_OVER_CAPTURE_RATE)
        // No cap: ask for the short side at 16:9 and take the closest size the lens supports, up to its maximum.
        .setResolutionStrategy(ResolutionStrategy(Size(shortSide * 16 / 9, shortSide), ResolutionStrategy.FALLBACK_RULE_CLOSEST_LOWER_THEN_HIGHER)).build()

    @androidx.annotation.OptIn(ExperimentalCamera2Interop::class)
    private suspend fun bindCamera(key: BindKey) {
        val provider = withContext(Dispatchers.IO) { ProcessCameraProvider.getInstance(this@StreamService).get() }
        Pro.lenses.value = Pro.listLenses(provider.availableCameraInfos)
        // Manual settings and zoom belong to one lens: reset them only when the camera really changes.
        val resolved = key.lensId ?: Pro.lenses.value.firstOrNull { it.facing == (if (key.front) "front" else "back") }?.id
        if (boundLens != null && boundLens!!.second != resolved) { Live.zoom.value = 1f; Pro.resetManual() }
        boundLens = key.front to resolved
        val wanted = Pro.lenses.value.firstOrNull { it.id == key.lensId }
            ?.let { lens -> CameraSelector.Builder().addCameraFilter { infos -> infos.filter { Camera2CameraInfo.from(it).cameraId == lens.id } }.build() }
            ?: if (key.front) CameraSelector.DEFAULT_FRONT_CAMERA else CameraSelector.DEFAULT_BACK_CAMERA
        val other = if (key.front) CameraSelector.DEFAULT_BACK_CAMERA else CameraSelector.DEFAULT_FRONT_CAMERA
        // Camera effects (bokeh, night, HDR, face retouch) come from the phone maker, through CameraX Extensions.
        // They need the default front/back camera and a phone that allows frame access with the effect on.
        val base = if (key.front) CameraSelector.DEFAULT_FRONT_CAMERA else CameraSelector.DEFAULT_BACK_CAMERA
        val ext = runCatching { withContext(Dispatchers.IO) { ExtensionsManager.getInstanceAsync(this@StreamService, provider).get() } }.getOrNull()
        Pro.effects.value = if (ext == null || key.lensId != null) emptyList() else Pro.EFFECTS.filter { (_, mode) ->
            runCatching { ext.isExtensionAvailable(base, mode) && ext.isImageAnalysisSupported(base, mode) }.getOrDefault(false)
        }.map { it.first }
        val effectMode = Pro.EFFECTS.firstOrNull { it.first == key.effect && it.first in Pro.effects.value }?.second
        val selector = if (ext != null && effectMode != null) ext.getExtensionEnabledCameraSelector(base, effectMode) else wanted
        val h264 = key.codec != "jpeg" // any hardware video format
        // 10-bit HDR needs the camera to deliver HLG frames too.
        val hlg = runCatching { provider.getCameraInfo(base).querySupportedDynamicRanges(setOf(androidx.camera.core.DynamicRange.UNSPECIFIED)) }
            .getOrNull()?.contains(androidx.camera.core.DynamicRange.HLG_10_BIT) == true
        Pro.codecs.value = listOf("jpeg") + H264Encoder.supported().filter { it != "hevc-hdr" || hlg }
        // The phone's own screen gets a hardware preview (smooth, no work for the processor).
        // JPEG: frames for the PC come from an analysis stream at the full requested size.
        // H.264: the camera's video stream draws straight into the hardware encoder (zero copy).
        val pv = Preview.Builder().setResolutionSelector(sizeFor(1080)).setTargetRotation(deviceRotation).build()
        pv.surfaceProvider = Live.previewSurface.value
        val an = if (h264) null else ImageAnalysis.Builder().setResolutionSelector(sizeFor(key.quality))
            .setOutputImageFormat(ImageAnalysis.OUTPUT_IMAGE_FORMAT_RGBA_8888)
            .setBackpressureStrategy(ImageAnalysis.STRATEGY_KEEP_ONLY_LATEST).setTargetRotation(deviceRotation)
            .setOutputImageRotationEnabled(true).build().also { it.setAnalyzer(analysisExecutor, ::onFrame) }
        val previous = bound
        analysis = an
        previewUse = pv
        useH264 = h264
        zeroCopy = h264
        if (!h264) encoder?.let { encoder = null; it.release() }
        val useCases = mutableListOf<UseCase>(pv)
        an?.let { useCases += it }
        val videoFps = key.fps.takeIf { it > 0 } ?: 30
        if (h264) useCases += VideoCapture.Builder(VideoFeed(key.quality) { request, timebase ->
            val size = request.resolution
            val enc = runCatching { H264Encoder(size.width, size.height, videoFps, surfaceInput = true, kind = key.codec, onPacket = ::sendVideo) }.getOrNull()
            if (enc?.surface == null) { request.willNotProvideSurface(); Live.message.value = "This phone's video encoder couldn't start."; return@VideoFeed }
            // Frame timestamps on the uptime clock need shifting onto elapsedRealtime (they differ by deep-sleep time).
            enc.clockOffsetUs = if (timebase == androidx.camera.core.impl.Timebase.UPTIME)
                (android.os.SystemClock.elapsedRealtimeNanos() - System.nanoTime()) / 1000 else 0L
            netLog("video timebase $timebase, offset ${enc.clockOffsetUs} us")
            encoder?.release(); encoder = enc; needKey = true
            netLog("zero-copy ${key.codec} encoder ${size.width}x${size.height} @ $videoFps fps (asked ${key.quality}p; lens can do ${videoSizes.joinToString { "${it.width}x${it.height}" }}), intra refresh ${enc.intraRefresh}")
            request.provideSurface(enc.surface, encoderExecutor) { if (encoder === enc) encoder = null; enc.release() }
            lifecycleScope.launch { sendStatus() }
        }).setTargetFrameRate(android.util.Range(videoFps, videoFps))
            .apply { if (key.codec == "hevc-hdr") setDynamicRange(androidx.camera.core.DynamicRange.HLG_10_BIT) }
            .build()
        if (previous.isNotEmpty()) provider.unbind(*previous.toTypedArray()) // only our own streams; never the scanner's
        bound = useCases
        camera = runCatching { provider.bindToLifecycle(this, selector, *useCases.toTypedArray()) }
            .recoverCatching { provider.bindToLifecycle(this, wanted, *useCases.toTypedArray()) }
            .recoverCatching {
                if (!h264) throw it
                // This phone can't run the video stream next to the preview: fall back to copying full-size frames.
                netLog("zero-copy unavailable ($it); copying frames instead")
                zeroCopy = false
                provider.unbind(*useCases.toTypedArray())
                val full = ImageAnalysis.Builder().setResolutionSelector(sizeFor(key.quality))
                    .setOutputImageFormat(ImageAnalysis.OUTPUT_IMAGE_FORMAT_YUV_420_888)
                    .setBackpressureStrategy(ImageAnalysis.STRATEGY_KEEP_ONLY_LATEST).setTargetRotation(deviceRotation).build()
                full.setAnalyzer(analysisExecutor, ::onFrame)
                analysis = full
                bound = listOf(pv, full)
                provider.bindToLifecycle(this, wanted, pv, full)
            }
            .recoverCatching { provider.bindToLifecycle(this, other, *useCases.toTypedArray()) }.getOrNull()
        if (camera == null) Live.message.value = "Couldn't open the camera. Is another app using it?"
        readCapabilities()
        readVideoSizes()
        Pro.caps.value = camera?.cameraInfo?.let(Pro::readCaps)
        Live.hasTorch.value = camera?.cameraInfo?.hasFlashUnit()
        if (Live.hasTorch.value != true) Live.torch.value = false
        camera?.cameraControl?.enableTorch(Live.torch.value)
        camera?.cameraInfo?.zoomState?.value?.let { Live.zoomRange.value = it.minZoomRatio to it.maxZoomRatio }
        camera?.cameraControl?.setZoomRatio(Live.zoom.value)
        applyPro()
    }

    /** Pushes the pro settings (exposure, focus, white balance, frame rate, stabilization, HDR) to the camera. */
    @androidx.annotation.OptIn(ExperimentalCamera2Interop::class)
    private fun applyPro() {
        val cam = camera ?: return
        val c = Pro.caps.value ?: return sendStatus()
        runCatching { Camera2CameraControl.from(cam.cameraControl).captureRequestOptions = Pro.options(c) }
        if (c.evMax > c.evMin) cam.cameraControl.setExposureCompensationIndex(Pro.ev.value.coerceIn(c.evMin, c.evMax))
        sendStatus()
    }

    /** Focus and meter on a point of the picture as shown (0..1, already rotated upright): taps from the PC. */
    private fun focusAt(x: Float, y: Float) {
        val pv = previewUse ?: return
        val rot = sensorRotation()
        val (bx, by) = when (rot) { 90 -> y to 1 - x; 180 -> 1 - x to 1 - y; 270 -> 1 - y to x; else -> x to y }
        focusOn(SurfaceOrientedMeteringPointFactory(1f, 1f, pv).createPoint(bx, by))
    }

    private fun focusOn(point: androidx.camera.core.MeteringPoint) {
        val cam = camera ?: return
        Pro.focus.value = null
        cam.cameraControl.startFocusAndMetering(FocusMeteringAction.Builder(point).setAutoCancelDuration(5, TimeUnit.SECONDS).build())
    }

    /** How far frames from the sensor must turn to be upright, given how the phone is held. */
    private fun sensorRotation() = camera?.cameraInfo?.getSensorRotationDegrees(deviceRotation) ?: 0

    private fun sendVideo(packet: ByteArray, key: Boolean) {
        val socket = ws ?: return
        val enc = encoder
        val queued = socket.queueSize()
        val now = System.nanoTime()
        // Smart bitrate, at most twice a second: back off when the link can't keep up, creep back up when clear.
        if (enc != null && now - lastRateChange > 500_000_000L) {
            if (queued > 32 * 1024) { enc.setBitrate(enc.currentBitrate * 3 / 4); lastRateChange = now }
            else if (queued < 64 * 1024 && enc.currentBitrate < enc.maxBitrate) { enc.setBitrate(enc.currentBitrate * 9 / 8); lastRateChange = now }
        }
        // Keep the queue to about 300 ms of video (but always room for a whole keyframe): anything older is lag.
        val maxQueued = maxOf(256 * 1024, (enc?.currentBitrate ?: 3_000_000) / 8 * 3 / 10)
        if (queued > maxQueued) {
            // Too far behind: drop this frame, then resume from a keyframe. Ask for that keyframe only once a second;
            // asking on every packet turns every frame into a large keyframe and keeps the link clogged.
            needKey = true
            if (now - lastKeyRequest > 1_000_000_000L) { enc?.requestKeyframe(); lastKeyRequest = now }
            txDropped++
            return
        }
        if (needKey && !key) { txDropped++; return }
        if (needKey && now - lastKeyRequest > 1_000_000_000L) { enc?.requestKeyframe(); lastKeyRequest = now }
        needKey = false
        socket.send(packet.toByteString())
        txSent++
    }

    // What actually leaves the phone, reported to the PC as txFps (and logged in debug builds).
    @Volatile private var txSent = 0
    @Volatile private var txDropped = 0
    @Volatile private var txFps = 0
    private var lastRateChange = 0L
    private var lastKeyRequest = 0L

    @Volatile private var useH264 = false
    /** H.264 with the camera drawing straight into the encoder (no per-frame copying). */
    @Volatile private var zeroCopy = false

    /** H.264: hand the camera's YUV frame to the hardware encoder (made, or remade, to match the frame size). */
    private fun encodeFrame(image: ImageProxy) {
        var enc = encoder
        if (enc == null || enc.width != image.width || enc.height != image.height) {
            enc?.release()
            enc = runCatching { H264Encoder(image.width, image.height, Pro.fps.value.takeIf { it > 0 } ?: 30, kind = Pro.codec.value, onPacket = ::sendVideo) }.getOrNull()
            if (enc == null) { Live.message.value = "This phone's video encoder couldn't start."; return }
            encoder = enc; needKey = true
            lifecycleScope.launch { sendStatus() }
        }
        if (ws != null) enc.encode(image)
    }

    /** Video sizes this lens can stream as hardware video (CameraX's own table), largest first: "videoSizes" in status. */
    @Volatile private var videoSizes: List<android.util.Size> = emptyList()
    private fun readVideoSizes() {
        val info = camera?.cameraInfo ?: return
        videoSizes = runCatching {
            val caps = androidx.camera.video.Recorder.getVideoCapabilities(info)
            caps.getSupportedQualities(androidx.camera.core.DynamicRange.SDR).mapNotNull {
                androidx.camera.video.QualitySelector.getResolution(info, it)
            }.sortedByDescending { it.width * it.height }
        }.getOrDefault(emptyList())
        netLog("video sizes for this lens: ${videoSizes.joinToString { "${it.width}x${it.height}" }}")
    }

    /** Largest YUV size and highest frame rate this lens offers, reported to the PC as maxW/maxH/maxFps. */
    @androidx.annotation.OptIn(ExperimentalCamera2Interop::class)
    private fun readCapabilities() {
        val info = camera?.cameraInfo ?: return
        runCatching {
            val c2 = Camera2CameraInfo.from(info)
            val sizes = c2.getCameraCharacteristic(CameraCharacteristics.SCALER_STREAM_CONFIGURATION_MAP)?.getOutputSizes(ImageFormat.YUV_420_888)
            sizes?.maxByOrNull { it.width.toLong() * it.height }?.let { Live.maxSize.value = maxOf(it.width, it.height) to minOf(it.width, it.height) }
            c2.getCameraCharacteristic(CameraCharacteristics.CONTROL_AE_AVAILABLE_TARGET_FPS_RANGES)?.maxOfOrNull { it.upper }?.let { Live.maxFps.value = it }
        }
    }

    private var frameNo = 0L
    private fun onFrame(image: ImageProxy) {
        image.use {
            if (useH264) { if (!zeroCopy) encodeFrame(it); return } // the fallback copy path
            val socket = ws.takeIf { Pro.codec.value == "jpeg" }
            Live.frameSize.value = it.width to it.height // already upright: CameraX rotated it
            // Drop frames rather than lag behind: skip when the link is backed up or every compressor is busy.
            val canSend = socket != null && socket.queueSize() <= 512 * 1024 && compressors.tryAcquire()
            if (!canSend) return
            val bmp = it.toBitmap()
            run {
                // Compress on a small pool so several frames are in flight at once, then send them in order.
                val seq = nextSeq++
                jpegPool.execute {
                    val out = ByteArrayOutputStream(256 * 1024)
                    runCatching { bmp.compress(Bitmap.CompressFormat.JPEG, 85, out) }
                    compressors.release()
                    sendInOrder(seq, out.toByteArray())
                }
            }
        }
    }

    // JPEG pipeline: up to 3 frames compressing at once on separate cores, sent in capture order.
    private val jpegPool = Executors.newFixedThreadPool(3)
    private val compressors = java.util.concurrent.Semaphore(3)
    private var nextSeq = 0L
    private var sentSeq = -1L
    private val ready = java.util.TreeMap<Long, ByteArray>()

    private fun sendInOrder(seq: Long, jpeg: ByteArray) = synchronized(ready) {
        ready[seq] = jpeg
        // Send everything that is next in line; if a frame was dropped earlier, don't wait for it forever.
        while (ready.isNotEmpty()) {
            val first = ready.firstKey()
            if (first != sentSeq + 1 && ready.size < 3) break
            val data = ready.remove(first)!!
            if (first > sentSeq) { ws?.send(data.toByteString()); sentSeq = first; txSent++ }
        }
    }

    /** Camera timestamps on the monotonic clock need this to land on elapsedRealtime (the PC's reference). */
    @androidx.annotation.OptIn(ExperimentalCamera2Interop::class)
    private fun clockOffsetUs(): Long {
        val source = runCatching {
            Camera2CameraInfo.from(camera!!.cameraInfo).getCameraCharacteristic(CameraCharacteristics.SENSOR_INFO_TIMESTAMP_SOURCE)
        }.getOrNull()
        return if (source == CameraCharacteristics.SENSOR_INFO_TIMESTAMP_SOURCE_REALTIME) 0L
        else (android.os.SystemClock.elapsedRealtimeNanos() - System.nanoTime()) / 1000
    }

    private val REJECTED = "rejected"
    private fun netLog(msg: String) { if (applicationInfo.flags and android.content.pm.ApplicationInfo.FLAG_DEBUGGABLE != 0) android.util.Log.d("KissikNet", msg) }

    /** Connects (USB first when adb reverse is set up, else Wi-Fi) and reconnects quickly whenever the link drops. */
    private suspend fun connectLoop() {
        var backoff = 500L
        while (lifecycleScope.isActive) {
            val pairing = Pairing.load(this)
            if (pairing == null) { Live.message.value = "Not paired. Scan the code in KISSIK."; delay(2000); continue }
            val usb = usbReachable(pairing.port + 1)
            Live.link.value = Link.Connecting
            // TCP_NODELAY: send each frame at once instead of letting the OS batch small packets.
            val builder = OkHttpClient.Builder().pingInterval(5, TimeUnit.SECONDS).connectTimeout(4, TimeUnit.SECONDS)
                .socketFactory(NoDelaySocketFactory)
            if (!usb) {
                val trust = PinningTrust(pairing.pin) { fp -> pairing.copy(pin = fp).save(this) }
                builder.sslSocketFactory(trust.socketFactory(), trust).hostnameVerifier { _, _ -> true } // the pin is the check
            }
            val done = CompletableDeferred<String>()
            val request = Request.Builder().url(if (usb) pairing.usbUrl else pairing.wifiUrl).build()
            netLog("connect ${if (usb) "usb" else "wifi"} ${request.url.host}:${request.url.port}")
            attempt = builder.build().newWebSocket(request, object : WebSocketListener() {
                override fun onOpen(webSocket: WebSocket, response: Response) {
                    // Re-paired while this connect was in flight: drop it so the loop uses the new pairing.
                    if (Pairing.load(this@StreamService)?.token != pairing.token) { webSocket.cancel(); return }
                    netLog("open")
                    // Every connection starts in JPEG, which every KISSIK can show; the PC asks for H.264 if it decodes it.
                    Pro.codec.value = "jpeg"
                    ws = webSocket; backoff = 500L
                    needKey = true; encoder?.requestKeyframe()
                    Live.link.value = if (usb) Link.Usb else Link.Wifi
                    Live.message.value = if (usb) "Live in KISSIK over USB" else "Live in KISSIK over Wi-Fi"
                    updateNotification(Live.message.value)
                    sendStatus()
                    // On Wi-Fi: move to USB as soon as the cable is linked (adb reverse), since it's faster and steadier.
                    if (!usb) lifecycleScope.launch(Dispatchers.IO) {
                        while (ws === webSocket) {
                            delay(3000)
                            if (ws === webSocket && usbReachable(pairing.port + 1)) { netLog("usb appeared"); webSocket.close(1000, "switching to USB"); break }
                        }
                    }
                }
                override fun onMessage(webSocket: WebSocket, text: String) = onCommand(text)
                override fun onClosed(webSocket: WebSocket, code: Int, reason: String) { netLog("closed $code $reason"); done.complete("Disconnected.") }
                override fun onClosing(webSocket: WebSocket, code: Int, reason: String) { netLog("closing $code $reason"); webSocket.close(1000, null) }
                override fun onFailure(webSocket: WebSocket, t: Throwable, response: Response?) {
                    netLog("failure ${response?.code} $t")
                    val certProblem = generateSequence(t) { it.cause }.any { it.message?.contains("certificate", true) == true }
                    done.complete(when {
                        response?.code == 403 -> REJECTED
                        certProblem -> REJECTED
                        else -> "Can't reach your PC. Is KISSIK open, on the same Wi-Fi?"
                    })
                }
            })
            val why = done.await()
            ws = null
            if (why == REJECTED) {
                // Retrying a refused pairing is pointless, and KISSIK blocks devices that keep sending wrong links.
                // Wait quietly for the user to scan the new code.
                Live.link.value = Link.Rejected
                Live.message.value = "Scan the QR code in KISSIK again."
                updateNotification("Scan the QR code in KISSIK again")
                while (lifecycleScope.isActive && Pairing.load(this) == pairing) delay(1000)
                backoff = 500L
                continue
            }
            Live.link.value = Link.Connecting
            Live.message.value = "$why Retrying…"
            updateNotification("Reconnecting…")
            delay(backoff); backoff = minOf(backoff * 2, 3000L)
        }
    }

    private fun onCommand(text: String) {
        when {
            text == "front" -> { Live.front.value = true; Pro.lensId.value = null }
            text == "back" -> { Live.front.value = false; Pro.lensId.value = null }
            text == "key" -> { needKey = true; encoder?.requestKeyframe(); return }
            text.startsWith("focus:tap:") -> {
                val xy = text.removePrefix("focus:tap:").split(',').mapNotNull { it.toFloatOrNull() }
                if (xy.size == 2) Live.focusTap.tryEmit(xy[0].coerceIn(0f, 1f) to xy[1].coerceIn(0f, 1f))
                return
            }
            text == "mic:on" || text == "mic:off" -> Live.mic.value = text == "mic:on"
            Mic.command(text) -> return
            Pro.command(text) -> return
            text.startsWith("q:") -> text.drop(2).toIntOrNull()?.takeIf { it in 144..8640 }?.let { Live.quality.value = it }
            text == "torch:on" || text == "torch:off" -> { Live.torch.value = text == "torch:on"; return }
            text.startsWith("zoom:") -> {
                val info = camera?.cameraInfo?.zoomState?.value
                text.drop(5).toFloatOrNull()?.let { Live.zoom.value = it.coerceIn(info?.minZoomRatio ?: 1f, info?.maxZoomRatio ?: it) }
                return
            }
            else -> return
        }
        Live.save(this)
    }

    private fun battery(): Pair<Double?, Boolean?> {
        val i = registerReceiver(null, IntentFilter(Intent.ACTION_BATTERY_CHANGED)) ?: return null to null
        val level = i.getIntExtra(BatteryManager.EXTRA_LEVEL, -1); val scale = i.getIntExtra(BatteryManager.EXTRA_SCALE, -1)
        val status = i.getIntExtra(BatteryManager.EXTRA_STATUS, -1)
        return (if (level >= 0 && scale > 0) level / scale.toDouble() else null) to
            (status == BatteryManager.BATTERY_STATUS_CHARGING || status == BatteryManager.BATTERY_STATUS_FULL)
    }

    /** Battery/heat saver: drop to 480p when the phone runs hot, or battery is under 20% and not charging. */
    private fun checkSaver() {
        val (level, charging) = battery()
        val hot = Build.VERSION.SDK_INT >= 29 &&
            getSystemService(PowerManager::class.java).currentThermalStatus >= PowerManager.THERMAL_STATUS_MODERATE
        val low = level != null && level < 0.2 && charging != true
        Live.saving.value = Live.saver.value && (hot || low)
    }

    private fun sendStatus() {
        val debug = applicationInfo.flags and android.content.pm.ApplicationInfo.FLAG_DEBUGGABLE != 0
        val socket = ws
        if (socket == null && !debug) return
        val (level, charging) = battery()
        val (w, h) = Live.frameSize.value
        val json = JSONObject().put("device", "${Build.MANUFACTURER} ${Build.MODEL}".trim())
            .put("w", w).put("h", h).put("battery", level ?: JSONObject.NULL).put("charging", charging ?: JSONObject.NULL)
            .put("facing", if (Live.front.value) "front" else "back")
        // Lets the PC enable its Torch button and zoom slider only when this lens supports them.
        val info = camera?.cameraInfo
        json.put("torch", if (info?.hasFlashUnit() == true) Live.torch.value else JSONObject.NULL)
        val zs = info?.zoomState?.value
        zs?.let { Live.zoomRange.value = it.minZoomRatio to it.maxZoomRatio }
        val (mw, mh) = Live.maxSize.value
        if (mw > 0) json.put("maxW", mw).put("maxH", mh)
        if (Live.maxFps.value > 0) json.put("maxFps", Live.maxFps.value)
        json.put("zoomMax", zs?.maxZoomRatio?.toDouble() ?: JSONObject.NULL).put("zoom", Live.zoom.value.toDouble())
            .put("zoomMin", zs?.minZoomRatio?.toDouble() ?: JSONObject.NULL)
        // H.264 frames come straight from the sensor: the PC rotates them by "rot" (clockwise degrees). JPEGs are upright.
        val enc = encoder
        val rot = if (enc != null) sensorRotation() else 0 // H.264 frames come as the sensor sees them
        if (enc != null) json.put("w", if (rot % 180 == 0) enc.width else enc.height).put("h", if (rot % 180 == 0) enc.height else enc.width)
        json.put("rot", rot)
        // What hardware video this lens can actually do (the PC's size menu can follow this), largest first.
        json.put("videoSizes", org.json.JSONArray(videoSizes.map { "${maxOf(it.width, it.height)}x${minOf(it.width, it.height)}" }))
        videoSizes.firstOrNull()?.let { json.put("videoMaxW", maxOf(it.width, it.height)).put("videoMaxH", minOf(it.width, it.height)) }
        encoder?.let { json.put("intraRefresh", it.intraRefresh) }
        json.put("txFps", txFps).put("txQueueKB", (socket?.queueSize() ?: 0) / 1024)
        // Where the phone's share of the delay goes: sensor capture → encoded, then waiting in the send queue.
        encoder?.let { e ->
            e.captureToEncodedUs.averageMs()?.let { json.put("capEncMs", "%.1f".format(java.util.Locale.ROOT, it).toDouble()) }
            json.put("queueMs", ((socket?.queueSize() ?: 0) * 8000L / maxOf(1, e.currentBitrate)).toInt())
            json.put("ptsClock", "elapsedRealtime")
        }
        encoder?.let { json.put("bitrate", it.currentBitrate) }
        Pro.putStatus(json)
        Mic.putStatus(json)
        json.put("mic", Live.mic.value)
        if (debug) android.util.Log.d("KissikStatus", json.toString()) // debug builds: readable with adb even offline
        socket?.send(json.toString())
    }

    /** The mic is opened only while it's switched on and connected, and released straight after. */
    @SuppressLint("MissingPermission")
    private fun setMic(on: Boolean) {
        micJob?.cancel(); micJob = null
        if (!on || !hasMic()) return
        micJob = lifecycleScope.launch(Dispatchers.IO) {
            val rate = 48000
            val rec = Mic.open(this@StreamService, rate)
            if (rec == null) { Live.message.value = "Couldn't open the microphone."; return@launch }
            val samples = FloatArray(960) // 20 ms
            val packet = ByteBuffer.allocate(5 + samples.size * 4).order(ByteOrder.LITTLE_ENDIAN)
            try {
                rec.startRecording()
                while (isActive) {
                    val n = rec.read(samples, 0, samples.size, AudioRecord.READ_BLOCKING)
                    if (n <= 0) continue
                    packet.clear(); packet.put('A'.code.toByte()); packet.putInt(rate)
                    for (i in 0 until n) packet.putFloat(samples[i])
                    ws?.send(packet.array().toByteString(0, 5 + n * 4))
                }
            } finally { rec.stop(); rec.release(); Mic.close(this@StreamService) }
        }
    }

    override fun onDestroy() {
        Live.running.value = false; Live.hasTorch.value = null; Live.link.value = Link.Off; Live.message.value = ""; Live.torch.value = false
        ws?.close(1000, "stopped"); ws = null
        micJob?.cancel()
        // Release only the stream's own camera use: the scanner may already be using the camera.
        runCatching { ProcessCameraProvider.getInstance(this).get().unbind(*bound.toTypedArray()) }
        wake?.takeIf { it.isHeld }?.release(); wifiLock?.takeIf { it.isHeld }?.release()
        orientation?.disable()
        analysisExecutor.shutdown(); encoderExecutor.shutdown(); jpegPool.shutdown()
        encoder?.release(); encoder = null
        super.onDestroy()
    }
}
