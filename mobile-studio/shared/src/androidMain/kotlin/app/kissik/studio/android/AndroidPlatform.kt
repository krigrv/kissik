package app.kissik.studio.android

import android.app.Activity
import android.content.ActivityNotFoundException
import android.content.Context
import android.content.Intent
import android.graphics.Bitmap
import android.graphics.BitmapFactory
import android.media.MediaMetadataRetriever
import android.net.Uri
import android.os.Build
import android.os.Bundle
import android.provider.Settings as SysSettings
import android.speech.RecognitionListener
import android.speech.RecognizerIntent
import android.speech.SpeechRecognizer
import androidx.activity.ComponentActivity
import androidx.activity.result.ActivityResultLauncher
import androidx.activity.result.IntentSenderRequest
import androidx.activity.result.contract.ActivityResultContract
import androidx.activity.result.contract.ActivityResultContracts
import androidx.compose.foundation.layout.*
import androidx.compose.material3.Button
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.runtime.collectAsState
import androidx.compose.runtime.getValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.text.style.TextAlign
import androidx.compose.ui.unit.dp
import androidx.core.content.FileProvider
import app.kissik.studio.ThemeChoice
import app.kissik.studio.captions.CaptionEngineKind
import app.kissik.studio.image.Frame
import app.kissik.studio.platform.*
import app.kissik.studio.ui.KissikLogo
import app.kissik.studio.ui.KissikTheme
import com.google.android.gms.tasks.Tasks
import com.google.mlkit.vision.common.InputImage
import com.google.mlkit.vision.documentscanner.GmsDocumentScannerOptions
import com.google.mlkit.vision.documentscanner.GmsDocumentScanning
import com.google.mlkit.vision.documentscanner.GmsDocumentScanningResult
import com.google.mlkit.vision.text.TextRecognition
import com.google.mlkit.vision.text.latin.TextRecognizerOptions
import kotlinx.coroutines.flow.MutableSharedFlow
import java.io.File
import java.text.SimpleDateFormat
import java.util.Date
import java.util.Locale
import java.util.TimeZone
import java.util.concurrent.atomic.AtomicInteger

/** Wires every Android service together for the shared app. */
class AndroidPlatform(private val activity: ComponentActivity) : StudioPlatform {
    override val platformName = "Android"
    override val store = PrefsStore(activity)
    override val system = AndroidSystem(activity)
    private val androidAudio = AndroidAudio(activity.applicationContext)
    private val androidCamera = AndroidCamera(activity) { system.reducedMotion }
    private val androidMedia = AndroidMedia(activity, androidCamera, androidAudio)
    private val androidLive = AndroidLive(androidCamera, androidAudio)
    private val androidWebcam = AndroidWebcam(activity.applicationContext, androidCamera, androidAudio, store)
    override val camera: CameraService = androidCamera
    override val audio: AudioService = androidAudio
    override val media: MediaService = androidMedia
    override val live: LiveService = androidLive
    override val webcam: WebcamService = androidWebcam
    override val captions = AndroidCaptions(activity, androidAudio)
    override val files = AndroidFiles(activity)
    override val scanner = AndroidScanner(activity)

    /** The app left the screen / came back. */
    fun background() = androidMedia.onBackground()
    fun foreground() = androidMedia.onForeground()

    fun release() {
        androidMedia.release(); androidLive.stop(); androidWebcam.disconnect(); captions.stop()
        androidCamera.release(); androidAudio.release()
    }
}

/** One-shot launcher via the activity's result registry (usable any time, not only during onCreate). */
private val keys = AtomicInteger()
fun <I, O> ComponentActivity.launchOnce(contract: ActivityResultContract<I, O>, input: I, onResult: (O) -> Unit) {
    var l: ActivityResultLauncher<I>? = null
    l = activityResultRegistry.register("kissik-${keys.incrementAndGet()}", contract) { r -> l?.unregister(); onResult(r) }
    l.launch(input)
}

class PrefsStore(ctx: Context) : KeyValueStore {
    private val p = ctx.getSharedPreferences("kissik", Context.MODE_PRIVATE)
    override fun get(key: String): String? = p.getString(key, null)
    override fun put(key: String, value: String?) { p.edit().apply { if (value == null) remove(key) else putString(key, value) }.apply() }
}

