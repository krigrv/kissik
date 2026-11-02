@file:OptIn(kotlinx.cinterop.ExperimentalForeignApi::class, kotlinx.cinterop.BetaInteropApi::class)

package app.kissik.studio.ios

import androidx.compose.foundation.Image
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.collectAsState
import androidx.compose.runtime.getValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.ImageBitmap
import androidx.compose.ui.graphics.asComposeImageBitmap
import androidx.compose.ui.layout.ContentScale
import app.kissik.studio.camera.CameraState
import app.kissik.studio.camera.Lens
import app.kissik.studio.effects.Compositor
import app.kissik.studio.effects.Effects
import app.kissik.studio.framing.NRect
import app.kissik.studio.image.Frame
import app.kissik.studio.image.Mask
import app.kissik.studio.platform.CameraService
import app.kissik.studio.scenes.SceneDocument
import kotlinx.cinterop.*
import kotlinx.coroutines.flow.MutableSharedFlow
import kotlinx.coroutines.flow.MutableStateFlow
import org.jetbrains.skia.Bitmap
import org.jetbrains.skia.ColorAlphaType
import org.jetbrains.skia.ImageInfo
import platform.AVFoundation.*
import platform.CoreGraphics.*
import platform.CoreMedia.*
import platform.CoreVideo.*
import platform.Foundation.*
import platform.UIKit.*
import platform.Vision.*
import platform.darwin.*

/** Gets every finished frame (effects applied). */
fun interface IosFrameSink { fun onFrame(frame: Frame, timeUs: Long) }

/**
 * Camera on iOS: AVCaptureSession with a BGRA video output; Vision for person segmentation, faces and barcodes; the
 * shared [Compositor] for the picture. The viewfinder always shows the processed frame so it matches recordings.
 */
class IosCamera(private val reducedMotion: () -> Boolean) : CameraService {
    override val state = MutableStateFlow(CameraState())
    override val faces = MutableStateFlow<List<NRect>>(emptyList())
    override val barcodes = MutableSharedFlow<String>(extraBufferCapacity = 8)
    private val picture = MutableStateFlow<ImageBitmap?>(null)

    private val session = AVCaptureSession()
    private val output = AVCaptureVideoDataOutput()
    private val queue = dispatch_queue_create("kissik.camera", null)
    private var device: AVCaptureDevice? = null
    private var front = false
    private var lensId: String? = null
    @kotlin.concurrent.Volatile private var effects = Effects()
    @kotlin.concurrent.Volatile private var scene: SceneDocument? = null
    @kotlin.concurrent.Volatile private var scanning = false
    val sinks = mutableListOf<IosFrameSink>()
    @kotlin.concurrent.Volatile var lastFrame: Frame? = null; private set
    private val compositor = Compositor()
    private var frameNo = 0L
    private var lastFaces: List<NRect> = emptyList()
    private var lastT = 0.0

    private val delegate = object : NSObject(), AVCaptureVideoDataOutputSampleBufferDelegateProtocol {
        override fun captureOutput(output: AVCaptureOutput, didOutputSampleBuffer: CMSampleBufferRef?, fromConnection: AVCaptureConnection) {
            didOutputSampleBuffer?.let { process(it) }
        }
    }

    @Composable
    override fun Viewfinder(modifier: Modifier) {
        val img by picture.collectAsState()
        val st by state.collectAsState()
        Box(modifier) {
            img?.let { Image(it, null, Modifier.fillMaxSize(), contentScale = ContentScale.Fit) }
            if (!st.ready) Text(st.error ?: "", color = Color.White, modifier = Modifier.align(Alignment.Center))
        }
    }

    private fun devices(): List<AVCaptureDevice> = AVCaptureDeviceDiscoverySession.discoverySessionWithDeviceTypes(
        listOf(AVCaptureDeviceTypeBuiltInUltraWideCamera, AVCaptureDeviceTypeBuiltInWideAngleCamera, AVCaptureDeviceTypeBuiltInTelephotoCamera),
        AVMediaTypeVideo, AVCaptureDevicePositionUnspecified).devices.map { it as AVCaptureDevice }

