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
 * Streams the camera (JPEG frames) and, when switched on, the mic to KISSIK on the PC. Runs as a foreground service so
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

        // Rebind the camera whenever the lens or the effective quality changes.
        lifecycleScope.launch {
            combine(Live.front, Live.quality, Live.saving) { f, q, s -> f to (if (s) minOf(q, 480) else q) }
                .distinctUntilChanged().collect { (f, q) -> bindCamera(f, q) }
        }
        lifecycleScope.launch { Live.torch.collect { camera?.cameraControl?.enableTorch(it); sendStatus() } }
        lifecycleScope.launch { Live.zoom.collect { camera?.cameraControl?.setZoomRatio(it); sendStatus() } }
        lifecycleScope.launch {
            combine(Live.mic, Live.link) { m, l -> m && (l == Link.Wifi || l == Link.Usb) }.distinctUntilChanged().collect { setMic(it) }
        }
        lifecycleScope.launch(Dispatchers.IO) { connectLoop() }
        lifecycleScope.launch { while (isActive) { checkSaver(); sendStatus(); delay(15_000) } }
    }

    private var boundFront: Boolean? = null
    private suspend fun bindCamera(front: Boolean, quality: Int) {
        if (boundFront != null && boundFront != front) Live.zoom.value = 1f
        boundFront = front
        val provider = withContext(Dispatchers.IO) { ProcessCameraProvider.getInstance(this@StreamService).get() }
        val long = when (quality) { 1080 -> 1920; 480 -> 640; else -> 1280 }
        val analysis = ImageAnalysis.Builder()
            .setResolutionSelector(ResolutionSelector.Builder().setResolutionStrategy(
                ResolutionStrategy(Size(long, quality), ResolutionStrategy.FALLBACK_RULE_CLOSEST_LOWER_THEN_HIGHER)).build())
            .setOutputImageFormat(ImageAnalysis.OUTPUT_IMAGE_FORMAT_RGBA_8888)
            .setBackpressureStrategy(ImageAnalysis.STRATEGY_KEEP_ONLY_LATEST).build()
        analysis.setAnalyzer(analysisExecutor, ::onFrame)
        val wanted = if (front) CameraSelector.DEFAULT_FRONT_CAMERA else CameraSelector.DEFAULT_BACK_CAMERA
        val other = if (front) CameraSelector.DEFAULT_BACK_CAMERA else CameraSelector.DEFAULT_FRONT_CAMERA
        provider.unbindAll()
        camera = runCatching { provider.bindToLifecycle(this, wanted, analysis) }
            .recoverCatching { provider.bindToLifecycle(this, other, analysis) }.getOrNull()
        if (camera == null) Live.message.value = "Couldn't open the camera. Is another app using it?"
        camera?.cameraControl?.enableTorch(Live.torch.value)
        camera?.cameraControl?.setZoomRatio(Live.zoom.value)
        sendStatus()
    }

    private var frameNo = 0L
    private fun onFrame(image: ImageProxy) {
        image.use {
            val socket = ws
            val wantPreview = frameNo++ % 3 == 0L
            val canSend = socket != null && socket.queueSize() <= 256 * 1024 // drop frames rather than lag behind
            if (!canSend && !wantPreview) return
            var bmp = it.toBitmap()
            val rot = it.imageInfo.rotationDegrees
            if (rot != 0) bmp = Bitmap.createBitmap(bmp, 0, 0, bmp.width, bmp.height, Matrix().apply { postRotate(rot.toFloat()) }, true)
            Live.frameSize.value = bmp.width to bmp.height
            if (wantPreview) Live.preview.value = bmp
            if (canSend) {
                val out = ByteArrayOutputStream(128 * 1024)
                bmp.compress(Bitmap.CompressFormat.JPEG, 80, out)
                socket!!.send(out.toByteArray().toByteString())
            }
        }
    }

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
            attempt = builder.build().newWebSocket(request, object : WebSocketListener() {
                override fun onOpen(webSocket: WebSocket, response: Response) {
                    // Re-paired while this connect was in flight: drop it so the loop uses the new pairing.
                    if (Pairing.load(this@StreamService)?.token != pairing.token) { webSocket.cancel(); return }
                    ws = webSocket; backoff = 500L
                    Live.link.value = if (usb) Link.Usb else Link.Wifi
                    Live.message.value = if (usb) "Live in KISSIK over USB" else "Live in KISSIK over Wi-Fi"
                    updateNotification(Live.message.value)
                    sendStatus()
                }
                override fun onMessage(webSocket: WebSocket, text: String) = onCommand(text)
                override fun onClosed(webSocket: WebSocket, code: Int, reason: String) { done.complete("Disconnected.") }
                override fun onFailure(webSocket: WebSocket, t: Throwable, response: Response?) {
                    val certProblem = generateSequence(t) { it.cause }.any { it.message?.contains("certificate", true) == true }
                    done.complete(when {
                        response?.code == 403 -> "KISSIK made a new pairing. Scan the code again."
                        certProblem -> "Your PC's certificate changed. Scan the code in KISSIK again."
                        else -> "Can't reach your PC. Is KISSIK open, on the same Wi-Fi?"
                    })
                }
            })
            val why = done.await()
            ws = null
            Live.link.value = Link.Connecting
            Live.message.value = "$why Retrying…"
            updateNotification("Reconnecting…")
            delay(backoff); backoff = minOf(backoff * 2, 3000L)
        }
    }

    private fun onCommand(text: String) {
        when {
            text == "front" -> Live.front.value = true
            text == "back" -> Live.front.value = false
            text.startsWith("q:") -> text.drop(2).toIntOrNull()?.takeIf { it in setOf(480, 720, 1080) }?.let { Live.quality.value = it }
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
        json.put("zoomMax", zs?.maxZoomRatio?.toDouble() ?: JSONObject.NULL).put("zoom", Live.zoom.value.toDouble())
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
        Live.running.value = false; Live.link.value = Link.Off; Live.message.value = ""; Live.torch.value = false
        Live.preview.value = null
        ws?.close(1000, "stopped"); ws = null
        micJob?.cancel()
        runCatching { ProcessCameraProvider.getInstance(this).get().unbindAll() }
        wake?.takeIf { it.isHeld }?.release(); wifiLock?.takeIf { it.isHeld }?.release()
        analysisExecutor.shutdown()
        super.onDestroy()
    }
}
