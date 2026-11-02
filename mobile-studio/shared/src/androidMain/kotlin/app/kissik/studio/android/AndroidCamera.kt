package app.kissik.studio.android

import android.annotation.SuppressLint
import android.graphics.Bitmap
import android.graphics.Matrix
import android.hardware.camera2.CameraCharacteristics
import android.hardware.camera2.CameraMetadata
import android.hardware.camera2.CaptureRequest
import android.util.Range
import android.util.Size
import androidx.activity.ComponentActivity
import androidx.camera.camera2.interop.Camera2CameraControl
import androidx.camera.camera2.interop.Camera2CameraInfo
import androidx.camera.camera2.interop.CaptureRequestOptions
import androidx.camera.camera2.interop.ExperimentalCamera2Interop
import androidx.camera.core.*
import androidx.camera.core.resolutionselector.ResolutionSelector
import androidx.camera.core.resolutionselector.ResolutionStrategy
import androidx.camera.lifecycle.ProcessCameraProvider
import androidx.camera.view.PreviewView
import androidx.compose.foundation.Image
import androidx.compose.foundation.layout.Box
import androidx.compose.runtime.Composable
import androidx.compose.runtime.collectAsState
import androidx.compose.runtime.getValue
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.ImageBitmap
import androidx.compose.ui.graphics.asImageBitmap
import androidx.compose.ui.layout.ContentScale
import androidx.compose.ui.viewinterop.AndroidView
import app.kissik.studio.camera.CameraState
import app.kissik.studio.camera.Lens
import app.kissik.studio.effects.Compositor
import app.kissik.studio.effects.Effects
import app.kissik.studio.framing.NRect
import app.kissik.studio.image.Frame
import app.kissik.studio.image.Mask
import app.kissik.studio.platform.CameraService
import app.kissik.studio.scenes.SceneDocument
import com.google.android.gms.tasks.Tasks
import com.google.mlkit.vision.barcode.BarcodeScanning
import com.google.mlkit.vision.common.InputImage
import com.google.mlkit.vision.face.FaceDetection
import com.google.mlkit.vision.face.FaceDetectorOptions
import com.google.mlkit.vision.segmentation.Segmentation
import com.google.mlkit.vision.segmentation.selfie.SelfieSegmenterOptions
import kotlinx.coroutines.flow.MutableSharedFlow
import kotlinx.coroutines.flow.MutableStateFlow
import java.util.concurrent.CopyOnWriteArrayList
import java.util.concurrent.Executors
import java.util.concurrent.TimeUnit

/** Gets every finished frame (effects applied, scene burned in). The bitmap is only valid during the call. */
fun interface FrameSink { fun onFrame(bmp: Bitmap, timeNs: Long) }

/**
 * Camera on Android: CameraX for preview and frames, Camera2 interop for the pro controls, ML Kit for selfie
 * segmentation, faces and barcodes, and the shared [Compositor] for the picture itself.
 */
@OptIn(ExperimentalCamera2Interop::class)
class AndroidCamera(private val activity: ComponentActivity, private val reducedMotion: () -> Boolean) : CameraService {
    override val state = MutableStateFlow(CameraState())
    override val faces = MutableStateFlow<List<NRect>>(emptyList())
    override val barcodes = MutableSharedFlow<String>(extraBufferCapacity = 8)

    private val previewView by lazy {
        PreviewView(activity).apply { implementationMode = PreviewView.ImplementationMode.COMPATIBLE; scaleType = PreviewView.ScaleType.FIT_CENTER }
    }
    private val processed = MutableStateFlow<ImageBitmap?>(null)
    private val analysisExec = Executors.newSingleThreadExecutor()
    private var provider: ProcessCameraProvider? = null
    private var camera: Camera? = null
    private var started = false

    @Volatile private var effects = Effects()
    @Volatile private var scene: SceneDocument? = null
    @Volatile private var scanning = false
    val sinks = CopyOnWriteArrayList<FrameSink>()
    /** The latest finished frame (for snapshots). */
    @Volatile var lastFrame: Bitmap? = null; private set

    private val compositor = Compositor()
    private val segmenter by lazy { Segmentation.getClient(SelfieSegmenterOptions.Builder().setDetectorMode(SelfieSegmenterOptions.STREAM_MODE).build()) }
    private val faceDetector by lazy { FaceDetection.getClient(FaceDetectorOptions.Builder().setPerformanceMode(FaceDetectorOptions.PERFORMANCE_MODE_FAST).build()) }
    private val barcodeScanner by lazy { BarcodeScanning.getClient() }

