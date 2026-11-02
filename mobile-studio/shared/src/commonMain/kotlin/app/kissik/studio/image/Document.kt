package app.kissik.studio.image

import kotlin.math.hypot
import kotlin.math.max
import kotlin.math.roundToInt

data class Pt(val x: Float, val y: Float)

/** A page outline in picture pixels: top-left, top-right, bottom-right, bottom-left. */
data class Quad(val tl: Pt, val tr: Pt, val br: Pt, val bl: Pt) {
    val points get() = listOf(tl, tr, br, bl)

    /** Output size that keeps the page's real proportions. */
    fun outputSize(): Pair<Int, Int> {
        val w = max(hypot(tr.x - tl.x, tr.y - tl.y), hypot(br.x - bl.x, br.y - bl.y))
        val h = max(hypot(bl.x - tl.x, bl.y - tl.y), hypot(br.x - tr.x, br.y - tr.y))
        return w.roundToInt().coerceAtLeast(1) to h.roundToInt().coerceAtLeast(1)
    }

    companion object {
        /** Orders four loose corner points into tl, tr, br, bl (by x+y and y−x). */
        fun fromPoints(p: List<Pt>): Quad {
            require(p.size == 4)
            val tl = p.minBy { it.x + it.y }; val br = p.maxBy { it.x + it.y }
            val tr = p.minBy { it.y - it.x }; val bl = p.maxBy { it.y - it.x }
            return Quad(tl, tr, br, bl)
        }
    }
}

/** 3×3 projective transform, row-major. */
class Homography(val m: DoubleArray) {
    fun map(x: Double, y: Double): Pair<Double, Double> {
        val w = m[6] * x + m[7] * y + m[8]
        return (m[0] * x + m[1] * y + m[2]) / w to (m[3] * x + m[4] * y + m[5]) / w
    }

    companion object {
        /** The transform taking each src[i] to dst[i] (4 point pairs), solved with Gaussian elimination. */
        fun fromPoints(src: List<Pt>, dst: List<Pt>): Homography {
            val a = Array(8) { DoubleArray(9) }
            for (i in 0 until 4) {
                val (x, y) = src[i].x.toDouble() to src[i].y.toDouble()
                val (u, v) = dst[i].x.toDouble() to dst[i].y.toDouble()
                a[i * 2] = doubleArrayOf(x, y, 1.0, 0.0, 0.0, 0.0, -u * x, -u * y, u)
                a[i * 2 + 1] = doubleArrayOf(0.0, 0.0, 0.0, x, y, 1.0, -v * x, -v * y, v)
            }
            for (c in 0 until 8) {
                val p = (c until 8).maxBy { kotlin.math.abs(a[it][c]) }
                val t = a[c]; a[c] = a[p]; a[p] = t
                require(kotlin.math.abs(a[c][c]) > 1e-12) { "Degenerate quad" }
                for (r in 0 until 8) if (r != c) {
                    val f = a[r][c] / a[c][c]
                    for (k in c..8) a[r][k] -= f * a[c][k]
                }
            }
            val h = DoubleArray(9) { if (it == 8) 1.0 else a[it][8] / a[it][it] }
            return Homography(h)
        }
    }
}

/** Perspective correction: flattens the page inside [quad] into an upright rectangle. */
fun Frame.warpedPage(quad: Quad, maxSide: Int = 2000): Frame {
    var (w, h) = quad.outputSize()
    val s = maxSide.toFloat() / max(w, h)
    if (s < 1f) { w = (w * s).roundToInt(); h = (h * s).roundToInt() }
    val dst = listOf(Pt(0f, 0f), Pt(w - 1f, 0f), Pt(w - 1f, h - 1f), Pt(0f, h - 1f))
    val inv = Homography.fromPoints(dst, quad.points) // output pixel → source pixel
    val out = IntArray(w * h)
    for (y in 0 until h) for (x in 0 until w) {
        val (sx, sy) = inv.map(x.toDouble(), y.toDouble())
        val x0 = sx.toInt().coerceIn(0, width - 1); val y0 = sy.toInt().coerceIn(0, height - 1)
        val x1 = (x0 + 1).coerceAtMost(width - 1); val y1 = (y0 + 1).coerceAtMost(height - 1)
        val tx = (sx - x0).toFloat().coerceIn(0f, 1f); val ty = (sy - y0).toFloat().coerceIn(0f, 1f)
        out[y * w + x] = lerpColor(lerpColor(this[x0, y0], this[x1, y0], tx), lerpColor(this[x0, y1], this[x1, y1], tx), ty)
    }
    return Frame(w, h, out)
}

/**
 * Clean black-and-white: adaptive threshold against the local average (an integral image), so shadows and uneven
 * light don't turn into grey blotches. Ink darker than its surroundings by [offset] becomes black.
 */