// ── Crash log ────────────────────────────────────────────────────────────────

object CrashLog {
    fun file(ctx: Context) = File(ctx.filesDir, "crash.log")
    fun install(ctx: Context) {
        val prev = Thread.getDefaultUncaughtExceptionHandler()
        Thread.setDefaultUncaughtExceptionHandler { t, e ->
            runCatching {
                val iso = SimpleDateFormat("yyyy-MM-dd'T'HH:mm:ss'Z'", Locale.US).apply { timeZone = TimeZone.getTimeZone("UTC") }.format(Date())
                file(ctx).appendText("$iso\n${e.stackTraceToString()}\n")
            }
            prev?.uncaughtException(t, e)
        }
    }
}

class AndroidSystem(private val activity: Activity) : SystemService {
    override val appVersion: String = runCatching { activity.packageManager.getPackageInfo(activity.packageName, 0).versionName }.getOrNull() ?: "0"
    override val osName = "Android"
    override val osVersion: String = Build.VERSION.RELEASE
    override val deviceName: String = runCatching { SysSettings.Global.getString(activity.contentResolver, "device_name") }.getOrNull() ?: Build.MODEL
    override val reducedMotion: Boolean get() = SysSettings.Global.getFloat(activity.contentResolver, SysSettings.Global.ANIMATOR_DURATION_SCALE, 1f) == 0f
    override val highContrast: Boolean get() = runCatching { SysSettings.Secure.getInt(activity.contentResolver, "high_text_contrast_enabled", 0) == 1 }.getOrDefault(false)
    override fun readCrashLog(): String? = CrashLog.file(activity).takeIf { it.exists() }?.readText()
    override fun clearCrashLog() { CrashLog.file(activity).delete() }
    override fun postCrash(url: String, auth: String, envelope: String, done: (Boolean) -> Unit) {
        Thread {
            val ok = runCatching {
                val c = java.net.URL(url).openConnection() as java.net.HttpURLConnection
                c.requestMethod = "POST"; c.doOutput = true; c.connectTimeout = 15000; c.readTimeout = 15000
                c.setRequestProperty("Content-Type", "application/x-sentry-envelope"); c.setRequestProperty("X-Sentry-Auth", auth)
                c.outputStream.use { it.write(envelope.toByteArray()) }
                val code = c.responseCode; c.disconnect(); code in 200..299
            }.getOrDefault(false)
            activity.runOnUiThread { done(ok) }
        }.start()
    }
    override fun nowIso(): String = SimpleDateFormat("yyyy-MM-dd'T'HH:mm:ss'Z'", Locale.US).apply { timeZone = TimeZone.getTimeZone("UTC") }.format(Date())
    override fun nowMs() = System.currentTimeMillis()
    @Composable override fun OnBack(enabled: Boolean, action: () -> Unit) = androidx.activity.compose.BackHandler(enabled, action)
    override fun openPrivacySettings() {
        activity.startActivity(Intent(SysSettings.ACTION_APPLICATION_DETAILS_SETTINGS, Uri.parse("package:${activity.packageName}")))
    }
}

// ── Files and backgrounds ────────────────────────────────────────────────────

class AndroidFiles(private val activity: ComponentActivity) : FileService {
    override fun pickBackground(video: Boolean, onPicked: (BackgroundSource?) -> Unit) {
        activity.launchOnce(ActivityResultContracts.GetContent(), if (video) "video/*" else "image/*") { uri ->
            if (uri == null) { onPicked(null); return@launchOnce }
            Thread {
                val src = runCatching { if (video) VideoBackground(activity, uri) else stillBackground(uri) }.getOrNull()
                activity.runOnUiThread { onPicked(src) }
            }.start()
        }
    }

    private fun stillBackground(uri: Uri): BackgroundSource {
        val opts = BitmapFactory.Options().apply { inSampleSize = 2 }
        val bmp = activity.contentResolver.openInputStream(uri)!!.use { BitmapFactory.decodeStream(it, null, opts) }!!
        val f = bmp.toFrame(960)
        return object : BackgroundSource {
            override val label = "Picture"
            override fun frameAt(timeMs: Long) = f
            override fun close() {}
        }
    }

