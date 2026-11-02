package app.kissik.studio.image

import kotlin.math.abs
import kotlin.math.max
import kotlin.math.min
import kotlin.math.roundToInt

/**
 * A picture as packed ARGB pixels (0xAARRGGBB), row by row. The whole effects pipeline works on this one type so the
 * same code runs on Android and iOS; each platform only converts its camera buffers to and from it.
 */
class Frame(val width: Int, val height: Int, val pixels: IntArray = IntArray(width * height)) {
    init { require(pixels.size == width * height) { "pixels must be width × height" } }
    operator fun get(x: Int, y: Int) = pixels[y * width + x]
    operator fun set(x: Int, y: Int, v: Int) { pixels[y * width + x] = v }
    fun copy() = Frame(width, height, pixels.copyOf())

    companion object {
        fun solid(w: Int, h: Int, argb: Int) = Frame(w, h, IntArray(w * h) { argb })
    }
}

/** A per-pixel weight 0..1 (1 = person), the output of segmentation. */
class Mask(val width: Int, val height: Int, val values: FloatArray = FloatArray(width * height)) {
    operator fun get(x: Int, y: Int) = values[y * width + x]
}

inline fun a(c: Int) = c ushr 24
inline fun r(c: Int) = (c shr 16) and 0xFF
inline fun g(c: Int) = (c shr 8) and 0xFF
inline fun b(c: Int) = c and 0xFF
fun argb(a: Int, r: Int, g: Int, b: Int): Int =
    (clamp255(a) shl 24) or (clamp255(r) shl 16) or (clamp255(g) shl 8) or clamp255(b)
fun clamp255(v: Int) = if (v < 0) 0 else if (v > 255) 255 else v
fun clamp01(v: Float) = if (v < 0f) 0f else if (v > 1f) 1f else v
fun luma(c: Int) = (r(c) * 77 + g(c) * 150 + b(c) * 29) shr 8

/** Parses "#RGB", "#RRGGBB" or "#AARRGGBB" (the desktop scene format). Unknown text gives [fallback]. */
fun parseColor(text: String?, fallback: Int = 0xFFFFFFFF.toInt()): Int {
    val s = text?.trim()?.removePrefix("#") ?: return fallback
    return when (s.length) {
        3 -> s.map { "$it$it" }.joinToString("").toLongOrNull(16)?.let { (0xFF000000 or it).toInt() }
        6 -> s.toLongOrNull(16)?.let { (0xFF000000 or it).toInt() }
        8 -> s.toLongOrNull(16)?.toInt()
        else -> null
    } ?: fallback
}

fun lerpColor(x: Int, y: Int, t: Float): Int {
    val u = 1 - t
    return argb(
        (a(x) * u + a(y) * t).roundToInt(), (r(x) * u + r(y) * t).roundToInt(),
        (g(x) * u + g(y) * t).roundToInt(), (b(x) * u + b(y) * t).roundToInt(),
    )
}

/** Separable box blur, run [passes] times (3 passes ≈ Gaussian). O(pixels) whatever the radius. */
fun Frame.boxBlur(radius: Int, passes: Int = 3): Frame {
    if (radius <= 0) return copy()
    var src = pixels.copyOf()
    var tmp = IntArray(src.size)
    repeat(passes) {
        blurLine(src, tmp, width, height, radius, horizontal = true)
        blurLine(tmp, src, width, height, radius, horizontal = false)
    }
    return Frame(width, height, src)
}

private fun blurLine(src: IntArray, dst: IntArray, w: Int, h: Int, rad: Int, horizontal: Boolean) {
    val lines = if (horizontal) h else w
    val len = if (horizontal) w else h
    val div = rad * 2 + 1
    for (line in 0 until lines) {
        fun idx(i: Int): Int { val c = min(len - 1, max(0, i)); return if (horizontal) line * w + c else c * w + line }
        var sa = 0; var sr = 0; var sg = 0; var sb = 0
        for (i in -rad..rad) { val c = src[idx(i)]; sa += a(c); sr += r(c); sg += g(c); sb += b(c) }
        for (i in 0 until len) {
            dst[idx(i)] = argb(sa / div, sr / div, sg / div, sb / div)
            val out = src[idx(i - rad)]; val inn = src[idx(i + rad + 1)]
            sa += a(inn) - a(out); sr += r(inn) - r(out); sg += g(inn) - g(out); sb += b(inn) - b(out)
        }
    }
}