    override fun start() {
        AVCaptureDevice.requestAccessForMediaType(AVMediaTypeVideo) { ok ->
            if (!ok) { state.value = state.value.copy(error = "KISSIK needs camera access. Turn it on in Settings."); return@requestAccessForMediaType }
            dispatch_async(queue) { configure() }
        }
    }

    private fun configure() {
        session.beginConfiguration()
        session.inputs.forEach { session.removeInput(it as AVCaptureInput) }
        session.sessionPreset = AVCaptureSessionPreset1280x720
        val all = devices()
        val dev = all.firstOrNull { it.uniqueID == lensId }
            ?: AVCaptureDevice.defaultDeviceWithDeviceType(AVCaptureDeviceTypeBuiltInWideAngleCamera, AVMediaTypeVideo,
                if (front) AVCaptureDevicePositionFront else AVCaptureDevicePositionBack)
        if (dev == null) { session.commitConfiguration(); state.value = state.value.copy(error = "No camera found"); return }
        device = dev
        front = dev.position == AVCaptureDevicePositionFront
        AVCaptureDeviceInput.deviceInputWithDevice(dev, null)?.let { if (session.canAddInput(it)) session.addInput(it) }
        if (session.outputs.isEmpty()) {
            output.videoSettings = mapOf<Any?, Any?>(kCVPixelBufferPixelFormatTypeKey to kCVPixelFormatType_32BGRA)
            output.alwaysDiscardsLateVideoFrames = true
            output.setSampleBufferDelegate(delegate, queue)
            if (session.canAddOutput(output)) session.addOutput(output)
        }
        output.connectionWithMediaType(AVMediaTypeVideo)?.let { c ->
            c.videoOrientation = AVCaptureVideoOrientationPortrait
            if (c.isVideoMirroringSupported()) c.videoMirrored = front
        }
        session.commitConfiguration()
        if (!session.running) session.startRunning()
        readCapabilities(dev, all)
    }

    private fun readCapabilities(d: AVCaptureDevice, all: List<AVCaptureDevice>) {
        val f = d.activeFormat
        val lenses = all.map { x ->
            Lens(x.uniqueID, x.position == AVCaptureDevicePositionFront, when {
                x.position == AVCaptureDevicePositionFront -> "Front"
                x.deviceType == AVCaptureDeviceTypeBuiltInUltraWideCamera -> "Ultra-wide"
                x.deviceType == AVCaptureDeviceTypeBuiltInTelephotoCamera -> "Telephoto"
                else -> "Main"
            })
        }
        val fpsMax = f.videoSupportedFrameRateRanges.maxOfOrNull { (it as AVFrameRateRange).maxFrameRate }?.toInt() ?: 30
        state.value = state.value.copy(
            ready = true, error = null, front = front, lenses = lenses, lensId = lensId, width = 720, height = 1280,
            maxFps = fpsMax, hasTorch = d.hasTorch, torch = d.torchMode == AVCaptureTorchModeOn,
            zoom = d.videoZoomFactor.toFloat(), zoomMin = d.minAvailableVideoZoomFactor.toFloat(), zoomMax = minOf(d.maxAvailableVideoZoomFactor, 10.0).toFloat(),
            ev = 0, evMin = (d.minExposureTargetBias * 3).toInt(), evMax = (d.maxExposureTargetBias * 3).toInt(), evStep = 1f / 3,
            manual = d.isExposureModeSupported(AVCaptureExposureModeCustom),
            isoMin = f.minISO.toInt(), isoMax = f.maxISO.toInt(),
            shutterMin = (CMTimeGetSeconds(f.minExposureDuration) * 1e9).toLong(), shutterMax = (CMTimeGetSeconds(f.maxExposureDuration) * 1e9).toLong(),
            focusManual = d.isLockingFocusWithCustomLensPositionSupported(),
            wbModes = listOf("auto", "daylight", "cloudy", "tungsten", "fluorescent", "locked"),
            fpsList = listOf(24, 25, 30, 60).filter { it <= fpsMax },
            stab = if (f.isVideoStabilizationModeSupported(AVCaptureVideoStabilizationModeStandard)) false else null,
            hdr = if (f.videoHDRSupported) d.videoHDREnabled else null,
        )
    }

