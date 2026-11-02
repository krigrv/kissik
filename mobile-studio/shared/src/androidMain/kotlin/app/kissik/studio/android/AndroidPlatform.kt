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
    override val captions = AndroidCaptions(activity)
    override val files = AndroidFiles(activity)
    override val scanner = AndroidScanner(activity)

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

class AndroidCaptions(private val activity: Activity) : CaptionService {
    override val events = MutableSharedFlow<CaptionEvent>(extraBufferCapacity = 32)
    private var rec: SpeechRecognizer? = null
    private var running = false
    private var language = "en-US"

    private fun onDevice() = Build.VERSION.SDK_INT >= 31 && SpeechRecognizer.isOnDeviceRecognitionAvailable(activity)
    override fun isAvailable(kind: CaptionEngineKind) = unavailableReason(kind) == null
    override fun unavailableReason(kind: CaptionEngineKind): String? = when (kind) {
        CaptionEngineKind.Fast -> if (onDevice() || SpeechRecognizer.isRecognitionAvailable(activity)) null else "This phone has no speech recognition service"
        CaptionEngineKind.Accurate -> "Accurate mode (whisper.cpp) isn't included in this build yet"
    }

    override fun start(kind: CaptionEngineKind, language: String) {
        this.language = language; running = true
        activity.runOnUiThread { listen() }
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
        r.startListening(Intent(RecognizerIntent.ACTION_RECOGNIZE_SPEECH).apply {
            putExtra(RecognizerIntent.EXTRA_LANGUAGE_MODEL, RecognizerIntent.LANGUAGE_MODEL_FREE_FORM)
            putExtra(RecognizerIntent.EXTRA_LANGUAGE, language)
            putExtra(RecognizerIntent.EXTRA_PARTIAL_RESULTS, true)
            putExtra(RecognizerIntent.EXTRA_PREFER_OFFLINE, true)
        })
    }

    override fun stop() { running = false; activity.runOnUiThread { rec?.destroy(); rec = null } }
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

// ── Permission screen ────────────────────────────────────────────────────────

@Composable
fun PermissionGate(onAsk: () -> Unit, onSettings: () -> Unit) {
    KissikTheme(ThemeChoice.System, highContrast = false, largeText = false) {
        androidx.compose.material3.Surface(Modifier.fillMaxSize()) {
            Column(Modifier.fillMaxSize().padding(32.dp), horizontalAlignment = Alignment.CenterHorizontally, verticalArrangement = Arrangement.Center) {
                KissikLogo(Modifier.size(96.dp))
                Spacer(Modifier.height(16.dp))
                Text("KISSIK needs the camera", style = MaterialTheme.typography.headlineSmall)
                Spacer(Modifier.height(8.dp))
                Text("The picture is processed on this phone only. The microphone is used for recordings, streams and captions.", textAlign = TextAlign.Center)
                Spacer(Modifier.height(16.dp))
                Button(onClick = onAsk) { Text("Allow camera and microphone") }
                TextButton(onClick = onSettings) { Text("Open app settings") }
            }
        }
    }
}
