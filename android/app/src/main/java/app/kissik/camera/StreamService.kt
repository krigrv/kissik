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
import androidx.camera.core.Preview
import androidx.camera.core.SurfaceOrientedMeteringPointFactory
import androidx.camera.core.UseCase
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
            combine(Live.front, Pro.lensId, Live.quality, Live.saving, Pro.codec) { f, l, q, s, c -> BindKey(f, l, if (s) minOf(q, 480) else q, c) }
                .distinctUntilChanged().collect { bindCamera(it) }
        }
        lifecycleScope.launch { merge(Pro.ev, Pro.iso, Pro.shutterNs, Pro.focus, Pro.wb, Pro.fps, Pro.stab, Pro.hdr).collect { applyPro() } }
        lifecycleScope.launch { Live.focusTap.collect { (x, y) -> focusAt(x, y) } }
        lifecycleScope.launch { Live.torch.collect { camera?.cameraControl?.enableTorch(it); sendStatus() } }
        lifecycleScope.launch { Live.zoom.collect { camera?.cameraControl?.setZoomRatio(it); sendStatus() } }
        lifecycleScope.launch {
            combine(Live.mic, Live.link) { m, l -> m && (l == Link.Wifi || l == Link.Usb) }.distinctUntilChanged().collect { setMic(it) }
        }
        // Keep the picture upright as the phone turns: frames are rotated by sensor angle + how the phone is held.
        orientation = object : OrientationEventListener(this) {
            override fun onOrientationChanged(deg: Int) {
                if (deg == ORIENTATION_UNKNOWN) return
                val r = when (deg) { in 45..134 -> Surface.ROTATION_270; in 135..224 -> Surface.ROTATION_180; in 225..314 -> Surface.ROTATION_90; else -> Surface.ROTATION_0 }
                if (r == deviceRotation) return
                deviceRotation = r
                analysis?.targetRotation = r
                lifecycleScope.launch { delay(400); sendStatus() } // after a frame at the new size
            }
        }.apply { if (canDetectOrientation()) enable() }
        lifecycleScope.launch(Dispatchers.IO) { connectLoop() }
        lifecycleScope.launch { while (isActive) { checkSaver(); sendStatus(); delay(15_000) } }
    }

    private data class BindKey(val front: Boolean, val lensId: String?, val quality: Int, val codec: String)
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
        val h264 = key.codec == "h264"
        // JPEG: frames come from the analysis stream at full size. H.264: the camera draws straight into the hardware
        // encoder, and a small analysis stream only feeds the on-phone preview.
        val an = ImageAnalysis.Builder().setResolutionSelector(sizeFor(if (h264) 480 else key.quality))
            .setOutputImageFormat(ImageAnalysis.OUTPUT_IMAGE_FORMAT_RGBA_8888)
            .setBackpressureStrategy(ImageAnalysis.STRATEGY_KEEP_ONLY_LATEST).setTargetRotation(deviceRotation).build()
        an.setAnalyzer(analysisExecutor, ::onFrame)
        analysis = an
        val useCases = mutableListOf<UseCase>(an)
        if (h264) {
            val preview = Preview.Builder().setResolutionSelector(sizeFor(key.quality)).build()
            preview.setSurfaceProvider(encoderExecutor) { request ->
                val size = request.resolution
                val enc = runCatching { H264Encoder(size.width, size.height, Pro.fps.value.takeIf { it > 0 } ?: 30, ::sendVideo) }.getOrNull()
                if (enc == null) { request.willNotProvideSurface(); Live.message.value = "This phone's video encoder couldn't start."; return@setSurfaceProvider }
                encoder = enc; needKey = true
                request.provideSurface(enc.surface, encoderExecutor) { if (encoder === enc) encoder = null; enc.release() }
            }
            useCases += preview
        }
        provider.unbindAll()
        camera = runCatching { provider.bindToLifecycle(this, wanted, *useCases.toTypedArray()) }
            .recoverCatching { provider.bindToLifecycle(this, other, *useCases.toTypedArray()) }.getOrNull()
        if (camera == null) Live.message.value = "Couldn't open the camera. Is another app using it?"
        readCapabilities()
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

    /** Focus and meter on a point of the picture as shown (0..1, already rotated upright). */
    private fun focusAt(x: Float, y: Float) {
        val cam = camera ?: return
        val an = analysis ?: return
        val (bx, by) = when (lastRotation) { 90 -> y to 1 - x; 180 -> 1 - x to 1 - y; 270 -> 1 - y to x; else -> x to y }
        Pro.focus.value = null
        val point = SurfaceOrientedMeteringPointFactory(1f, 1f, an).createPoint(bx, by)
        cam.cameraControl.startFocusAndMetering(FocusMeteringAction.Builder(point).setAutoCancelDuration(5, TimeUnit.SECONDS).build())
    }

    private fun sendVideo(packet: ByteArray, key: Boolean) {
        val socket = ws ?: return
        if (needKey && !key) return
        if (socket.queueSize() > 4 * 1024 * 1024) { needKey = true; encoder?.requestKeyframe(); return }
        needKey = false
        socket.send(packet.toByteString())
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
            val socket = ws.takeIf { encoder == null && Pro.codec.value == "jpeg" }
            lastRotation = it.imageInfo.rotationDegrees
            val wantPreview = frameNo++ % 3 == 0L
            val canSend = socket != null && socket.queueSize() <= 256 * 1024 // drop frames rather than lag behind
            if (!canSend && !wantPreview) return
            var bmp = it.toBitmap()
            val rot = it.imageInfo.rotationDegrees
            if (rot != 0) bmp = Bitmap.createBitmap(bmp, 0, 0, bmp.width, bmp.height, Matrix().apply { postRotate(rot.toFloat()) }, true)
            if (encoder == null) Live.frameSize.value = bmp.width to bmp.height
            if (wantPreview) Live.preview.value = bmp
            if (canSend) {
                val out = ByteArrayOutputStream(128 * 1024)
                bmp.compress(Bitmap.CompressFormat.JPEG, 80, out)
                socket!!.send(out.toByteArray().toByteString())
            }
        }
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
            val builder = OkHttpClient.Builder().pingInterval(5, TimeUnit.SECONDS).connectTimeout(4, TimeUnit.SECONDS)
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
        val socket = ws ?: return
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
        val rot = if (enc != null) camera?.cameraInfo?.getSensorRotationDegrees(deviceRotation) ?: 0 else 0
        if (enc != null) json.put("w", if (rot % 180 == 0) enc.width else enc.height).put("h", if (rot % 180 == 0) enc.height else enc.width)
        json.put("rot", rot)
        Pro.putStatus(json)
        if (applicationInfo.flags and android.content.pm.ApplicationInfo.FLAG_DEBUGGABLE != 0) android.util.Log.d("KissikStatus", json.toString())
        socket.send(json.toString())
    }

    /** The mic is opened only while it's switched on and connected, and released straight after. */
    @SuppressLint("MissingPermission")
    private fun setMic(on: Boolean) {
        micJob?.cancel(); micJob = null
        if (!on || !hasMic()) return
        micJob = lifecycleScope.launch(Dispatchers.IO) {
            val rate = 48000
            val min = AudioRecord.getMinBufferSize(rate, AudioFormat.CHANNEL_IN_MONO, AudioFormat.ENCODING_PCM_FLOAT)
            val rec = runCatching {
                AudioRecord(MediaRecorder.AudioSource.MIC, rate, AudioFormat.CHANNEL_IN_MONO, AudioFormat.ENCODING_PCM_FLOAT, maxOf(min, 4 * 4800))
            }.getOrNull()
            if (rec == null || rec.state != AudioRecord.STATE_INITIALIZED) { Live.message.value = "Couldn't open the microphone."; rec?.release(); return@launch }
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
            } finally { rec.stop(); rec.release() }
        }
    }

    override fun onDestroy() {
        Live.running.value = false; Live.hasTorch.value = null; Live.link.value = Link.Off; Live.message.value = ""; Live.torch.value = false
        Live.preview.value = null
        ws?.close(1000, "stopped"); ws = null
        micJob?.cancel()
        runCatching { ProcessCameraProvider.getInstance(this).get().unbindAll() }
        wake?.takeIf { it.isHeld }?.release(); wifiLock?.takeIf { it.isHeld }?.release()
        orientation?.disable()
        analysisExecutor.shutdown(); encoderExecutor.shutdown()
        super.onDestroy()
    }
}
