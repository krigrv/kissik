package app.kissik.studio.ui

import androidx.compose.foundation.Canvas
import androidx.compose.runtime.Composable
import androidx.compose.ui.Modifier
import androidx.compose.ui.geometry.CornerRadius
import androidx.compose.ui.geometry.Offset
import androidx.compose.ui.geometry.Size
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.drawscope.DrawScope
import androidx.compose.ui.graphics.drawscope.Stroke
import androidx.compose.ui.graphics.drawscope.scale
import androidx.compose.ui.graphics.drawscope.translate
import androidx.compose.ui.semantics.contentDescription
import androidx.compose.ui.semantics.semantics
import androidx.compose.ui.text.TextMeasurer
import androidx.compose.ui.text.TextStyle
import androidx.compose.ui.text.drawText
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.rememberTextMeasurer
import androidx.compose.ui.text.style.TextAlign
import androidx.compose.ui.unit.Constraints
import app.kissik.studio.image.parseColor
import app.kissik.studio.scenes.SceneDocument
import app.kissik.studio.scenes.SceneLayer
import app.kissik.studio.scenes.Transition
import app.kissik.studio.scenes.transitionFrame

fun colorOf(text: String, fallback: Color = Color.Transparent): Color =
    if (text.isBlank()) fallback else Color(parseColor(text, 0))

/**
 * Draws a scene's overlay layers (text, shapes, and image placeholders) on top of the viewfinder, with the scene
 * transition between the previous and current scene. Layout is normalised, so it fits any preview size.
 */
@Composable
fun SceneOverlay(scene: SceneDocument?, previous: SceneDocument?, transition: Transition, progress: Float, modifier: Modifier = Modifier) {
    val measurer = rememberTextMeasurer()
    val desc = scene?.overlays?.filter { it.visible && it.kind == "text" }?.joinToString(", ") { it.text } ?: ""
    Canvas(modifier.semantics { if (desc.isNotEmpty()) contentDescription = "Scene overlay: $desc" }) {
        val tf = transitionFrame(transition, progress)
        if (previous != null && progress < 1f) drawScene(previous, measurer, tf.oldAlpha, tf.oldOffset, tf.oldScale)
        if (scene != null) drawScene(scene, measurer, if (previous != null || progress < 1f) tf.newAlpha else 1f,
            if (progress < 1f) tf.newOffset else 0f, if (progress < 1f) tf.newScale else 1f)
    }
}

private fun DrawScope.drawScene(scene: SceneDocument, measurer: TextMeasurer, alpha: Float, offset: Float, scale: Float) {
    if (alpha <= 0f) return
    translate(left = offset * size.width) {
        scale(scale) {
            scene.overlays.filter { it.visible }.forEach { drawLayer(it, measurer, alpha) }
        }
    }
}

fun DrawScope.drawLayer(l: SceneLayer, measurer: TextMeasurer, alpha: Float) {
    val w = size.width; val h = size.height
    val topLeft = Offset((l.x * w).toFloat(), (l.y * h).toFloat())
    val sz = Size((l.w * w).toFloat(), (l.h * h).toFloat())
    val a = (l.opacity.toFloat() * alpha).coerceIn(0f, 1f)
    val radius = CornerRadius((l.cornerRadius * h).toFloat())
    when (l.kind) {
        "shape", "image" -> {
            val fill = if (l.kind == "image") Color(0x33FFFFFF) else colorOf(l.color, Color.White)
            if (l.shape == "ellipse") drawOval(fill, topLeft, sz, alpha = a) else drawRoundRect(fill, topLeft, sz, radius, alpha = a)
            if (l.stroke.isNotBlank() && l.strokeWidth > 0) {
                val st = Stroke((l.strokeWidth * h).toFloat())
                if (l.shape == "ellipse") drawOval(colorOf(l.stroke), topLeft, sz, alpha = a, style = st)
                else drawRoundRect(colorOf(l.stroke), topLeft, sz, radius, alpha = a, style = st)
            }
        }
        "text" -> {
            if (l.fill.isNotBlank()) drawRoundRect(colorOf(l.fill), topLeft, sz, radius, alpha = a)
            val style = TextStyle(
                color = colorOf(l.color, Color.White),
                fontSize = with(this) { (l.fontSize * h).toFloat().toSp() },
                fontWeight = if (l.bold) FontWeight.Bold else FontWeight.Normal,
                textAlign = when (l.align) { "center" -> TextAlign.Center; "right" -> TextAlign.End; else -> TextAlign.Start },
            )
            val layout = measurer.measure(l.text, style, constraints = Constraints(maxWidth = sz.width.toInt().coerceAtLeast(1)))
            val dy = if (l.fill.isNotBlank()) ((sz.height - layout.size.height) / 2f).coerceAtLeast(0f) else 0f
            drawText(layout, topLeft = topLeft.copy(y = topLeft.y + dy), alpha = a)
        }
    }
}
