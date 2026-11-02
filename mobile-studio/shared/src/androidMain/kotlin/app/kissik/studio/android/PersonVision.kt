package app.kissik.studio.android

import android.content.Context
import android.graphics.Bitmap
import app.kissik.studio.framing.NRect
import app.kissik.studio.image.Mask
import com.google.mediapipe.framework.image.BitmapImageBuilder
import com.google.mediapipe.framework.image.ByteBufferExtractor
import com.google.mediapipe.tasks.core.BaseOptions
import com.google.mediapipe.tasks.vision.core.RunningMode
import com.google.mediapipe.tasks.vision.facedetector.FaceDetector
import com.google.mediapipe.tasks.vision.imagesegmenter.ImageSegmenter
import java.nio.ByteBuffer
import java.nio.ByteOrder

/** Finds the person (mask) and faces in a frame. */
interface PersonVision {
    fun mask(bmp: Bitmap): Mask?
    fun faces(bmp: Bitmap): List<NRect>
    fun close()

    companion object {
        /** MediaPipe; if it can't load on this phone, effects that need a person mask simply pass the picture through. */
        fun create(ctx: Context): PersonVision = runCatching { MediaPipeVision(ctx) }.getOrElse {
            android.util.Log.w("KISSIK", "MediaPipe unavailable", it)
            object : PersonVision {
                override fun mask(bmp: Bitmap): Mask? = null
                override fun faces(bmp: Bitmap): List<NRect> = emptyList()
                override fun close() {}
            }
        }
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