    override fun pickLut(onPicked: (Pair<String, String>?) -> Unit) {
        activity.launchOnce(ActivityResultContracts.OpenDocument(), arrayOf("*/*")) { uri ->
            onPicked(uri?.let { u ->
                runCatching { (u.lastPathSegment?.substringAfterLast('/') ?: "LUT") to activity.contentResolver.openInputStream(u)!!.bufferedReader().readText() }.getOrNull()
            })
        }
    }

    override fun readAppFile(name: String) = File(activity.filesDir, name).takeIf { it.exists() }?.readText()
    override fun writeAppFile(name: String, text: String) = File(activity.filesDir, name).writeText(text)
}

fun Bitmap.toFrame(maxSide: Int): Frame {
    val s = minOf(1f, maxSide.toFloat() / maxOf(width, height))
    val b = if (s < 1f) Bitmap.createScaledBitmap(this, (width * s).toInt(), (height * s).toInt(), true) else this
    val px = IntArray(b.width * b.height); b.getPixels(px, 0, b.width, 0, 0, b.width, b.height)
    return Frame(b.width, b.height, px)
}

/** A looping video background: frames pre-decoded at ~12 fps (up to 10 s) so playback costs nothing per frame. */
private class VideoBackground(ctx: Context, uri: Uri) : BackgroundSource {
    override val label = "Looping video"
    private val frames: List<Frame>
    private val stepMs = 83L
    init {
        val r = MediaMetadataRetriever(); r.setDataSource(ctx, uri)
        val dur = (r.extractMetadata(MediaMetadataRetriever.METADATA_KEY_DURATION)?.toLong() ?: 0L).coerceAtMost(10_000L)
        frames = (0 until (dur / stepMs).coerceAtLeast(1)).mapNotNull { i ->
            r.getScaledFrameAtTime(i * stepMs * 1000, MediaMetadataRetriever.OPTION_CLOSEST_SYNC, 640, 640)?.toFrame(640)
        }
        r.release()
        require(frames.isNotEmpty())
    }
    override fun frameAt(timeMs: Long) = frames[((timeMs / stepMs) % frames.size).toInt()]
    override fun close() {}
}

// ── Captions ─────────────────────────────────────────────────────────────────

/**
 * Captions. Fast: the phone's SpeechRecognizer (its on-device recogniser when available). Accurate: whisper.cpp on the
 * phone with the same ggml-base model as the desktop, downloaded once on first use.
 */
class AndroidCaptions(private val activity: Activity, private val audio: AndroidAudio) : CaptionService {
    companion object {
        const val MODEL_FILE = "ggml-base.bin"
        const val MODEL_URL = "https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-base.bin"
    }
    override val events = MutableSharedFlow<CaptionEvent>(extraBufferCapacity = 32)
    private var rec: SpeechRecognizer? = null
    private var running = false
    private var language = "en-US"
    private var useOnDevice = true

    private fun onDevice() = useOnDevice && Build.VERSION.SDK_INT >= 31 && SpeechRecognizer.isOnDeviceRecognitionAvailable(activity)
    private fun modelFile() = File(activity.filesDir, "whisper/$MODEL_FILE")
    override fun isAvailable(kind: CaptionEngineKind) = unavailableReason(kind) == null
    override fun unavailableReason(kind: CaptionEngineKind): String? = when (kind) {
        CaptionEngineKind.Fast -> if (SpeechRecognizer.isRecognitionAvailable(activity) || onDevice()) null else "This phone has no speech recognition service"
        CaptionEngineKind.Accurate -> null
    }

    override fun start(kind: CaptionEngineKind, language: String) {
        this.language = language; running = true
        if (kind == CaptionEngineKind.Accurate) startWhisper() else activity.runOnUiThread { listen() }
    }

    // ── Fast: SpeechRecognizer ──────────────────────────────────────────────
    private fun intent() = Intent(RecognizerIntent.ACTION_RECOGNIZE_SPEECH).apply {
        putExtra(RecognizerIntent.EXTRA_LANGUAGE_MODEL, RecognizerIntent.LANGUAGE_MODEL_FREE_FORM)
        putExtra(RecognizerIntent.EXTRA_LANGUAGE, language)
        putExtra(RecognizerIntent.EXTRA_PARTIAL_RESULTS, true)
        putExtra(RecognizerIntent.EXTRA_PREFER_OFFLINE, true)
    }