    // Chosen settings (re-applied after every rebind).
    private var front = false
    private var lensId: String? = null
    private var shortSide = 720
    private var iso: Int? = null; private var shutter: Long? = null; private var focus: Float? = null
    private var wb = "auto"; private var fps = 0; private var hdr = false; private var stab = false
    private var frameNo = 0L
    private var lastNs = 0L

    @Composable
    override fun Viewfinder(modifier: Modifier) {
        val bmp by processed.collectAsState()
        val st by state.collectAsState()
        Box(modifier) {
            AndroidView({ previewView }, Modifier.matchParentSize())
            if (effects.needsProcessing || bmp != null && st.ready && effectsOn()) bmp?.let {
                Image(it, contentDescription = null, contentScale = ContentScale.Fit, modifier = Modifier.matchParentSize())
            }
        }
    }
    private fun effectsOn() = effects.needsProcessing

    override fun start() {
        if (started) return
        started = true
        val f = ProcessCameraProvider.getInstance(activity)
        f.addListener({ runCatching { provider = f.get(); bind() }.onFailure { state.value = state.value.copy(error = "The camera couldn't start: ${it.message}") } },
            activity.mainExecutor)
    }

    override fun stop() { started = false; provider?.unbindAll(); camera = null; state.value = state.value.copy(ready = false) }

    /** Releases the camera for a moment (e.g. a 4K direct recording that binds its own use cases). */
    fun provider() = provider
    fun selector(): CameraSelector = selectorFor(lensId, front)
    fun rebind() { if (started) bind() }

    /** Binds preview + [extra] only (no effects), for direct 4K recording. Returns false if the camera refused. */
    fun bindDirect(extra: UseCase): Boolean {
        val p = provider ?: return false
        p.unbindAll()
        val preview = Preview.Builder().build()
        return runCatching { camera = p.bindToLifecycle(activity, selectorFor(lensId, front), preview, extra); preview.surfaceProvider = previewView.surfaceProvider; applyOptions(); true }
            .getOrElse { bind(); false }
    }

    private fun cameraId(info: CameraInfo) = Camera2CameraInfo.from(info).cameraId

    private fun selectorFor(id: String?, front: Boolean): CameraSelector = if (id != null)
        CameraSelector.Builder().addCameraFilter { list -> list.filter { cameraId(it) == id } }.build()
    else if (front) CameraSelector.DEFAULT_FRONT_CAMERA else CameraSelector.DEFAULT_BACK_CAMERA

    private fun lenses(p: ProcessCameraProvider): List<Lens> {
        val infos = p.availableCameraInfos
        val backs = infos.filter { it.lensFacing == CameraSelector.LENS_FACING_BACK }
        val focal = backs.associateWith { Camera2CameraInfo.from(it).getCameraCharacteristic(CameraCharacteristics.LENS_INFO_AVAILABLE_FOCAL_LENGTHS)?.minOrNull() ?: 0f }
        val main = focal.entries.sortedBy { it.value }.getOrNull(if (backs.size >= 3) 1 else 0)?.value ?: 0f
        return infos.map { i ->
            val id = cameraId(i)
            val isFront = i.lensFacing == CameraSelector.LENS_FACING_FRONT
            val label = when {
                isFront -> "Front"
                main > 0 && (focal[i] ?: main) < main * 0.8f -> "Ultra-wide"
                main > 0 && (focal[i] ?: main) > main * 1.5f -> "Telephoto"
                else -> "Main"
            }
            Lens(id, isFront, label)
        }.groupBy { it.label }.flatMap { (label, l) -> if (l.size == 1) l else l.mapIndexed { n, x -> x.copy(label = "$label ${n + 1}") } }
    }

