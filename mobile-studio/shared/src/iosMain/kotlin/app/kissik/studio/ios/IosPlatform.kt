@file:OptIn(kotlinx.cinterop.ExperimentalForeignApi::class, kotlinx.cinterop.BetaInteropApi::class)

package app.kissik.studio.ios

import app.kissik.studio.captions.CaptionEngineKind
import app.kissik.studio.image.PdfWriter
import app.kissik.studio.platform.*
import kotlinx.cinterop.*
import kotlinx.coroutines.flow.MutableSharedFlow
import kotlinx.coroutines.flow.MutableStateFlow
import platform.AVFAudio.*
import platform.Foundation.*
import platform.Photos.*
import platform.Speech.*
import platform.UIKit.*
import platform.Vision.*
import platform.VisionKit.*
import platform.darwin.NSObject
import platform.darwin.dispatch_async
import platform.darwin.dispatch_get_main_queue

/** Wires every iOS service together for the shared app. */
class IosPlatform : StudioPlatform {
    var host: UIViewController? = null
    override val platformName = "iOS"
    override val store = DefaultsStore()
    override val system = IosSystem { host }
    private val iosAudio = IosAudio()
    private val iosCamera = IosCamera { system.reducedMotion }
    override val camera: CameraService = iosCamera
    override val audio: AudioService = iosAudio
    override val media: MediaService = IosMedia(iosCamera, iosAudio)
    override val live: LiveService = IosLive()
    override val captions: CaptionService = IosCaptions()
    override val scanner: ScannerService = IosScanner { host }
    override val webcam: WebcamService = IosWebcam(iosCamera, iosAudio, store)
    override val files: FileService = IosFiles { host }
}

fun onMain(block: () -> Unit) = dispatch_async(dispatch_get_main_queue()) { block() }

class DefaultsStore : KeyValueStore {
    private val d = NSUserDefaults.standardUserDefaults
    override fun get(key: String): String? = d.stringForKey("kissik.$key")
    override fun put(key: String, value: String?) { if (value == null) d.removeObjectForKey("kissik.$key") else d.setObject(value, "kissik.$key") }
}

fun documentsDir(): String = NSSearchPathForDirectoriesInDomains(NSDocumentDirectory, NSUserDomainMask, true).first() as String

class IosSystem(private val host: () -> UIViewController?) : SystemService {
    override val appVersion: String = NSBundle.mainBundle.objectForInfoDictionaryKey("CFBundleShortVersionString") as? String ?: "0"
    override val osName = "iOS"
    override val osVersion: String = UIDevice.currentDevice.systemVersion
    override val deviceName: String = UIDevice.currentDevice.name
    override val reducedMotion: Boolean get() = UIAccessibilityIsReduceMotionEnabled()
    override val highContrast: Boolean get() = UIAccessibilityDarkerSystemColorsEnabled()
    private val crashPath get() = "${documentsDir()}/crash.log"
    override fun readCrashLog(): String? = NSString.stringWithContentsOfFile(crashPath, NSUTF8StringEncoding, null)
    override fun clearCrashLog() { NSFileManager.defaultManager.removeItemAtPath(crashPath, null) }
    override fun postCrash(url: String, auth: String, envelope: String, done: (Boolean) -> Unit) {
        val req = NSMutableURLRequest.requestWithURL(NSURL.URLWithString(url)!!).apply {
            setHTTPMethod("POST")
            setValue("application/x-sentry-envelope", forHTTPHeaderField = "Content-Type")
            setValue(auth, forHTTPHeaderField = "X-Sentry-Auth")
            setHTTPBody((envelope as NSString).dataUsingEncoding(NSUTF8StringEncoding))
        }
        NSURLSession.sharedSession.dataTaskWithRequest(req) { _, resp, err ->
            val code = (resp as? NSHTTPURLResponse)?.statusCode ?: 0
            onMain { done(err == null && code in 200..299) }
        }.resume()
    }
    override fun nowIso(): String = NSISO8601DateFormatter().stringFromDate(NSDate())
    override fun nowMs(): Long = (NSDate().timeIntervalSince1970 * 1000).toLong()
    override fun openPrivacySettings() { NSURL.URLWithString(UIApplicationOpenSettingsURLString)?.let { UIApplication.sharedApplication.openURL(it, emptyMap<Any?, Any?>(), null) } }