    private inline fun configureDevice(block: AVCaptureDevice.() -> Unit) {
        val d = device ?: return
        if (d.lockForConfiguration(null)) { runCatching { d.block() }; d.unlockForConfiguration() }
    }

    // ── Frames ────────────────────────────────────────────────────────────
    private fun process(sample: CMSampleBufferRef) {
        val pb = CMSampleBufferGetImageBuffer(sample) ?: return
        val t = CMTimeGetSeconds(CMSampleBufferGetPresentationTimeStamp(sample))
        val dt = if (lastT == 0.0) 0.033f else (t - lastT).toFloat().coerceIn(0.001f, 0.2f); lastT = t
        frameNo++
        val fx = effects
        if (scanning && frameNo % 5 == 0L) detectBarcodes(pb)
        if (fx.autoFrame && frameNo % 3 == 0L) detectFaces(pb)
        val frame = pb.toFrame(maxSide = 960)
        val mask = if (fx.needsMask) segment(pb) else null
        val out = compositor.process(frame, fx, mask, lastFaces, dt, reducedMotion())
        picture.value = out.toImageBitmap()
        lastFrame = out
        val us = (t * 1_000_000).toLong()
        sinks.toList().forEach { it.onFrame(out, us) }
    }

    private fun segment(pb: CVPixelBufferRef): Mask? {
        val req = VNGeneratePersonSegmentationRequest().apply {
            qualityLevel = VNGeneratePersonSegmentationRequestQualityLevelBalanced
            outputPixelFormat = kCVPixelFormatType_OneComponent32Float
        }
        VNImageRequestHandler(pb, emptyMap<Any?, Any?>()).performRequests(listOf(req), null)
        val m = (req.results?.firstOrNull() as? VNPixelBufferObservation)?.pixelBuffer ?: return null
        CVPixelBufferLockBaseAddress(m, 1u)
        val w = CVPixelBufferGetWidth(m).toInt(); val h = CVPixelBufferGetHeight(m).toInt(); val stride = CVPixelBufferGetBytesPerRow(m).toInt()
        val base = CVPixelBufferGetBaseAddress(m)!!.reinterpret<ByteVar>()
        val v = FloatArray(w * h)
        for (y in 0 until h) { val row = (base + y * stride)!!.reinterpret<FloatVar>(); for (x in 0 until w) v[y * w + x] = row[x] }
        CVPixelBufferUnlockBaseAddress(m, 1u)
        return Mask(w, h, v)
    }

    private fun detectFaces(pb: CVPixelBufferRef) {
        val req = VNDetectFaceRectanglesRequest()
        VNImageRequestHandler(pb, emptyMap<Any?, Any?>()).performRequests(listOf(req), null)
        lastFaces = req.results?.mapNotNull { o ->
            (o as? VNFaceObservation)?.boundingBox?.useContents { NRect(origin.x.toFloat(), (1 - origin.y - size.height).toFloat(), size.width.toFloat(), size.height.toFloat()) }
        } ?: emptyList()
        faces.value = lastFaces
    }

    private fun detectBarcodes(pb: CVPixelBufferRef) {
        val req = VNDetectBarcodesRequest()
        VNImageRequestHandler(pb, emptyMap<Any?, Any?>()).performRequests(listOf(req), null)
        req.results?.forEach { (it as? VNBarcodeObservation)?.payloadStringValue?.let { s -> barcodes.tryEmit(s) } }
    }