fun Frame.cleanBlackAndWhite(window: Int = max(15, width / 40), offset: Int = 12): Frame {
    val w = width; val h = height
    val integral = LongArray((w + 1) * (h + 1))
    for (y in 0 until h) {
        var row = 0L
        for (x in 0 until w) {
            row += luma(this[x, y])
            integral[(y + 1) * (w + 1) + x + 1] = integral[y * (w + 1) + x + 1] + row
        }
    }
    val r = window / 2
    val out = IntArray(w * h)
    for (y in 0 until h) for (x in 0 until w) {
        val x0 = max(0, x - r); val y0 = max(0, y - r); val x1 = minOf(w, x + r + 1); val y1 = minOf(h, y + r + 1)
        val sum = integral[y1 * (w + 1) + x1] - integral[y0 * (w + 1) + x1] - integral[y1 * (w + 1) + x0] + integral[y0 * (w + 1) + x0]
        val mean = sum / ((x1 - x0) * (y1 - y0))
        out[y * w + x] = if (luma(this[x, y]) < mean - offset) 0xFF000000.toInt() else 0xFFFFFFFF.toInt()
    }
    return Frame(w, h, out)
}

/** Grey-scale with gentle contrast: the "Grey" scan look. */
fun Frame.cleanGrey(): Frame = adjusted(Adjustments(contrast = 0.35f, brightness = 0.08f), mono = true)

/**
 * Minimal multi-page PDF writer: one JPEG per page, each page sized to the picture at [dpi]. Pure Kotlin so Android
 * and iOS make identical files. JPEG encoding itself is done by the platform.
 */
object PdfWriter {
    class Page(val jpeg: ByteArray, val width: Int, val height: Int)

    fun write(pages: List<Page>, dpi: Int = 150, title: String = "KISSIK scan"): ByteArray {
        require(pages.isNotEmpty())
        val out = ByteSink()
        val offsets = mutableListOf<Int>()
        fun obj(body: () -> Unit) { offsets += out.size; out.text("${offsets.size} 0 obj\n"); body(); out.text("\nendobj\n") }
        out.text("%PDF-1.4\n%âãÏÓ\n")
        val n = pages.size
        // 1 catalog, 2 pages, 3 info, then per page: page, content, image.
        val pageIds = (0 until n).map { 4 + it * 3 }
        obj { out.text("<< /Type /Catalog /Pages 2 0 R >>") }
        obj { out.text("<< /Type /Pages /Count $n /Kids [${pageIds.joinToString(" ") { "$it 0 R" }}] >>") }
        obj { out.text("<< /Title (${title.replace("(", "").replace(")", "")}) /Producer (KISSIK) >>") }
        pages.forEachIndexed { i, p ->
            val pw = p.width * 72f / dpi; val ph = p.height * 72f / dpi
            val pid = pageIds[i]
            obj {
                out.text("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 ${fmt(pw)} ${fmt(ph)}] " +
                    "/Resources << /XObject << /Im$i ${pid + 2} 0 R >> >> /Contents ${pid + 1} 0 R >>")
            }
            val content = "q ${fmt(pw)} 0 0 ${fmt(ph)} 0 0 cm /Im$i Do Q"
            obj { out.text("<< /Length ${content.length} >>\nstream\n$content\nendstream") }
            obj {
                out.text("<< /Type /XObject /Subtype /Image /Width ${p.width} /Height ${p.height} /ColorSpace /DeviceRGB " +
                    "/BitsPerComponent 8 /Filter /DCTDecode /Length ${p.jpeg.size} >>\nstream\n")
                out.bytes(p.jpeg)
                out.text("\nendstream")
            }
        }
        val xref = out.size
        out.text("xref\n0 ${offsets.size + 1}\n0000000000 65535 f \n")
        offsets.forEach { out.text(it.toString().padStart(10, '0') + " 00000 n \n") }
        out.text("trailer\n<< /Size ${offsets.size + 1} /Root 1 0 R /Info 3 0 R >>\nstartxref\n$xref\n%%EOF\n")
        return out.toByteArray()
    }

    private fun fmt(v: Float) = ((v * 100).roundToInt() / 100f).toString()

    private class ByteSink {
        private var buf = ByteArray(64 * 1024); var size = 0; private set
        fun bytes(b: ByteArray) { ensure(b.size); b.copyInto(buf, size); size += b.size }
        fun text(s: String) = bytes(ByteArray(s.length) { s[it].code.toByte() }) // Latin-1, as PDF syntax is
        private fun ensure(n: Int) { if (size + n > buf.size) buf = buf.copyOf(max(buf.size * 2, size + n)) }
        fun toByteArray() = buf.copyOf(size)
    }
}