    private fun bind() {
        val p = provider ?: return
        p.unbindAll()
        val sel = selectorFor(lensId, front).takeIf { runCatching { p.hasCamera(it) }.getOrDefault(false) }
            ?: CameraSelector.DEFAULT_BACK_CAMERA.takeIf { p.hasCamera(it) } ?: CameraSelector.DEFAULT_FRONT_CAMERA
        val res = ResolutionSelector.Builder().setResolutionStrategy(
            ResolutionStrategy(if (shortSide >= 1080) Size(1920, 1080) else Size(1280, 720), ResolutionStrategy.FALLBACK_RULE_CLOSEST_HIGHER_THEN_LOWER)).build()
        val preview = Preview.Builder().setResolutionSelector(res).build()
        val analysis = ImageAnalysis.Builder().setResolutionSelector(res)
            .setOutputImageFormat(ImageAnalysis.OUTPUT_IMAGE_FORMAT_RGBA_8888)
            .setBackpressureStrategy(ImageAnalysis.STRATEGY_KEEP_ONLY_LATEST).build()
        analysis.setAnalyzer(analysisExec, ::analyze)
        val cam = runCatching { p.bindToLifecycle(activity, sel, preview, analysis) }.getOrElse {
            state.value = state.value.copy(error = "The camera couldn't start: ${it.message}", ready = false); return
        }
        preview.surfaceProvider = previewView.surfaceProvider
        camera = cam
        front = cam.cameraInfo.lensFacing == CameraSelector.LENS_FACING_FRONT
        readCapabilities(cam, p, analysis.resolutionInfo?.resolution, analysis.resolutionInfo?.rotationDegrees ?: 0)
        applyOptions()
        cam.cameraInfo.zoomState.observe(activity) { z -> state.value = state.value.copy(zoom = z.zoomRatio, zoomMin = z.minZoomRatio, zoomMax = z.maxZoomRatio) }
        cam.cameraInfo.torchState.observe(activity) { t -> state.value = state.value.copy(torch = t == TorchState.ON) }
    }

    private fun readCapabilities(cam: Camera, p: ProcessCameraProvider, res: Size?, rot: Int) {
        val info = cam.cameraInfo
        val c2 = Camera2CameraInfo.from(info)
        fun <T> ch(k: CameraCharacteristics.Key<T>): T? = runCatching { c2.getCameraCharacteristic(k) }.getOrNull()
        val caps = ch(CameraCharacteristics.REQUEST_AVAILABLE_CAPABILITIES)?.toList() ?: emptyList()
        val manual = CameraMetadata.REQUEST_AVAILABLE_CAPABILITIES_MANUAL_SENSOR in caps
        val isoR = ch(CameraCharacteristics.SENSOR_INFO_SENSITIVITY_RANGE)
        val expR = ch(CameraCharacteristics.SENSOR_INFO_EXPOSURE_TIME_RANGE)
        val minFocus = ch(CameraCharacteristics.LENS_INFO_MINIMUM_FOCUS_DISTANCE) ?: 0f
        val awb = ch(CameraCharacteristics.CONTROL_AWB_AVAILABLE_MODES)?.toList() ?: emptyList()
        val fpsR = ch(CameraCharacteristics.CONTROL_AE_AVAILABLE_TARGET_FPS_RANGES)?.map { it.upper }?.distinct()?.sorted() ?: emptyList()
        val stabModes = ch(CameraCharacteristics.CONTROL_AVAILABLE_VIDEO_STABILIZATION_MODES)?.toList() ?: emptyList()
        val scenes = ch(CameraCharacteristics.CONTROL_AVAILABLE_SCENE_MODES)?.toList() ?: emptyList()
        val big = ch(CameraCharacteristics.SCALER_STREAM_CONFIGURATION_MAP)?.getOutputSizes(android.graphics.ImageFormat.YUV_420_888)?.maxByOrNull { it.width * it.height }
        val ev = info.exposureState
        val upright = if (rot % 180 == 90) res?.let { Size(it.height, it.width) } else res
        state.value = state.value.copy(
            ready = true, error = null, front = front, lenses = lenses(p), lensId = lensId,
            width = upright?.width ?: 0, height = upright?.height ?: 0, maxWidth = big?.width ?: 0, maxHeight = big?.height ?: 0,
            maxFps = fpsR.maxOrNull() ?: 30, hasTorch = info.hasFlashUnit(),
            ev = ev.exposureCompensationIndex, evMin = ev.exposureCompensationRange.lower, evMax = ev.exposureCompensationRange.upper,
            evStep = if (ev.isExposureCompensationSupported) ev.exposureCompensationStep.toFloat() else 0f,
            manual = manual && isoR != null && expR != null,
            isoMin = isoR?.lower ?: 0, isoMax = isoR?.upper ?: 0, shutterMin = expR?.lower ?: 0, shutterMax = expR?.upper ?: 0,
            iso = iso, shutter = shutter, focusManual = minFocus > 0f, focus = focus,
            wbModes = listOf("auto") + awb.mapNotNull { WB[it] }.filter { it != "auto" }, wb = wb,
            fpsList = fpsR.filter { it >= 15 }, fps = fps,
            stab = if (stabModes.contains(CameraMetadata.CONTROL_VIDEO_STABILIZATION_MODE_ON)) stab else null,
            hdr = if (scenes.contains(CameraMetadata.CONTROL_SCENE_MODE_HDR)) hdr else null,
        )
        minFocusDistance = minFocus
    }
    private var minFocusDistance = 0f

