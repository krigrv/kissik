package app.kissik.studio.android

import android.content.Context
import android.graphics.Bitmap
import app.kissik.studio.framing.NRect
import app.kissik.studio.image.Mask
import com.google.android.gms.tasks.Tasks
import com.google.mediapipe.framework.image.BitmapImageBuilder
import com.google.mediapipe.framework.image.ByteBufferExtractor
import com.google.mediapipe.tasks.core.BaseOptions
import com.google.mediapipe.tasks.vision.core.RunningMode
import com.google.mediapipe.tasks.vision.facedetector.FaceDetector
import com.google.mediapipe.tasks.vision.imagesegmenter.ImageSegmenter
import com.google.mlkit.vision.common.InputImage
import com.google.mlkit.vision.face.FaceDetection
import com.google.mlkit.vision.face.FaceDetectorOptions
import com.google.mlkit.vision.segmentation.Segmentation
import com.google.mlkit.vision.segmentation.selfie.SelfieSegmenterOptions
import java.nio.ByteBuffer
import java.nio.ByteOrder
import java.util.concurrent.TimeUnit

/** Finds the person (mask) and faces in a frame. Two engines: MediaPipe (Apache-2.0, default) and ML Kit. */
interface PersonVision {
    fun mask(bmp: Bitmap): Mask?
    fun faces(bmp: Bitmap): List<NRect>
    fun close()

    companion object {
        const val MEDIAPIPE = "mediapipe"
        const val MLKIT = "mlkit"
        fun create(ctx: Context, engine: String): PersonVision =
            if (engine == MLKIT) MlKitVision() else runCatching { MediaPipeVision(ctx) }.getOrElse { MlKitVision() }
    }
}

/** MediaPipe Tasks: selfie segmenter + BlazeFace short-range, both bundled as assets (Apache-2.0 models). */
class MediaPipeVision(ctx: Context) : PersonVision {
    private fun model(ctx: Context, name: String): ByteBuffer {
        val bytes = ctx.assets.open("mediapipe/$name").use { it.readBytes() }
        return ByteBuffer.allocateDirect(bytes.size).order(ByteOrder.nativeOrder()).put(bytes).also { it.rewind() }
    }
    private val segmenter = ImageSegmenter.createFromOptions(ctx, ImageSegmenter.ImageSegmenterOptions.builder()
        .setBaseOptions(BaseOptions.builder().setModelAssetBuffer(model(ctx, "selfie_segmenter.tflite")).build())
        .setRunningMode(RunningMode.IMAGE).setOutputConfidenceMasks(true).setOutputCategoryMask(false).build())
    private val detector = FaceDetector.createFromOptions(ctx, FaceDetector.FaceDetectorOptions.builder()
        .setBaseOptions(BaseOptions.builder().setModelAssetBuffer(model(ctx, "blaze_face_short_range.tflite")).build())
        .setRunningMode(RunningMode.IMAGE).setMinDetectionConfidence(0.5f).build())

    override fun mask(bmp: Bitmap): Mask? = runCatching {
        val res = segmenter.segment(BitmapImageBuilder(bmp).build())
        val masks = res.confidenceMasks().orElse(null) ?: return null
        val m = masks.last() // the person channel (single-channel model: the only one)
        val buf = ByteBufferExtractor.extract(m).order(ByteOrder.nativeOrder()).asFloatBuffer()
        val v = FloatArray(m.width * m.height); buf.get(v)
        Mask(m.width, m.height, v)
    }.getOrNull()

    override fun faces(bmp: Bitmap): List<NRect> = runCatching {
        detector.detect(BitmapImageBuilder(bmp).build()).detections().map { d ->
            val r = d.boundingBox()
            NRect(r.left / bmp.width, r.top / bmp.height, r.width() / bmp.width, r.height() / bmp.height)
        }
    }.getOrDefault(emptyList())

    override fun close() { segmenter.close(); detector.close() }
}

/** Google ML Kit: selfie segmentation (bundled) + face detection (via Play services). */
class MlKitVision : PersonVision {
    private val segmenter = Segmentation.getClient(SelfieSegmenterOptions.Builder().setDetectorMode(SelfieSegmenterOptions.STREAM_MODE).build())
    private val detector = FaceDetection.getClient(FaceDetectorOptions.Builder().setPerformanceMode(FaceDetectorOptions.PERFORMANCE_MODE_FAST).build())

    override fun mask(bmp: Bitmap): Mask? = runCatching {
        val r = Tasks.await(segmenter.process(InputImage.fromBitmap(bmp, 0)), 2, TimeUnit.SECONDS)
        val v = FloatArray(r.width * r.height); r.buffer.asFloatBuffer().get(v)
        Mask(r.width, r.height, v)
    }.getOrNull()

    override fun faces(bmp: Bitmap): List<NRect> = runCatching {
        Tasks.await(detector.process(InputImage.fromBitmap(bmp, 0)), 2, TimeUnit.SECONDS).map { f ->
            val r = f.boundingBox
            NRect(r.left / bmp.width.toFloat(), r.top / bmp.height.toFloat(), r.width() / bmp.width.toFloat(), r.height() / bmp.height.toFloat())
        }
    }.getOrDefault(emptyList())

    override fun close() { segmenter.close(); detector.close() }
}
