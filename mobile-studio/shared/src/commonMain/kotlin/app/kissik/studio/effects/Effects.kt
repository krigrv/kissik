package app.kissik.studio.effects

import app.kissik.studio.framing.AutoFramer
import app.kissik.studio.framing.ManualFraming
import app.kissik.studio.framing.NRect
import app.kissik.studio.image.*
import kotlin.math.roundToInt

enum class BackgroundMode(val label: String) { None("Off"), Blur("Blur"), Replace("Replace"), Remove("Remove"), GreenScreen("Green screen") }

/** Everything the picture pipeline does to a frame. Immutable: the UI swaps in a new copy. */
data class Effects(
    val background: BackgroundMode = BackgroundMode.None,
    val blurStrength: Float = 0.6f,               // 0..1
    val backgroundColor: Int = 0xFF1A1D22.toInt(), // Remove
    val backgroundImage: Frame? = null,            // Replace (a picture, or the current frame of a looping video)
    val edgeRefine: Boolean = true,
    val lightWrap: Float = 0.3f,
    val keyColor: Int = 0xFF00B140.toInt(),
    val keyTolerance: Float = 0.28f,
    val filter: Filter = Filter.None,
    val adjust: Adjustments = Adjustments(),
    val lut: Lut3D? = null,
    val lutStrength: Float = 1f,
    val autoBest: Boolean = false,
    val smoothing: Float = 0f,
    val autoFrame: Boolean = false,
    val manual: ManualFraming = ManualFraming(),
    val vertical: Boolean = false,
) {
    /** True when the processed (CPU) path is needed; otherwise the platform shows the camera preview directly. */
    val needsProcessing get() = background != BackgroundMode.None || filter != Filter.None || !adjust.isNeutral ||
        lut != null || autoBest || smoothing > 0f || autoFrame || manual != ManualFraming() || vertical
    val needsMask get() = background == BackgroundMode.Blur || background == BackgroundMode.Replace || background == BackgroundMode.Remove
}

/**
 * The shared picture pipeline. Platforms call [process] with an upright camera frame, the person mask from their own
 * segmenter (Android ML Kit, iOS Vision) and face boxes; the result is what is shown, recorded, streamed and sent to
 * the PC. Order: framing crop → background → looks → retouch.
 */
class Compositor {
    private var lastMask: Mask? = null
    private var auto = AutoCorrection(floatArrayOf(1f, 1f, 1f), 0f, 0f)
    private var framer: AutoFramer? = null
    private var framerAspect = 0f
    var lastCrop: NRect = NRect.Full; private set

    fun process(input: Frame, fx: Effects, mask: Mask?, faces: List<NRect>, dt: Float, reducedMotion: Boolean = false): Frame {
        var frame = input
        var m = mask

        // Framing (crop in source coordinates, mask cropped alongside).
        // Output keeps the camera's own shape (portrait when the phone is upright) unless 9:16 is asked for.
        val srcAspect = input.width.toFloat() / input.height
        val outAspect = if (fx.vertical) minOf(9f / 16f, srcAspect) else srcAspect
        val crop = when {
            fx.autoFrame -> {
                val rel = outAspect / srcAspect
                if (framer == null || framerAspect != rel) { framer = AutoFramer(rel); framerAspect = rel }
                framer!!.reducedMotion = reducedMotion
                framer!!.update(faces, dt)
            }
            fx.vertical || fx.manual != ManualFraming() -> {
                val c = fx.manual.crop()
                val rel = outAspect / srcAspect
                if (rel < 1f) NRect(c.x + c.w * (1 - rel) / 2, c.y, c.w * rel, c.h) else c
            }
            else -> NRect.Full
        }
        lastCrop = crop
        if (crop != NRect.Full) {
            val x = (crop.x * input.width).roundToInt(); val y = (crop.y * input.height).roundToInt()
            val w = (crop.w * input.width).roundToInt().coerceAtLeast(2); val h = (crop.h * input.height).roundToInt().coerceAtLeast(2)
            // Keep the output size steady while zooming: scale the crop back up to a fixed size.
            val outW = if (outAspect < srcAspect) (input.height * outAspect).roundToInt() else input.width
            val outH = input.height
            frame = frame.crop(x, y, w, h).scaled(outW, outH)
            m = m?.let { cropMask(it, crop).scaled(outW, outH) }
        }
        if (fx.manual.mirror) { frame = frame.mirrored(); m = m?.let { mirrorMask(it) } }
        if (fx.manual.rotation != 0) frame = frame.rotated(fx.manual.rotation)

        // Background.
        when (fx.background) {
            BackgroundMode.None -> {}
            BackgroundMode.GreenScreen -> {
                val key = chromaKeyMask(frame, fx.keyColor, fx.keyTolerance)
                val bg = fx.backgroundImage?.cover(frame.width, frame.height) ?: Frame.solid(frame.width, frame.height, fx.backgroundColor)
                frame = composite(despill(frame), bg, key)
            }
            else -> if (m != null && fx.manual.rotation == 0) {
                val mm = m.scaled(frame.width, frame.height).let { if (fx.edgeRefine) it.refined(previous = lastMask) else it }
                lastMask = mm
                val bg = when (fx.background) {
                    BackgroundMode.Blur -> frame.scaled(frame.width / 4, frame.height / 4)
                        .boxBlur((2 + fx.blurStrength * 10).roundToInt()).scaled(frame.width, frame.height)
                    BackgroundMode.Replace -> fx.backgroundImage?.cover(frame.width, frame.height)
                        ?: Frame.solid(frame.width, frame.height, fx.backgroundColor)
                    else -> Frame.solid(frame.width, frame.height, fx.backgroundColor)
                }
                frame = composite(frame, bg, mm)
                if (fx.background == BackgroundMode.Replace && fx.lightWrap > 0f) frame = lightWrap(frame, bg, mm, fx.lightWrap)
            }
        }

        // Looks.
        if (fx.autoBest) auto = auto.toward(analyzeAuto(frame), 0.08f)
        val adj = combine(fx.filter.adjust, fx.adjust, if (fx.autoBest) auto.toAdjustments() else null)
        frame = frame.adjusted(adj, fx.filter.mono, if (fx.autoBest) auto.gains else floatArrayOf(1f, 1f, 1f))
        fx.lut?.let { frame = it.apply(frame, fx.lutStrength) }
        if (fx.smoothing > 0f) frame = frame.smoothedSkin(fx.smoothing)
        return frame
    }

    private fun combine(a: Adjustments, b: Adjustments, c: Adjustments?) = Adjustments(
        brightness = a.brightness + b.brightness + (c?.brightness ?: 0f),
        contrast = a.contrast + b.contrast + (c?.contrast ?: 0f),
        saturation = a.saturation + b.saturation,
        temperature = a.temperature + b.temperature,
        tint = a.tint + b.tint,
        vignette = maxOf(a.vignette, b.vignette),
    )

    private fun cropMask(m: Mask, c: NRect): Mask {
        val x0 = (c.x * m.width).roundToInt(); val y0 = (c.y * m.height).roundToInt()
        val w = (c.w * m.width).roundToInt().coerceAtLeast(1); val h = (c.h * m.height).roundToInt().coerceAtLeast(1)
        return Mask(w, h, FloatArray(w * h) { i -> m[(x0 + i % w).coerceIn(0, m.width - 1), (y0 + i / w).coerceIn(0, m.height - 1)] })
    }

    private fun mirrorMask(m: Mask) = Mask(m.width, m.height, FloatArray(m.values.size) { i ->
        val x = i % m.width; val y = i / m.width; m[m.width - 1 - x, y]
    })
}