    private val WB = mapOf(
        CameraMetadata.CONTROL_AWB_MODE_AUTO to "auto", CameraMetadata.CONTROL_AWB_MODE_DAYLIGHT to "daylight",
        CameraMetadata.CONTROL_AWB_MODE_CLOUDY_DAYLIGHT to "cloudy", CameraMetadata.CONTROL_AWB_MODE_SHADE to "shade",
        CameraMetadata.CONTROL_AWB_MODE_INCANDESCENT to "tungsten", CameraMetadata.CONTROL_AWB_MODE_FLUORESCENT to "fluorescent",
        CameraMetadata.CONTROL_AWB_MODE_WARM_FLUORESCENT to "warm-fluorescent", CameraMetadata.CONTROL_AWB_MODE_TWILIGHT to "twilight",
    )

    /** Rebuilds the Camera2 request options from every chosen setting. */
    private fun applyOptions() {
        val cam = camera ?: return
        val s = state.value
        val b = CaptureRequestOptions.Builder()
        if (s.manual && (iso != null || shutter != null)) {
            b.setCaptureRequestOption(CaptureRequest.CONTROL_AE_MODE, CaptureRequest.CONTROL_AE_MODE_OFF)
            b.setCaptureRequestOption(CaptureRequest.SENSOR_SENSITIVITY, iso ?: 400)
            b.setCaptureRequestOption(CaptureRequest.SENSOR_EXPOSURE_TIME, shutter ?: 16_666_666L)
        }
        focus?.let { f ->
            b.setCaptureRequestOption(CaptureRequest.CONTROL_AF_MODE, CaptureRequest.CONTROL_AF_MODE_OFF)
            b.setCaptureRequestOption(CaptureRequest.LENS_FOCUS_DISTANCE, f * minFocusDistance)
        }
        WB.entries.firstOrNull { it.value == wb }?.let { b.setCaptureRequestOption(CaptureRequest.CONTROL_AWB_MODE, it.key) }
        if (fps > 0) b.setCaptureRequestOption(CaptureRequest.CONTROL_AE_TARGET_FPS_RANGE, Range(fps, fps))
        if (s.stab != null) b.setCaptureRequestOption(CaptureRequest.CONTROL_VIDEO_STABILIZATION_MODE,
            if (stab) CaptureRequest.CONTROL_VIDEO_STABILIZATION_MODE_ON else CaptureRequest.CONTROL_VIDEO_STABILIZATION_MODE_OFF)
        if (s.hdr != null && hdr) {
            b.setCaptureRequestOption(CaptureRequest.CONTROL_MODE, CaptureRequest.CONTROL_MODE_USE_SCENE_MODE)
            b.setCaptureRequestOption(CaptureRequest.CONTROL_SCENE_MODE, CaptureRequest.CONTROL_SCENE_MODE_HDR)
        }
        Camera2CameraControl.from(cam.cameraControl).captureRequestOptions = b.build()
        state.value = state.value.copy(iso = iso, shutter = shutter, focus = focus, wb = wb, fps = fps,
            stab = s.stab?.let { stab }, hdr = s.hdr?.let { hdr })
    }

    // ── Frames ────────────────────────────────────────────────────────────
    private var lastFaces: List<NRect> = emptyList()

