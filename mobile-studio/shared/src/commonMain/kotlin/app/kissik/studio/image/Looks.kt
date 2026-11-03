package app.kissik.studio.image

import kotlin.math.max
import kotlin.math.min
import kotlin.math.pow
import kotlin.math.roundToInt

/** Manual adjustments, all centred on 0 (no change) and in -1..1 unless noted. */
data class Adjustments(
    val brightness: Float = 0f,
    val contrast: Float = 0f,
    val saturation: Float = 0f,
    val temperature: Float = 0f, // − cooler, + warmer
    val tint: Float = 0f,        // − green, + magenta
    val vignette: Float = 0f,    // 0..1
) {
    val isNeutral get() = this == Adjustments()
}

/** Built-in looks: our own colour grades, defined as adjustments plus an optional tone curve. */
enum class Filter(val label: String, val adjust: Adjustments, val mono: Boolean = false) {
    None("None", Adjustments()),
    Warm("Warm", Adjustments(temperature = 0.35f, saturation = 0.1f)),
    Cool("Cool", Adjustments(temperature = -0.35f)),
    Vivid("Vivid", Adjustments(contrast = 0.2f, saturation = 0.45f)),
    Soft("Soft", Adjustments(contrast = -0.2f, brightness = 0.05f, saturation = -0.1f)),
    Film("Film", Adjustments(contrast = 0.15f, saturation = -0.15f, temperature = 0.15f, vignette = 0.35f)),
    Studio("Studio", Adjustments(contrast = 0.1f, brightness = 0.04f)),
    Mono("Mono", Adjustments(contrast = 0.15f), mono = true),
    Noir("Noir", Adjustments(contrast = 0.45f, vignette = 0.5f), mono = true),
}

/** Precomputes a 256-entry table per channel for adjustments, so per-pixel work is three lookups. */
class ColorTable(adj: Adjustments, gains: FloatArray = floatArrayOf(1f, 1f, 1f)) {
    val r = IntArray(256); val g = IntArray(256); val b = IntArray(256)
    init {
        val c = 1f + adj.contrast
        val br = adj.brightness * 80f
        val tr = adj.temperature * 30f - adj.tint * -10f
        val tg = -adj.tint * 20f
        val tb = -adj.temperature * 30f + adj.tint * 10f
        for (i in 0..255) {
            fun f(v: Float, gain: Float, shift: Float) = clamp255((((v * gain) - 128f) * c + 128f + br + shift).roundToInt())
            r[i] = f(i.toFloat(), gains[0], tr); g[i] = f(i.toFloat(), gains[1], tg); b[i] = f(i.toFloat(), gains[2], tb)
        }
    }
}

fun Frame.adjusted(adj: Adjustments, mono: Boolean = false, gains: FloatArray = floatArrayOf(1f, 1f, 1f)): Frame {
    if (adj.isNeutral && !mono && gains.all { it == 1f }) return this
    val t = ColorTable(adj, gains)
    val sat = 1f + adj.saturation
    val out = IntArray(pixels.size)
    val cx = width / 2f; val cy = height / 2f; val maxD = cx * cx + cy * cy
    for (y in 0 until height) for (x in 0 until width) {
        val i = y * width + x
        val c = pixels[i]
        var rr = t.r[r(c)]; var gg = t.g[g(c)]; var bb = t.b[b(c)]
        if (mono || sat != 1f) {
            val l = (rr * 77 + gg * 150 + bb * 29) shr 8
            val s = if (mono) 0f else sat
            rr = clamp255((l + (rr - l) * s).roundToInt()); gg = clamp255((l + (gg - l) * s).roundToInt()); bb = clamp255((l + (bb - l) * s).roundToInt())
        }
        if (adj.vignette > 0f) {
            val d = ((x - cx) * (x - cx) + (y - cy) * (y - cy)) / maxD
            val k = 1f - adj.vignette * d * d
            rr = (rr * k).roundToInt(); gg = (gg * k).roundToInt(); bb = (bb * k).roundToInt()
        }
        out[i] = argb(a(c), rr, gg, bb)
    }
    return Frame(width, height, out)
}

/**
 * Auto best picture: measures the frame and returns corrections for exposure (towards mid-grey), white balance
 * (grey-world) and contrast (stretch the 1st..99th percentile). Smoothed by the caller over time.
 */
data class AutoCorrection(val gains: FloatArray, val brightness: Float, val contrast: Float) {
    fun toAdjustments() = Adjustments(brightness = brightness, contrast = contrast)
    override fun equals(other: Any?) = other is AutoCorrection && gains.contentEquals(other.gains) &&
        brightness == other.brightness && contrast == other.contrast
    override fun hashCode() = gains.contentHashCode() * 31 + brightness.hashCode() * 7 + contrast.hashCode()
}

fun analyzeAuto(frame: Frame, step: Int = 4): AutoCorrection {
    var sr = 0L; var sg = 0L; var sb = 0L; var n = 0
    val hist = IntArray(256)
    var y = 0
    while (y < frame.height) {
        var x = 0
        while (x < frame.width) {
            val c = frame[x, y]; sr += r(c); sg += g(c); sb += b(c); hist[luma(c)]++; n++
            x += step
        }
        y += step
    }
    if (n == 0) return AutoCorrection(floatArrayOf(1f, 1f, 1f), 0f, 0f)
    val ar = sr.toFloat() / n; val ag = sg.toFloat() / n; val ab = sb.toFloat() / n
    val grey = (ar + ag + ab) / 3f
    fun gain(v: Float) = if (v < 1f) 1f else (grey / v).coerceIn(0.75f, 1.33f)
    // Percentiles for contrast and mean for exposure.
    fun pct(p: Float): Int { var acc = 0; val target = n * p; for (i in 0..255) { acc += hist[i]; if (acc >= target) return i }; return 255 }
    val lo = pct(0.01f); val hi = pct(0.99f)
    val range = max(1, hi - lo)
    val contrast = ((255f / range) - 1f).coerceIn(-0.2f, 0.5f)
    val brightness = ((118f - grey) / 160f).coerceIn(-0.3f, 0.4f)
    return AutoCorrection(floatArrayOf(gain(ar), gain(ag), gain(ab)), brightness, contrast)
}