/** Nearest/bilinear resample to a new size. */
fun Frame.scaled(w: Int, h: Int): Frame {
    if (w == width && h == height) return copy()
    val out = IntArray(w * h)
    val sx = width.toFloat() / w; val sy = height.toFloat() / h
    for (y in 0 until h) {
        val fy = ((y + 0.5f) * sy - 0.5f).coerceIn(0f, (height - 1).toFloat())
        val y0 = fy.toInt(); val y1 = min(height - 1, y0 + 1); val ty = fy - y0
        for (x in 0 until w) {
            val fx = ((x + 0.5f) * sx - 0.5f).coerceIn(0f, (width - 1).toFloat())
            val x0 = fx.toInt(); val x1 = min(width - 1, x0 + 1); val tx = fx - x0
            val top = lerpColor(this[x0, y0], this[x1, y0], tx)
            val bot = lerpColor(this[x0, y1], this[x1, y1], tx)
            out[y * w + x] = lerpColor(top, bot, ty)
        }
    }
    return Frame(w, h, out)
}

/** Scales [this] to cover [w]×[h] (centre crop), like a picture used as a background. */
fun Frame.cover(w: Int, h: Int): Frame {
    val s = max(w.toFloat() / width, h.toFloat() / height)
    val cw = (w / s).roundToInt().coerceIn(1, width); val ch = (h / s).roundToInt().coerceIn(1, height)
    return crop((width - cw) / 2, (height - ch) / 2, cw, ch).scaled(w, h)
}

fun Frame.crop(x: Int, y: Int, w: Int, h: Int): Frame {
    val out = IntArray(w * h)
    for (j in 0 until h) for (i in 0 until w) {
        out[j * w + i] = this[(x + i).coerceIn(0, width - 1), (y + j).coerceIn(0, height - 1)]
    }
    return Frame(w, h, out)
}

fun Frame.mirrored(): Frame {
    val out = IntArray(pixels.size)
    for (y in 0 until height) for (x in 0 until width) out[y * width + x] = this[width - 1 - x, y]
    return Frame(width, height, out)
}

/** Rotates clockwise by 0/90/180/270 degrees. */
fun Frame.rotated(degrees: Int): Frame = when (((degrees % 360) + 360) % 360) {
    90 -> Frame(height, width).also { o -> for (y in 0 until height) for (x in 0 until width) o[height - 1 - y, x] = this[x, y] }
    180 -> Frame(width, height, pixels.reversedArray())
    270 -> Frame(height, width).also { o -> for (y in 0 until height) for (x in 0 until width) o[y, width - 1 - x] = this[x, y] }
    else -> copy()
}

fun Mask.scaled(w: Int, h: Int): Mask {
    if (w == width && h == height) return this
    val out = FloatArray(w * h)
    val sx = width.toFloat() / w; val sy = height.toFloat() / h
    for (y in 0 until h) {
        val fy = ((y + 0.5f) * sy - 0.5f).coerceIn(0f, (height - 1).toFloat())
        val y0 = fy.toInt(); val y1 = min(height - 1, y0 + 1); val ty = fy - y0
        for (x in 0 until w) {
            val fx = ((x + 0.5f) * sx - 0.5f).coerceIn(0f, (width - 1).toFloat())
            val x0 = fx.toInt(); val x1 = min(width - 1, x0 + 1); val tx = fx - x0
            val top = this[x0, y0] * (1 - tx) + this[x1, y0] * tx
            val bot = this[x0, y1] * (1 - tx) + this[x1, y1] * tx
            out[y * w + x] = top * (1 - ty) + bot * ty
        }
    }
    return Mask(w, h, out)
}

/**
 * Edge refinement: feathers the raw mask with a small blur, then pulls it towards 0/1 with a smoothstep so hair-line
 * noise disappears while the outline stays soft. [previous] blends with the last frame's mask to stop flicker.
 */
fun Mask.refined(feather: Int = 2, low: Float = 0.3f, high: Float = 0.7f, previous: Mask? = null, keep: Float = 0.35f): Mask {
    val blurred = if (feather > 0) blurMask(this, feather) else values.copyOf()
    val out = FloatArray(blurred.size)
    val prev = previous?.takeIf { it.width == width && it.height == height }
    for (i in blurred.indices) {
        val t = clamp01((blurred[i] - low) / (high - low))
        val s = t * t * (3 - 2 * t)
        out[i] = if (prev != null) prev.values[i] * keep + s * (1 - keep) else s
    }
    return Mask(width, height, out)
}