    private fun listen() {
        if (!running) return
        rec?.destroy()
        val r = if (onDevice()) SpeechRecognizer.createOnDeviceSpeechRecognizer(activity) else SpeechRecognizer.createSpeechRecognizer(activity)
        rec = r
        r.setRecognitionListener(object : RecognitionListener {
            override fun onPartialResults(b: Bundle) { b.getStringArrayList(SpeechRecognizer.RESULTS_RECOGNITION)?.firstOrNull()?.let { events.tryEmit(CaptionEvent.Partial(it)) } }
            override fun onResults(b: Bundle) { b.getStringArrayList(SpeechRecognizer.RESULTS_RECOGNITION)?.firstOrNull()?.let { events.tryEmit(CaptionEvent.Final(it)) }; listen() }
            override fun onError(error: Int) {
                when (error) {
                    SpeechRecognizer.ERROR_NO_MATCH, SpeechRecognizer.ERROR_SPEECH_TIMEOUT, SpeechRecognizer.ERROR_RECOGNIZER_BUSY -> activity.window.decorView.postDelayed({ listen() }, 300)
                    SpeechRecognizer.ERROR_INSUFFICIENT_PERMISSIONS -> { running = false; events.tryEmit(CaptionEvent.Error("Captions need microphone permission")) }
                    SpeechRecognizer.ERROR_LANGUAGE_NOT_SUPPORTED, SpeechRecognizer.ERROR_LANGUAGE_UNAVAILABLE -> {
                        // The on-device pack for this language isn't installed: ask the system to fetch it and use the
                        // phone's regular recogniser meanwhile.
                        if (useOnDevice && Build.VERSION.SDK_INT >= 33) runCatching { r.triggerModelDownload(intent()) }
                        if (useOnDevice) { useOnDevice = false; events.tryEmit(CaptionEvent.Partial("Getting the speech pack for $language…")); listen() }
                        else { running = false; events.tryEmit(CaptionEvent.Error("Captions aren't available for $language on this phone")) }
                    }
                    else -> { running = false; events.tryEmit(CaptionEvent.Error("Captions stopped (speech error $error)")) }
                }
            }
            override fun onReadyForSpeech(p: Bundle?) {}
            override fun onBeginningOfSpeech() {}
            override fun onRmsChanged(v: Float) {}
            override fun onBufferReceived(b: ByteArray?) {}
            override fun onEndOfSpeech() {}
            override fun onEvent(t: Int, p: Bundle?) {}
        })
        r.startListening(intent())
    }

    // ── Accurate: whisper.cpp ───────────────────────────────────────────────
    private var whisper: app.kissik.whisper.Whisper? = null
    private var pcm: AndroidAudio.PcmListener? = null
    private val worker = java.util.concurrent.Executors.newSingleThreadExecutor()
    @Volatile private var busy = false

    private fun startWhisper() = worker.execute {
        try {
            val f = modelFile()
            if (!f.exists() && download(f) == null) return@execute
            events.tryEmit(CaptionEvent.Partial("Loading the speech model…"))
            val w = whisper ?: app.kissik.whisper.Whisper.load(f.path)
                ?: run { events.tryEmit(CaptionEvent.Error("The speech model couldn't be loaded")); return@execute }
            whisper = w
            if (!running) return@execute
            events.tryEmit(CaptionEvent.Partial(""))
            val window = WhisperWindow()
            pcm = AndroidAudio.PcmListener { p, n, _ ->
                val chunk = window.add(p, n) ?: return@PcmListener
                if (busy) return@PcmListener // still working on the last window: skip rather than fall behind
                busy = true
                worker.execute {
                    try {
                        val text = w.transcribe(chunk, language.substringBefore('-'), threads = 4)
                        if (running && text.isNotBlank() && !text.startsWith("[")) events.tryEmit(CaptionEvent.Final(text))
                    } finally { busy = false }
                }
            }.also { audio.addListener(it) }
        } catch (t: Throwable) { events.tryEmit(CaptionEvent.Error("Accurate captions failed: ${t.message}")) }
    }