    companion object {
        /** Kotlin/Native unhandled-exception hook writing crash.log (the Swift side may also install NSSetUncaughtExceptionHandler). */
        fun installCrashLog() {
            setUnhandledExceptionHook { e ->
                val text = "${NSISO8601DateFormatter().stringFromDate(NSDate())}\n${e.stackTraceToString()}\n"
                val path = "${documentsDir()}/crash.log"
                val old = NSString.stringWithContentsOfFile(path, NSUTF8StringEncoding, null) ?: ""
                ((old + text) as NSString).writeToFile(path, true, NSUTF8StringEncoding, null)
            }
        }
    }
}

// ── Audio ────────────────────────────────────────────────────────────────────

/** Microphone via AVAudioEngine: level meter, mute, voice-processing noise suppression, input choice. */
class IosAudio : AudioService {
    fun interface PcmListener { fun onPcm(samples: FloatArray, sampleRate: Int, timeUs: Long) }
    private val engine = AVAudioEngine()
    private val listeners = mutableListOf<PcmListener>()
    private var users = 0
    override val mics = MutableStateFlow(listMics())
    override val selectedMic = MutableStateFlow<String?>(null)
    override val level = MutableStateFlow(0f)
    override val muted = MutableStateFlow(false)
    override val noiseSuppression = MutableStateFlow(false)
    override val noiseSuppressionAvailable = true

    private fun listMics(): List<Mic> = AVAudioSession.sharedInstance().availableInputs?.map {
        val p = it as AVAudioSessionPortDescription; Mic(p.UID, p.portName)
    } ?: emptyList()

    override fun selectMic(id: String?) {
        selectedMic.value = id
        val port = AVAudioSession.sharedInstance().availableInputs?.map { it as AVAudioSessionPortDescription }?.firstOrNull { it.UID == id }
        AVAudioSession.sharedInstance().setPreferredInput(port, null)
    }
    override fun setMuted(muted: Boolean) { this.muted.value = muted }
    override fun setNoiseSuppression(on: Boolean) {
        noiseSuppression.value = on
        runCatching { engine.inputNode.setVoiceProcessingEnabled(on, null) }
    }
    override fun startMeter() { users++; ensure() }
    override fun stopMeter() { users--; maybeStop() }
    fun addListener(l: PcmListener) { listeners += l; users++; ensure() }
    fun removeListener(l: PcmListener) { listeners -= l; users--; maybeStop() }

    private fun ensure() {
        if (engine.running) return
        val s = AVAudioSession.sharedInstance()
        s.setCategory(AVAudioSessionCategoryPlayAndRecord, AVAudioSessionCategoryOptionDefaultToSpeaker or AVAudioSessionCategoryOptionAllowBluetooth, null)
        s.setActive(true, null)
        mics.value = listMics()
        val input = engine.inputNode
        val fmt = input.outputFormatForBus(0u)
        input.installTapOnBus(0u, 1024u, fmt) { buf, _ ->
            val b = buf ?: return@installTapOnBus
            val n = b.frameLength.toInt()
            val ch = b.floatChannelData?.get(0) ?: return@installTapOnBus
            val out = FloatArray(n) { if (muted.value) 0f else ch[it] }
            var peak = 0f; for (v in out) peak = maxOf(peak, kotlin.math.abs(v))
            level.value = maxOf(peak, level.value * 0.85f)
            val t = (NSDate().timeIntervalSince1970 * 1_000_000).toLong()
            listeners.toList().forEach { it.onPcm(out, fmt.sampleRate.toInt(), t) }
        }
        engine.prepare(); engine.startAndReturnError(null)
    }
    private fun maybeStop() { if (users <= 0) { users = 0; engine.inputNode.removeTapOnBus(0u); engine.stop(); level.value = 0f } }
}

// ── Live (RTMP) ──────────────────────────────────────────────────────────────

/**
 * iOS has no system RTMP client. The Swift host can plug one in (e.g. HaishinKit, BSD-3-Clause) through [IosLive.bridge];
 * without it, Go live reports that streaming needs the bridge.
 */
class IosLive : LiveService {
    interface Bridge { fun start(urls: List<Pair<String, String>>, settings: app.kissik.studio.stream.StreamSettings, state: (String, app.kissik.studio.stream.LinkState) -> Unit); fun stop() }
    companion object { var bridge: Bridge? = null }
    override val states = MutableStateFlow<Map<String, app.kissik.studio.stream.LinkState>>(emptyMap())
    override val supportsHevc = true
    override fun start(destinations: List<app.kissik.studio.stream.Destination>, settings: app.kissik.studio.stream.StreamSettings) {
        val b = bridge
        if (b == null) { states.value = destinations.associate { it.id to app.kissik.studio.stream.LinkState.Failed }; return }
        b.start(destinations.map { it.id to it.publishUrl }, settings) { id, s -> onMain { states.value = states.value + (id to s) } }
    }
    override fun stop() { bridge?.stop(); states.value = states.value.mapValues { app.kissik.studio.stream.LinkState.Idle } }
}