private fun blurMask(m: Mask, rad: Int): FloatArray {
    val w = m.width; val h = m.height
    val a = m.values.copyOf(); val t = FloatArray(a.size); val div = (rad * 2 + 1).toFloat()
    for (y in 0 until h) {
        var s = 0f
        for (i in -rad..rad) s += a[y * w + i.coerceIn(0, w - 1)]
        for (x in 0 until w) {
            t[y * w + x] = s / div
            s += a[y * w + (x + rad + 1).coerceIn(0, w - 1)] - a[y * w + (x - rad).coerceIn(0, w - 1)]
        }
    }
    for (x in 0 until w) {
        var s = 0f
        for (i in -rad..rad) s += t[i.coerceIn(0, h - 1) * w + x]
        for (y in 0 until h) {
            a[y * w + x] = s / div
            s += t[(y + rad + 1).coerceIn(0, h - 1) * w + x] - t[(y - rad).coerceIn(0, h - 1) * w + x]
        }
    }
    return a
}

/** Person over background, weighted by [mask] (same size as the frames). */
fun composite(person: Frame, background: Frame, mask: Mask): Frame {
    val out = IntArray(person.pixels.size)
    for (i in out.indices) {
        val m = mask.values[i]
        out[i] = when {
            m >= 0.999f -> person.pixels[i]
            m <= 0.001f -> background.pixels[i]
            else -> lerpColor(background.pixels[i], person.pixels[i], m)
        }
    }
    return Frame(person.width, person.height, out)
}

/**
 * Light wrap: lets the new background's light spill a little over the person's edge, so a cut-out sits in the scene
 * instead of looking pasted on. [strength] 0..1.
 */
fun lightWrap(composited: Frame, background: Frame, mask: Mask, strength: Float = 0.35f, radius: Int = 6): Frame {
    if (strength <= 0f) return composited
    val glow = background.boxBlur(radius, passes = 2)
    val out = composited.pixels.copyOf()
    for (i in out.indices) {
        val m = mask.values[i]
        // Strongest just inside the edge (m around 0.5..0.9), none deep inside the person or in the background.
        val edge = if (m <= 0.02f || m >= 0.98f) 0f else (1 - abs(m - 0.6f) / 0.6f).coerceAtLeast(0f)
        val k = edge * strength
        if (k > 0f) out[i] = lerpColor(out[i], screen(out[i], glow.pixels[i]), k)
    }
    return Frame(composited.width, composited.height, out)
}

private fun screen(x: Int, y: Int) = argb(
    255, 255 - (255 - r(x)) * (255 - r(y)) / 255, 255 - (255 - g(x)) * (255 - g(y)) / 255, 255 - (255 - b(x)) * (255 - b(y)) / 255,
)

/**
 * Green screen: a mask from how far each pixel is from the key colour, with spill suppression (pulls leftover key
 * colour out of the edges). [tolerance] and [softness] are 0..1.
 */
fun chromaKeyMask(frame: Frame, key: Int = 0xFF00B140.toInt(), tolerance: Float = 0.28f, softness: Float = 0.12f): Mask {
    val kcb = cb(key); val kcr = cr(key)
    val out = FloatArray(frame.pixels.size)
    for (i in out.indices) {
        val c = frame.pixels[i]
        val d = kotlin.math.sqrt(((cb(c) - kcb) * (cb(c) - kcb) + (cr(c) - kcr) * (cr(c) - kcr)).toFloat()) / 180f
        out[i] = clamp01((d - tolerance) / softness.coerceAtLeast(0.001f))
    }
    return Mask(frame.width, frame.height, out)
}

fun despill(frame: Frame): Frame {
    val out = IntArray(frame.pixels.size)
    for (i in out.indices) {
        val c = frame.pixels[i]
        val limit = (r(c) + b(c)) / 2
        out[i] = if (g(c) > limit) argb(a(c), r(c), limit, b(c)) else c
    }
    return Frame(frame.width, frame.height, out)
}

private fun cb(c: Int) = (-43 * r(c) - 85 * g(c) + 128 * b(c)) shr 8
private fun cr(c: Int) = (128 * r(c) - 107 * g(c) - 21 * b(c)) shr 8
