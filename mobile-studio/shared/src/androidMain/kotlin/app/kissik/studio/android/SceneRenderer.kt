package app.kissik.studio.android

import android.graphics.Bitmap
import android.graphics.Canvas
import android.graphics.Paint
import android.graphics.RectF
import android.graphics.Typeface
import android.text.Layout
import android.text.StaticLayout
import android.text.TextPaint
import app.kissik.studio.image.parseColor
import app.kissik.studio.scenes.SceneDocument

/** Burns a scene's overlay layers into a frame for recordings, streams and the PC feed (same layout as the preview). */
object SceneRenderer {
    private val fill = Paint(Paint.ANTI_ALIAS_FLAG)
    private val stroke = Paint(Paint.ANTI_ALIAS_FLAG).apply { style = Paint.Style.STROKE }
    private val text = TextPaint(Paint.ANTI_ALIAS_FLAG)

    fun draw(bmp: Bitmap, scene: SceneDocument) {
        val c = Canvas(bmp)
        val w = bmp.width.toFloat(); val h = bmp.height.toFloat()
        for (l in scene.overlays) {
            if (!l.visible) continue
            val r = RectF((l.x * w).toFloat(), (l.y * h).toFloat(), ((l.x + l.w) * w).toFloat(), ((l.y + l.h) * h).toFloat())
            val alpha = (l.opacity * 255).toInt().coerceIn(0, 255)
            val radius = (l.cornerRadius * h).toFloat()
            when (l.kind) {
                "shape", "image" -> {
                    fill.color = if (l.kind == "image") 0x33FFFFFF else parseColor(l.color)
                    fill.alpha = (fill.alpha * alpha / 255)
                    if (l.shape == "ellipse") c.drawOval(r, fill) else c.drawRoundRect(r, radius, radius, fill)
                    if (l.stroke.isNotBlank() && l.strokeWidth > 0) {
                        stroke.color = parseColor(l.stroke); stroke.strokeWidth = (l.strokeWidth * h).toFloat()
                        if (l.shape == "ellipse") c.drawOval(r, stroke) else c.drawRoundRect(r, radius, radius, stroke)
                    }
                }
                "text" -> {
                    if (l.fill.isNotBlank()) { fill.color = parseColor(l.fill); c.drawRoundRect(r, radius, radius, fill) }
                    text.color = parseColor(l.color); text.alpha = alpha
                    text.textSize = (l.fontSize * h).toFloat()
                    text.typeface = if (l.bold) Typeface.DEFAULT_BOLD else Typeface.DEFAULT
                    val align = when (l.align) { "center" -> Layout.Alignment.ALIGN_CENTER; "right" -> Layout.Alignment.ALIGN_OPPOSITE; else -> Layout.Alignment.ALIGN_NORMAL }
                    val layout = StaticLayout.Builder.obtain(l.text, 0, l.text.length, text, r.width().toInt().coerceAtLeast(1)).setAlignment(align).build()
                    val dy = if (l.fill.isNotBlank()) ((r.height() - layout.height) / 2f).coerceAtLeast(0f) else 0f
                    c.save(); c.translate(r.left, r.top + dy); layout.draw(c); c.restore()
                }
            }
        }
    }
}