    @SuppressLint("UnsafeOptInUsageError")
    private fun analyze(image: ImageProxy) {
        try {
            val fx = effects
            val wantFaces = fx.autoFrame
            val work = fx.needsProcessing || sinks.isNotEmpty() || scanning || wantFaces
            if (!work) { if (processed.value != null) processed.value = null; return }
            val raw = image.toBitmap()
            val rot = image.imageInfo.rotationDegrees
            // Upright, mirrored like a mirror for the front camera, at most 960 px on the long side for the effects.
            val scale = minOf(1f, 960f / maxOf(raw.width, raw.height))
            val m = Matrix().apply { postRotate(rot.toFloat()); postScale(if (front) -scale else scale, scale) }
            val bmp = Bitmap.createBitmap(raw, 0, 0, raw.width, raw.height, m, true)
            frameNo++
            val now = image.imageInfo.timestamp
            val dt = if (lastNs == 0L) 0.033f else ((now - lastNs) / 1e9f).coerceIn(0.001f, 0.2f)
            lastNs = now

            if (scanning && frameNo % 5 == 0L) runCatching {
                Tasks.await(barcodeScanner.process(InputImage.fromBitmap(bmp, 0)), 2, TimeUnit.SECONDS).forEach { b -> b.rawValue?.let { barcodes.tryEmit(it) } }
            }
            if (wantFaces && frameNo % 3 == 0L) runCatching {
                val found = Tasks.await(faceDetector.process(InputImage.fromBitmap(bmp, 0)), 2, TimeUnit.SECONDS)
                lastFaces = found.map { f -> val r = f.boundingBox
                    NRect(r.left / bmp.width.toFloat(), r.top / bmp.height.toFloat(), r.width() / bmp.width.toFloat(), r.height() / bmp.height.toFloat()) }
                faces.value = lastFaces
            }
            if (!fx.needsProcessing && sinks.isEmpty()) { if (processed.value != null) processed.value = null; return }

            val w = bmp.width; val h = bmp.height
            val px = IntArray(w * h); bmp.getPixels(px, 0, w, 0, 0, w, h)
            val mask = if (fx.needsMask) segment(bmp) else null
            val out = compositor.process(Frame(w, h, px), fx, mask, lastFaces, dt, reducedMotion())
            val outBmp = Bitmap.createBitmap(out.pixels, out.width, out.height, Bitmap.Config.ARGB_8888)
            processed.value = if (fx.needsProcessing) outBmp.asImageBitmap() else null
            val burned = scene?.let { sc -> outBmp.copy(Bitmap.Config.ARGB_8888, true).also { SceneRenderer.draw(it, sc) } } ?: outBmp
            lastFrame = burned
            sinks.forEach { runCatching { it.onFrame(burned, now) } }
        } catch (t: Throwable) {
            android.util.Log.w("KISSIK", "frame failed", t)
        } finally { image.close() }
    }

    private fun segment(bmp: Bitmap): Mask? = runCatching {
        val r = Tasks.await(segmenter.process(InputImage.fromBitmap(bmp, 0)), 2, TimeUnit.SECONDS)
        val buf = r.buffer.asFloatBuffer()
        val v = FloatArray(r.width * r.height); buf.get(v)
        Mask(r.width, r.height, v)
    }.getOrNull()

    // ── CameraService controls ─────────────────────────────────────────────
    override fun setEffects(effects: Effects) { this.effects = effects; if (!effects.needsProcessing) processed.value = null }
    override fun setScene(scene: SceneDocument?) { this.scene = scene }
    override fun setBarcodeScanning(on: Boolean) { scanning = on }
    override fun selectLens(id: String) { lensId = id; front = state.value.lenses.firstOrNull { it.id == id }?.front ?: front; resetManual(); rebind() }
    override fun switchFacing() { front = !front; lensId = null; resetManual(); rebind() }
    private fun resetManual() { iso = null; shutter = null; focus = null; fps = 0; hdr = false; stab = false }
    override fun setZoom(factor: Float) { camera?.cameraControl?.setZoomRatio(factor.coerceIn(state.value.zoomMin, state.value.zoomMax)) }
    override fun setTorch(on: Boolean) { camera?.cameraControl?.enableTorch(on) }
    override fun setEv(index: Int) {
        val s = state.value
        camera?.cameraControl?.setExposureCompensationIndex(index.coerceIn(s.evMin, s.evMax))
        state.value = s.copy(ev = index.coerceIn(s.evMin, s.evMax))
    }
    override fun setIso(iso: Int?) { this.iso = iso; applyOptions() }
    override fun setShutter(ns: Long?) { shutter = ns; applyOptions() }
    override fun setFocus(distance: Float?) { focus = distance; applyOptions() }
    override fun tapFocus(x: Float, y: Float) {
        val cam = camera ?: return
        focus = null; applyOptions()
        val pt = SurfaceOrientedMeteringPointFactory(1f, 1f).createPoint(if (front) 1 - x else x, y)
        cam.cameraControl.startFocusAndMetering(FocusMeteringAction.Builder(pt).setAutoCancelDuration(4, TimeUnit.SECONDS).build())
    }
    override fun setWhiteBalance(mode: String) { wb = mode; applyOptions() }
    override fun setFps(fps: Int) { this.fps = fps; applyOptions() }
    override fun setHdr(on: Boolean) { hdr = on; applyOptions() }
    override fun setStabilization(on: Boolean) { stab = on; applyOptions() }
    override fun setQuality(shortSide: Int) { this.shortSide = shortSide; rebind() }

    fun release() { stop(); analysisExec.shutdown(); runCatching { segmenter.close(); faceDetector.close(); barcodeScanner.close() } }
}