// ── Captions ─────────────────────────────────────────────────────────────────

class IosCaptions : CaptionService {
    override val events = MutableSharedFlow<CaptionEvent>(extraBufferCapacity = 32)
    private val engine = AVAudioEngine()
    private var task: SFSpeechRecognitionTask? = null
    private var request: SFSpeechAudioBufferRecognitionRequest? = null

    override fun isAvailable(kind: CaptionEngineKind) = unavailableReason(kind) == null
    override fun unavailableReason(kind: CaptionEngineKind): String? = when (kind) {
        CaptionEngineKind.Fast -> if (SFSpeechRecognizer()?.supportsOnDeviceRecognition == true) null else "On-device speech recognition isn't available for this language"
        CaptionEngineKind.Accurate -> "Accurate mode (whisper.cpp) isn't included in this build yet"
    }

    override fun start(kind: CaptionEngineKind, language: String) {
        SFSpeechRecognizer.requestAuthorization { status ->
            onMain {
                if (status != SFSpeechRecognizerAuthorizationStatus.SFSpeechRecognizerAuthorizationStatusAuthorized) { events.tryEmit(CaptionEvent.Error("Captions need speech recognition permission")); return@onMain }
                val rec = SFSpeechRecognizer(NSLocale(language)) ?: return@onMain
                val req = SFSpeechAudioBufferRecognitionRequest().apply { shouldReportPartialResults = true; requiresOnDeviceRecognition = true }
                request = req
                val input = engine.inputNode
                input.installTapOnBus(0u, 1024u, input.outputFormatForBus(0u)) { buf, _ -> buf?.let { req.appendAudioPCMBuffer(it) } }
                engine.prepare(); engine.startAndReturnError(null)
                task = rec.recognitionTaskWithRequest(req) { result, err ->
                    if (result != null) {
                        val text = result.bestTranscription.formattedString
                        events.tryEmit(if (result.isFinal()) CaptionEvent.Final(text) else CaptionEvent.Partial(text))
                    }
                    if (err != null) events.tryEmit(CaptionEvent.Error("Captions stopped: ${err.localizedDescription}"))
                }
            }
        }
    }

    override fun stop() { task?.cancel(); request?.endAudio(); engine.inputNode.removeTapOnBus(0u); engine.stop(); task = null; request = null }
}

// ── Document scanner ─────────────────────────────────────────────────────────

/** VisionKit's document camera (edges, perspective, clean-up, multi-page), Vision OCR, and the shared PDF writer. */
class IosScanner(private val host: () -> UIViewController?) : ScannerService {
    private var delegate: NSObject? = null

    override fun scan(onResult: (ScanResult?) -> Unit) {
        if (!VNDocumentCameraViewController.supported) { onResult(null); return }
        val vc = VNDocumentCameraViewController()
        val d = object : NSObject(), VNDocumentCameraViewControllerDelegateProtocol {
            override fun documentCameraViewController(controller: VNDocumentCameraViewController, didFinishWithScan: VNDocumentCameraScan) {
                val pages = mutableListOf<ScannedPage>(); val pdfPages = mutableListOf<PdfWriter.Page>()
                for (i in 0 until didFinishWithScan.pageCount.toInt()) {
                    val img = didFinishWithScan.imageOfPageAtIndex(i.toULong())
                    val data = UIImageJPEGRepresentation(img, 0.85) ?: continue
                    val path = "${documentsDir()}/scan-$i.jpg"
                    data.writeToFile(path, true)
                    val (w, h) = img.size.useContents { width.toInt() to height.toInt() }
                    pages += ScannedPage(path, w, h)
                    pdfPages += PdfWriter.Page(data.toByteArray(), w, h)
                }
                val pdfPath = "${documentsDir()}/KISSIK-scan.pdf"
                if (pdfPages.isNotEmpty()) PdfWriter.write(pdfPages).toNSData().writeToFile(pdfPath, true)
                controller.dismissViewControllerAnimated(true, null)
                onResult(ScanResult(pages, pdfPath.takeIf { pdfPages.isNotEmpty() }))
            }
            override fun documentCameraViewControllerDidCancel(controller: VNDocumentCameraViewController) { controller.dismissViewControllerAnimated(true, null); onResult(null) }
        }
        delegate = d; vc.delegate = d
        host()?.presentViewController(vc, true, null)
    }

