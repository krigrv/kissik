package app.kissik.studio.platform

import androidx.compose.runtime.Composable
import androidx.compose.ui.Modifier
import app.kissik.studio.camera.CameraState
import app.kissik.studio.captions.CaptionEngineKind
import app.kissik.studio.effects.Effects
import app.kissik.studio.framing.NRect
import app.kissik.studio.recording.Quality
import app.kissik.studio.recording.TimeLapse
import app.kissik.studio.scenes.SceneDocument
import app.kissik.studio.stream.Destination
import app.kissik.studio.stream.LinkState
import app.kissik.studio.stream.StreamSettings
import app.kissik.studio.webcam.Pairing
import kotlinx.coroutines.flow.Flow
import kotlinx.coroutines.flow.StateFlow

/**
 * Everything the shared app needs from the phone. Android implements it in `androidMain` (CameraX/Camera2, ML Kit,
 * MediaCodec, SpeechRecognizer); iOS in `iosMain` (AVFoundation, Vision, VisionKit, Speech). The shared UI and logic
 * only talk to these interfaces.
 */
interface StudioPlatform {
    val platformName: String
    val camera: CameraService
    val audio: AudioService
    val media: MediaService
    val live: LiveService
    val captions: CaptionService
    val scanner: ScannerService
    val webcam: WebcamService
    val store: KeyValueStore
    val files: FileService
    val system: SystemService
}

interface CameraService {
    val state: StateFlow<CameraState>
    /** Face boxes (0..1 of the shown picture) for auto-framing and the framing guide. */
    val faces: StateFlow<List<NRect>>
    /** Codes read while barcode scanning is on. */
    val barcodes: Flow<String>

    /** The live picture: the plain camera preview, or the processed picture when effects are on. */
    @Composable fun Viewfinder(modifier: Modifier)

    fun start()
    fun stop()
    fun setEffects(effects: Effects)
    /** The scene whose overlays are burned into recordings, streams and the PC feed (the preview draws them itself). */
    fun setScene(scene: SceneDocument?)
    fun setBarcodeScanning(on: Boolean)

    fun selectLens(id: String)
    fun switchFacing()
    fun setZoom(factor: Float)
    fun setTorch(on: Boolean)
    fun setEv(index: Int)
    fun setIso(iso: Int?)
    fun setShutter(ns: Long?)
    fun setFocus(distance: Float?)
    fun tapFocus(x: Float, y: Float)
    fun setWhiteBalance(mode: String)
    fun setFps(fps: Int)
    fun setHdr(on: Boolean)
    fun setStabilization(on: Boolean)
    fun setQuality(shortSide: Int)
}

data class Mic(val id: String, val label: String)

interface AudioService {
    val mics: StateFlow<List<Mic>>
    val selectedMic: StateFlow<String?>
    /** Input level 0..1 (peak, smoothed), for the meter. */
    val level: StateFlow<Float>
    val muted: StateFlow<Boolean>
    val noiseSuppression: StateFlow<Boolean>
    val noiseSuppressionAvailable: Boolean
    fun selectMic(id: String?)
    fun setMuted(muted: Boolean)
    fun setNoiseSuppression(on: Boolean)
    fun startMeter()
    fun stopMeter()
}

data class RecordOptions(val quality: Quality = Quality.HD, val vertical: Boolean = false)

data class MediaState(
    val recording: Boolean = false,
    val paused: Boolean = false,
    val elapsedMs: Long = 0,
    val replayOn: Boolean = false,
    val replaySeconds: Int = 30,
    val replayBufferedSec: Int = 0,
    val timeLapse: Boolean = false,
    val timeLapseFrames: Int = 0,
)

interface MediaService {
    val state: StateFlow<MediaState>
    /** Short messages for the person: "Saved to Gallery", errors. */
    val messages: Flow<String>
    fun startRecording(options: RecordOptions)
    fun pause()
    fun resume()
    fun stopRecording()
    fun setReplay(on: Boolean, seconds: Int = 30)
    fun saveReplay()
    fun snapshot()
    fun startTimeLapse(spec: TimeLapse)
    fun stopTimeLapse()
}

interface LiveService {
    val states: StateFlow<Map<String, LinkState>>
    val supportsHevc: Boolean
    fun start(destinations: List<Destination>, settings: StreamSettings)
    fun stop()
}

sealed interface CaptionEvent {
    data class Partial(val text: String) : CaptionEvent
    data class Final(val text: String) : CaptionEvent
    data class Error(val message: String) : CaptionEvent
}

interface CaptionService {
    val events: Flow<CaptionEvent>
    fun isAvailable(kind: CaptionEngineKind): Boolean
    /** Why [kind] isn't available (null when it is). */
    fun unavailableReason(kind: CaptionEngineKind): String?
    fun start(kind: CaptionEngineKind, language: String)
    fun stop()
}

data class ScannedPage(val path: String, val width: Int, val height: Int)
data class ScanResult(val pages: List<ScannedPage>, val pdfPath: String?)

interface ScannerService {
    /** Opens the system document scanner (edge detection, perspective, clean-up, multi-page). */
    fun scan(onResult: (ScanResult?) -> Unit)
    /** On-device OCR of one page. */
    fun recognizeText(page: ScannedPage, onText: (String?) -> Unit)
    /** Shares or saves a file (PDF, text) with the system sheet. */
    fun share(path: String, mime: String)
}

enum class WebcamState { Off, Connecting, Connected, Failed }

interface WebcamService {
    val state: StateFlow<WebcamState>
    val detail: StateFlow<String>
    fun connect(pairing: Pairing, useUsb: Boolean)
    fun disconnect()
}

interface KeyValueStore {
    fun get(key: String): String?
    fun put(key: String, value: String?)
}

interface FileService {
    /** Picks a picture or video for the background; returns a decoded frame source. */
    fun pickBackground(video: Boolean, onPicked: (BackgroundSource?) -> Unit)
    /** Picks a .cube LUT file and returns its text. */
    fun pickLut(onPicked: (Pair<String, String>?) -> Unit)
    fun readAppFile(name: String): String?
    fun writeAppFile(name: String, text: String)
}

/** A background picture or looping video, already decoded at preview size. */
interface BackgroundSource {
    val label: String
    /** The frame to show now (a still returns the same frame). */
    fun frameAt(timeMs: Long): app.kissik.studio.image.Frame?
    fun close()
}

interface SystemService {
    val appVersion: String
    val osName: String
    val osVersion: String
    val deviceName: String
    /** The system's "reduce motion" / "remove animations" setting. */
    val reducedMotion: Boolean
    /** The system's high-contrast text / increase-contrast setting. */
    val highContrast: Boolean
    fun readCrashLog(): String?
    fun clearCrashLog()
    /** Posts one Sentry envelope; calls back with success. */
    fun postCrash(url: String, auth: String, envelope: String, done: (Boolean) -> Unit)
    fun nowIso(): String
    fun nowMs(): Long
    fun openPrivacySettings()
}