/** Blends two corrections (temporal smoothing so Auto best never pumps). */
fun AutoCorrection.toward(next: AutoCorrection, t: Float) = AutoCorrection(
    FloatArray(3) { gains[it] + (next.gains[it] - gains[it]) * t },
    brightness + (next.brightness - brightness) * t, contrast + (next.contrast - contrast) * t,
)

/**
 * Skin smoothing: blurs only where the pixel looks like skin (YCbCr skin range) and close in colour to its blurred
 * neighbourhood, so edges, eyes and hair stay sharp. [amount] 0..1.
 */
fun Frame.smoothedSkin(amount: Float, radius: Int = 4): Frame {
    if (amount <= 0f) return this
    val soft = boxBlur(radius, passes = 2)
    val out = IntArray(pixels.size)
    for (i in out.indices) {
        val c = pixels[i]; val s = soft.pixels[i]
        val y = luma(c)
        val cb = 128 + ((-43 * r(c) - 85 * g(c) + 128 * b(c)) shr 8)
        val cr = 128 + ((128 * r(c) - 107 * g(c) - 21 * b(c)) shr 8)
        val skin = y > 40 && cb in 77..127 && cr in 133..173
        val diff = kotlin.math.abs(luma(s) - y)
        val k = if (skin) amount * (1f - (diff / 40f).coerceAtMost(1f)) else 0f
        out[i] = if (k > 0f) lerpColor(c, s, k) else c
    }
    return Frame(width, height, out)
}

/** A 3D colour lookup table (the Adobe/Resolve .cube text format), applied with trilinear interpolation. */
class Lut3D(val size: Int, val data: FloatArray, val title: String = "") {
    fun apply(frame: Frame, strength: Float = 1f): Frame {
        val out = IntArray(frame.pixels.size)
        val n = size - 1
        for (i in out.indices) {
            val c = frame.pixels[i]
            val fr = r(c) / 255f * n; val fg = g(c) / 255f * n; val fb = b(c) / 255f * n
            val r0 = fr.toInt(); val g0 = fg.toInt(); val b0 = fb.toInt()
            val r1 = min(n, r0 + 1); val g1 = min(n, g0 + 1); val b1 = min(n, b0 + 1)
            val tr = fr - r0; val tg = fg - g0; val tb = fb - b0
            val rgb = FloatArray(3)
            for (ch in 0..2) {
                fun v(ri: Int, gi: Int, bi: Int) = data[((bi * size + gi) * size + ri) * 3 + ch]
                val c00 = v(r0, g0, b0) * (1 - tr) + v(r1, g0, b0) * tr
                val c10 = v(r0, g1, b0) * (1 - tr) + v(r1, g1, b0) * tr
                val c01 = v(r0, g0, b1) * (1 - tr) + v(r1, g0, b1) * tr
                val c11 = v(r0, g1, b1) * (1 - tr) + v(r1, g1, b1) * tr
                rgb[ch] = (c00 * (1 - tg) + c10 * tg) * (1 - tb) + (c01 * (1 - tg) + c11 * tg) * tb
            }
            val graded = argb(a(c), (rgb[0] * 255).roundToInt(), (rgb[1] * 255).roundToInt(), (rgb[2] * 255).roundToInt())
            out[i] = if (strength >= 1f) graded else lerpColor(c, graded, strength)
        }
        return Frame(frame.width, frame.height, out)
    }

    companion object {
        /** Parses .cube text (LUT_3D_SIZE, optional TITLE / DOMAIN lines, then size³ "r g b" rows, red fastest). */
        fun parseCube(text: String): Lut3D {
            var size = 0; var title = ""
            val values = ArrayList<Float>()
            for (raw in text.lineSequence()) {
                val line = raw.substringBefore('#').trim()
                if (line.isEmpty()) continue
                when {
                    line.startsWith("TITLE") -> title = line.removePrefix("TITLE").trim().trim('"')
                    line.startsWith("LUT_3D_SIZE") -> size = line.split(Regex("\\s+"))[1].toInt()
                    line.startsWith("LUT_1D_SIZE") -> throw IllegalArgumentException("1D LUTs are not supported")
                    line[0].isLetter() -> {} // DOMAIN_MIN / DOMAIN_MAX and other keywords
                    else -> line.split(Regex("\\s+")).take(3).forEach { values += it.toFloat() }
                }
            }
            require(size in 2..128) { "LUT_3D_SIZE missing or out of range" }
            require(values.size == size * size * size * 3) { "Expected ${size * size * size} rows, found ${values.size / 3}" }
            return Lut3D(size, values.toFloatArray(), title)
        }

        /** Identity table (useful for tests and as a starting point). */
        fun identity(size: Int = 17) = Lut3D(size, FloatArray(size * size * size * 3).also { d ->
            for (b in 0 until size) for (g in 0 until size) for (r in 0 until size) {
                val i = ((b * size + g) * size + r) * 3
                d[i] = r / (size - 1f); d[i + 1] = g / (size - 1f); d[i + 2] = b / (size - 1f)
            }
        })
    }
}

/** sRGB-ish gamma helper used by tests and the scanner. */
fun gamma(v: Float, g: Float) = v.coerceIn(0f, 1f).pow(g)