    // ── Controls ──────────────────────────────────────────────────────────
    override fun stop() { dispatch_async(queue) { session.stopRunning() }; state.value = state.value.copy(ready = false) }
    override fun setEffects(effects: Effects) { this.effects = effects }
    override fun setScene(scene: SceneDocument?) { this.scene = scene }
    override fun setBarcodeScanning(on: Boolean) { scanning = on }
    override fun selectLens(id: String) { lensId = id; dispatch_async(queue) { configure() } }
    override fun switchFacing() { front = !front; lensId = null; dispatch_async(queue) { configure() } }
    override fun setZoom(factor: Float) = configureDevice { videoZoomFactor = factor.toDouble().coerceIn(minAvailableVideoZoomFactor, maxAvailableVideoZoomFactor); state.value = state.value.copy(zoom = factor) }
    override fun setTorch(on: Boolean) = configureDevice { if (hasTorch) torchMode = if (on) AVCaptureTorchModeOn else AVCaptureTorchModeOff; state.value = state.value.copy(torch = on) }
    override fun setEv(index: Int) = configureDevice { setExposureTargetBias(index / 3f, null); state.value = state.value.copy(ev = index) }
    override fun setIso(iso: Int?) = manualExposure(iso, state.value.shutter)
    override fun setShutter(ns: Long?) = manualExposure(state.value.iso, ns)
    private fun manualExposure(iso: Int?, ns: Long?) = configureDevice {
        if (iso == null && ns == null) exposureMode = AVCaptureExposureModeContinuousAutoExposure
        else setExposureModeCustomWithDuration(ns?.let { CMTimeMake(it, 1_000_000_000) } ?: AVCaptureExposureDurationCurrent,
            iso?.toFloat() ?: AVCaptureISOCurrent, null)
        state.value = state.value.copy(iso = iso, shutter = ns)
    }
    override fun setFocus(distance: Float?) = configureDevice {
        if (distance == null) focusMode = AVCaptureFocusModeContinuousAutoFocus else setFocusModeLockedWithLensPosition(1f - distance, null)
        state.value = state.value.copy(focus = distance)
    }
    override fun tapFocus(x: Float, y: Float) = configureDevice {
        val p = CGPointMake(y.toDouble(), 1.0 - x) // device coordinates are landscape
        if (focusPointOfInterestSupported) { focusPointOfInterest = p; focusMode = AVCaptureFocusModeAutoFocus }
        if (exposurePointOfInterestSupported) { exposurePointOfInterest = p; exposureMode = AVCaptureExposureModeContinuousAutoExposure }
    }
    override fun setWhiteBalance(mode: String) = configureDevice {
        val temp = mapOf("daylight" to 5500f, "cloudy" to 6500f, "tungsten" to 3200f, "fluorescent" to 4000f)[mode]
        when {
            mode == "auto" -> whiteBalanceMode = AVCaptureWhiteBalanceModeContinuousAutoWhiteBalance
            mode == "locked" -> whiteBalanceMode = AVCaptureWhiteBalanceModeLocked
            temp != null -> {
                val gains = deviceWhiteBalanceGainsForTemperatureAndTintValues(cValue { temperature = temp; tint = 0f })
                setWhiteBalanceModeLockedWithDeviceWhiteBalanceGains(gains, null)
            }
        }
        state.value = state.value.copy(wb = mode)
    }
    override fun setFps(fps: Int) = configureDevice {
        val d = if (fps == 0) kCMTimeInvalid else CMTimeMake(1, fps)
        activeVideoMinFrameDuration = d; activeVideoMaxFrameDuration = d
        state.value = state.value.copy(fps = fps)
    }
    override fun setHdr(on: Boolean) = configureDevice { automaticallyAdjustsVideoHDREnabled = false; videoHDREnabled = on; state.value = state.value.copy(hdr = on) }
    override fun setStabilization(on: Boolean) {
        output.connectionWithMediaType(AVMediaTypeVideo)?.preferredVideoStabilizationMode = if (on) AVCaptureVideoStabilizationModeStandard else AVCaptureVideoStabilizationModeOff
        state.value = state.value.copy(stab = on)
    }
    override fun setQuality(shortSide: Int) {
        dispatch_async(queue) {
            session.sessionPreset = when { shortSide >= 2160 -> AVCaptureSessionPreset3840x2160; shortSide >= 1080 -> AVCaptureSessionPreset1920x1080; else -> AVCaptureSessionPreset1280x720 }
        }
    }
}

// ── Frame conversions ────────────────────────────────────────────────────────