    /** Downloads the model with progress; returns the file, or null after reporting the error. */
    private fun download(f: File): File? {
        f.parentFile?.mkdirs()
        val tmp = File(f.path + ".part")
        return runCatching {
            val c = java.net.URL(MODEL_URL).openConnection() as java.net.HttpURLConnection
            c.connectTimeout = 20000; c.readTimeout = 30000
            val total = c.contentLengthLong
            var got = 0L; var lastPct = -1
            c.inputStream.use { inp -> tmp.outputStream().use { out ->
                val buf = ByteArray(1 shl 16)
                while (running) {
                    val n = inp.read(buf); if (n < 0) break
                    out.write(buf, 0, n); got += n
                    val pct = if (total > 0) (got * 100 / total).toInt() else -1
                    if (pct != lastPct) { lastPct = pct; events.tryEmit(CaptionEvent.Partial("Downloading the speech model (one time, ${total / 1_000_000} MB): $pct%")) }
                }
            } }
            if (!running) { tmp.delete(); return null }
            check(total <= 0 || got == total) { "download incomplete" }
            tmp.renameTo(f); f
        }.getOrElse { tmp.delete(); events.tryEmit(CaptionEvent.Error("Couldn't download the speech model: ${it.message}")); null }
    }

    override fun stop() {
        running = false
        pcm?.let { audio.removeListener(it) }; pcm = null
        activity.runOnUiThread { rec?.destroy(); rec = null }
    }
}

/** Collects 48 kHz mic audio into 4-second 16 kHz windows (0.5 s overlap) for Whisper; quiet windows are skipped. */
class WhisperWindow(seconds: Float = 4f) {
    private val size = (16000 * seconds).toInt()
    private val buf = FloatArray(size)
    private var n = 0
    fun add(pcm: ShortArray, count: Int): FloatArray? {
        var i = 0
        while (i + 2 < count) {
            if (n < size) buf[n++] = (pcm[i] + pcm[i + 1] + pcm[i + 2]) / (3f * 32768f)
            i += 3
        }
        if (n < size) return null
        val out = buf.copyOf()
        val keep = 8000 // 0.5 s carried over so words on the boundary aren't cut in half
        System.arraycopy(buf, size - keep, buf, 0, keep); n = keep
        return if (out.maxOf { kotlin.math.abs(it) } < 0.01f) null else out
    }
}

// ── Document scanner ─────────────────────────────────────────────────────────

class AndroidScanner(private val activity: ComponentActivity) : ScannerService {
    override fun scan(onResult: (ScanResult?) -> Unit) {
        val opts = GmsDocumentScannerOptions.Builder().setGalleryImportAllowed(true).setPageLimit(20)
            .setResultFormats(GmsDocumentScannerOptions.RESULT_FORMAT_JPEG, GmsDocumentScannerOptions.RESULT_FORMAT_PDF)
            .setScannerMode(GmsDocumentScannerOptions.SCANNER_MODE_FULL).build()
        GmsDocumentScanning.getClient(opts).getStartScanIntent(activity)
            .addOnSuccessListener { sender ->
                activity.launchOnce(ActivityResultContracts.StartIntentSenderForResult(), IntentSenderRequest.Builder(sender).build()) { r ->
                    val res = GmsDocumentScanningResult.fromActivityResultIntent(r.data)
                    if (r.resultCode != Activity.RESULT_OK || res == null) { onResult(null); return@launchOnce }
                    val pages = res.pages.orEmpty().mapIndexed { i, p ->
                        val f = File(activity.filesDir, "scan-$i.jpg"); activity.contentResolver.openInputStream(p.imageUri)!!.use { s -> f.outputStream().use { s.copyTo(it) } }
                        val o = BitmapFactory.Options().apply { inJustDecodeBounds = true }; BitmapFactory.decodeFile(f.path, o)
                        ScannedPage(f.path, o.outWidth, o.outHeight)
                    }
                    val pdf = res.pdf?.let { pd -> File(activity.filesDir, "KISSIK-scan.pdf").also { f -> activity.contentResolver.openInputStream(pd.uri)!!.use { s -> f.outputStream().use { s.copyTo(it) } } }.path }
                    onResult(ScanResult(pages, pdf))
                }
            }
            .addOnFailureListener { onResult(null); activity.runOnUiThread { android.widget.Toast.makeText(activity, "The scanner isn't available: ${it.message}", android.widget.Toast.LENGTH_LONG).show() } }
    }