    override fun recognizeText(page: ScannedPage, onText: (String?) -> Unit) {
        val img = UIImage.imageWithContentsOfFile(page.path)?.CGImage ?: run { onText(null); return }
        val req = VNRecognizeTextRequest { r, _ ->
            val text = r?.results?.mapNotNull { (it as? VNRecognizedTextObservation)?.topCandidates(1u)?.firstOrNull()?.let { c -> (c as VNRecognizedText).string } }?.joinToString("\n")
            onMain { onText(text) }
        }.apply { recognitionLevel = VNRequestTextRecognitionLevelAccurate }
        VNImageRequestHandler(img, emptyMap<Any?, Any?>()).performRequests(listOf(req), null)
    }

    override fun share(path: String, mime: String) {
        val p = if (path.startsWith("/")) path else "${documentsDir()}/$path"
        host()?.presentViewController(UIActivityViewController(listOf(NSURL.fileURLWithPath(p)), null), true, null)
    }
}

// ── Files ────────────────────────────────────────────────────────────────────

class IosFiles(private val host: () -> UIViewController?) : FileService {
    private var keep: NSObject? = null
    override fun pickBackground(video: Boolean, onPicked: (BackgroundSource?) -> Unit) {
        val picker = UIImagePickerController().apply {
            sourceType = UIImagePickerControllerSourceType.UIImagePickerControllerSourceTypePhotoLibrary
            mediaTypes = listOf(if (video) "public.movie" else "public.image")
        }
        val d = object : NSObject(), UIImagePickerControllerDelegateProtocol, UINavigationControllerDelegateProtocol {
            override fun imagePickerController(picker: UIImagePickerController, didFinishPickingMediaWithInfo: Map<Any?, *>) {
                picker.dismissViewControllerAnimated(true, null)
                val src = if (video) (didFinishPickingMediaWithInfo[UIImagePickerControllerMediaURL] as? NSURL)?.let { VideoBackground(it) }
                else (didFinishPickingMediaWithInfo[UIImagePickerControllerOriginalImage] as? UIImage)?.toFrame(960)?.let { f ->
                    object : BackgroundSource { override val label = "Picture"; override fun frameAt(timeMs: Long) = f; override fun close() {} }
                }
                onPicked(src)
            }
            override fun imagePickerControllerDidCancel(picker: UIImagePickerController) { picker.dismissViewControllerAnimated(true, null); onPicked(null) }
        }
        keep = d; picker.delegate = d
        host()?.presentViewController(picker, true, null)
    }

    override fun pickLut(onPicked: (Pair<String, String>?) -> Unit) {
        val picker = UIDocumentPickerViewController(documentTypes = listOf("public.data", "public.text"), inMode = UIDocumentPickerMode.UIDocumentPickerModeImport)
        val d = object : NSObject(), UIDocumentPickerDelegateProtocol {
            override fun documentPicker(controller: UIDocumentPickerViewController, didPickDocumentsAtURLs: List<*>) {
                val url = didPickDocumentsAtURLs.firstOrNull() as? NSURL
                onPicked(url?.let { u -> NSString.stringWithContentsOfURL(u, NSUTF8StringEncoding, null)?.let { (u.lastPathComponent ?: "LUT") to it } })
            }
            override fun documentPickerWasCancelled(controller: UIDocumentPickerViewController) = onPicked(null)
        }
        keep = d; picker.delegate = d
        host()?.presentViewController(picker, true, null)
    }

    override fun readAppFile(name: String): String? = NSString.stringWithContentsOfFile("${documentsDir()}/$name", NSUTF8StringEncoding, null)
    override fun writeAppFile(name: String, text: String) { (text as NSString).writeToFile("${documentsDir()}/$name", true, NSUTF8StringEncoding, null) }
}

// ── Byte helpers ─────────────────────────────────────────────────────────────

fun NSData.toByteArray(): ByteArray = ByteArray(length.toInt()).also { a -> if (a.isNotEmpty()) a.usePinned { platform.posix.memcpy(it.addressOf(0), bytes, length) } }
fun ByteArray.toNSData(): NSData = if (isEmpty()) NSData() else usePinned { NSData.dataWithBytes(it.addressOf(0), size.toULong()) }