/** BGRA bytes read as a little-endian Int are 0xAARRGGBB, the shared Frame layout. */
fun CVPixelBufferRef.toFrame(maxSide: Int): Frame {
    CVPixelBufferLockBaseAddress(this, 1u)
    val w = CVPixelBufferGetWidth(this).toInt(); val h = CVPixelBufferGetHeight(this).toInt(); val stride = CVPixelBufferGetBytesPerRow(this).toInt()
    val step = maxOf(1, (maxOf(w, h) + maxSide - 1) / maxSide)
    val ow = w / step; val oh = h / step
    val px = IntArray(ow * oh)
    val base = CVPixelBufferGetBaseAddress(this)!!.reinterpret<ByteVar>()
    for (y in 0 until oh) {
        val row = (base + y * step * stride)!!.reinterpret<IntVar>()
        for (x in 0 until ow) px[y * ow + x] = row[x * step]
    }
    CVPixelBufferUnlockBaseAddress(this, 1u)
    return Frame(ow, oh, px)
}

fun Frame.toImageBitmap(): ImageBitmap {
    val bytes = ByteArray(pixels.size * 4)
    for (i in pixels.indices) {
        val c = pixels[i]
        bytes[i * 4] = c.toByte(); bytes[i * 4 + 1] = (c shr 8).toByte(); bytes[i * 4 + 2] = (c shr 16).toByte(); bytes[i * 4 + 3] = (c ushr 24).toByte()
    }
    val bmp = Bitmap()
    bmp.allocPixels(ImageInfo.makeN32(width, height, ColorAlphaType.PREMUL))
    bmp.installPixels(bytes)
    return bmp.asComposeImageBitmap()
}

/** Draws a frame into a UIImage (snapshots, JPEG for the PC). */
fun Frame.toUIImage(): UIImage? {
    val space = CGColorSpaceCreateDeviceRGB()
    return pixels.usePinned { p ->
        val ctx = CGBitmapContextCreate(p.addressOf(0), width.toULong(), height.toULong(), 8u, (width * 4).toULong(), space,
            CGImageAlphaInfo.kCGImageAlphaPremultipliedFirst.value or kCGBitmapByteOrder32Little)
        val img = CGBitmapContextCreateImage(ctx)
        CGContextRelease(ctx)
        img?.let { UIImage.imageWithCGImage(it).also { _ -> CGImageRelease(img) } }
    }.also { CGColorSpaceRelease(space) }
}

fun UIImage.toFrame(maxSide: Int): Frame? {
    val cg = CGImage ?: return null
    val sw = CGImageGetWidth(cg).toInt(); val sh = CGImageGetHeight(cg).toInt()
    val s = minOf(1.0, maxSide.toDouble() / maxOf(sw, sh))
    val w = (sw * s).toInt(); val h = (sh * s).toInt()
    val px = IntArray(w * h)
    val space = CGColorSpaceCreateDeviceRGB()
    px.usePinned { p ->
        val ctx = CGBitmapContextCreate(p.addressOf(0), w.toULong(), h.toULong(), 8u, (w * 4).toULong(), space,
            CGImageAlphaInfo.kCGImageAlphaPremultipliedFirst.value or kCGBitmapByteOrder32Little)
        CGContextDrawImage(ctx, CGRectMake(0.0, 0.0, w.toDouble(), h.toDouble()), cg)
        CGContextRelease(ctx)
    }
    CGColorSpaceRelease(space)
    return Frame(w, h, px)
}

/** A looping video background, pre-decoded at ~12 fps (up to 10 s). */
class VideoBackground(url: NSURL) : app.kissik.studio.platform.BackgroundSource {
    override val label = "Looping video"
    private val frames: List<Frame>
    init {
        val asset = AVURLAsset(url, null)
        val gen = AVAssetImageGenerator(asset).apply { appliesPreferredTrackTransform = true; maximumSize = CGSizeMake(640.0, 640.0) }
        val dur = minOf(CMTimeGetSeconds(asset.duration), 10.0)
        frames = (0 until (dur * 12).toInt().coerceAtLeast(1)).mapNotNull { i ->
            gen.copyCGImageAtTime(CMTimeMakeWithSeconds(i / 12.0, 600), null, null)?.let { UIImage.imageWithCGImage(it).toFrame(640) }
        }
    }
    override fun frameAt(timeMs: Long) = frames.getOrNull(((timeMs / 83) % frames.size.coerceAtLeast(1)).toInt())
    override fun close() {}
}