    override fun readCodesFromPicture(onCodes: (List<String>?) -> Unit) {
        activity.launchOnce(ActivityResultContracts.GetContent(), "image/*") { uri ->
            if (uri == null) { onCodes(null); return@launchOnce }
            com.google.mlkit.vision.barcode.BarcodeScanning.getClient().process(InputImage.fromFilePath(activity, uri))
                .addOnSuccessListener { list -> onCodes(list.mapNotNull { it.rawValue }) }
                .addOnFailureListener { onCodes(emptyList()) }
        }
    }

    override fun recognizeText(page: ScannedPage, onText: (String?) -> Unit) {
        Thread {
            val t = runCatching {
                val bmp = BitmapFactory.decodeFile(page.path)
                Tasks.await(TextRecognition.getClient(TextRecognizerOptions.DEFAULT_OPTIONS).process(InputImage.fromBitmap(bmp, 0))).text
            }.getOrNull()
            activity.runOnUiThread { onText(t) }
        }.start()
    }

    override fun share(path: String, mime: String) {
        val f = File(path).let { if (it.isAbsolute) it else File(activity.filesDir, path) }
        val uri = FileProvider.getUriForFile(activity, "${activity.packageName}.files", f)
        try {
            activity.startActivity(Intent.createChooser(Intent(Intent.ACTION_SEND).setType(mime).putExtra(Intent.EXTRA_STREAM, uri)
                .addFlags(Intent.FLAG_GRANT_READ_URI_PERMISSION), "Share"))
        } catch (_: ActivityNotFoundException) {}
    }
}

/** Keeps the screen on while recording, time-lapse, replay buffer, a live stream or the PC link is running. */
@Composable
fun KeepScreenOn(platform: AndroidPlatform, activity: Activity) {
    val media by platform.media.state.collectAsState()
    val live by platform.live.states.collectAsState()
    val web by platform.webcam.state.collectAsState()
    val busy = media.recording || media.timeLapse || media.replayOn ||
        live.values.any { it == app.kissik.studio.stream.LinkState.Live || it == app.kissik.studio.stream.LinkState.Connecting } ||
        web == WebcamState.Connected || web == WebcamState.Connecting
    androidx.compose.runtime.DisposableEffect(busy) {
        val flag = android.view.WindowManager.LayoutParams.FLAG_KEEP_SCREEN_ON
        if (busy) activity.window.addFlags(flag) else activity.window.clearFlags(flag)
        onDispose {}
    }
}

// ── Permission screen ────────────────────────────────────────────────────────

@Composable
fun PermissionGate(blocked: Boolean, onAsk: () -> Unit, onSettings: () -> Unit) {
    KissikTheme(ThemeChoice.System, highContrast = false, largeText = false) {
        androidx.compose.material3.Surface(Modifier.fillMaxSize()) {
            Column(Modifier.fillMaxSize().padding(32.dp), horizontalAlignment = Alignment.CenterHorizontally, verticalArrangement = Arrangement.Center) {
                KissikLogo(Modifier.size(96.dp))
                Spacer(Modifier.height(16.dp))
                Text("KISSIK needs the camera", style = MaterialTheme.typography.headlineSmall)
                Spacer(Modifier.height(8.dp))
                Text("The picture is processed on this phone only. The microphone is used for recordings, streams and captions.", textAlign = TextAlign.Center)
                Spacer(Modifier.height(16.dp))
                // After two refusals Android stops showing its question, so the only way left is the settings page.
                if (blocked) {
                    Text("Android won't ask again. In settings, open Permissions, then allow Camera.", textAlign = TextAlign.Center,
                        style = MaterialTheme.typography.bodyMedium)
                    Spacer(Modifier.height(12.dp))
                    Button(onClick = onSettings) { Text("Open settings to allow the camera") }
                } else {
                    Button(onClick = onAsk) { Text("Allow camera and microphone") }
                    TextButton(onClick = onSettings) { Text("Open app settings") }
                }
            }
        }
    }
}
