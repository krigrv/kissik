package app.kissik.studio.framing

import kotlin.math.max
import kotlin.math.min
import kotlin.math.sqrt

/** A rectangle in 0..1 picture coordinates. */
data class NRect(val x: Float, val y: Float, val w: Float, val h: Float) {
    val cx get() = x + w / 2
    val cy get() = y + h / 2
    companion object { val Full = NRect(0f, 0f, 1f, 1f) }
}

/** Critically damped-ish spring for one value: smooth, no overshoot at the default damping. */
class Spring(var value: Float, var stiffness: Float = 30f, var damping: Float = 1f) {
    var velocity = 0f
    fun step(target: Float, dt: Float): Float {
        val omega = sqrt(stiffness)
        val f = -2f * damping * omega * velocity - stiffness * (value - target)
        velocity += f * dt
        value += velocity * dt
        return value
    }
}

/**
 * Smart framing: keeps the people in the picture nicely framed and follows them with spring motion. Feed it the face
 * boxes found in each frame; it returns the crop to show. With nobody in view it eases back to the full picture.
 *
 * - Holds still while faces move inside a dead zone, so small head movements don't make the picture swim.
 * - Puts eyes about a third from the top (head room), and zooms so the group fills [fill] of the width.
 * - [reducedMotion] snaps instead of gliding.
 */
class AutoFramer(
    private val aspect: Float,         // output width / height, in picture units (e.g. 9/16 for vertical from 16:9)
    var maxZoom: Float = 2.5f,
    var fill: Float = 0.45f,
    var reducedMotion: Boolean = false,
) {
    private val sx = Spring(0.5f); private val sy = Spring(0.5f); private val sz = Spring(1f, stiffness = 18f)
    private var target = NRect.Full
    private var lostFor = 0f

    var crop: NRect = fullCrop(); private set

    private fun fullCrop(): NRect {
        // Largest rectangle of the wanted aspect inside the picture (aspect is relative to the source's own aspect).
        return if (aspect >= 1f) NRect(0f, (1 - 1 / aspect) / 2, 1f, 1 / aspect) else NRect((1 - aspect) / 2, 0f, aspect, 1f)
    }

    fun update(faces: List<NRect>, dt: Float): NRect {
        val base = fullCrop()
        if (faces.isEmpty()) {
            lostFor += dt
            if (lostFor > 1.5f) target = base // wait a moment before letting go (people look away)
        } else {
            lostFor = 0f
            val l = faces.minOf { it.x }; val r = faces.maxOf { it.x + it.w }
            val t = faces.minOf { it.y }; val b = faces.maxOf { it.y + it.h }
            val groupW = r - l
            val zoom = (fill * base.w / max(groupW, 0.05f)).coerceIn(1f, maxZoom)
            val w = base.w / zoom; val h = base.h / zoom
            val cx = (l + r) / 2
            val cy = t + (b - t) * 0.4f + h * 0.12f // eyes ~1/3 from the top
            val next = NRect(cx - w / 2, cy - h / 2, w, h)
            // Dead zone: only move when the group drifts out of the middle part of the current target.
            val moved = kotlin.math.abs(next.cx - target.cx) > target.w * 0.12f ||
                kotlin.math.abs(next.cy - target.cy) > target.h * 0.12f ||
                kotlin.math.abs(next.w - target.w) > target.w * 0.15f
            if (moved || target == base) target = next
        }
        val zoomTarget = base.w / target.w
        val z: Float; val cx: Float; val cy: Float
        if (reducedMotion) {
            z = zoomTarget; cx = target.cx; cy = target.cy
            sz.value = z; sx.value = cx; sy.value = cy
        } else {
            val step = min(dt, 0.05f)
            z = sz.step(zoomTarget, step); cx = sx.step(target.cx, step); cy = sy.step(target.cy, step)
        }
        val w = base.w / z; val h = base.h / z
        crop = NRect((cx - w / 2).coerceIn(0f, 1 - w), (cy - h / 2).coerceIn(0f, 1 - h), w, h)
        return crop
    }
}

/** Manual framing: zoom, pan (−1..1 of the free room), mirror, rotate. */
data class ManualFraming(
    val zoom: Float = 1f,
    val panX: Float = 0f,
    val panY: Float = 0f,
    val mirror: Boolean = false,
    val rotation: Int = 0,
) {
    fun crop(): NRect {
        val z = zoom.coerceIn(1f, 8f)
        val w = 1f / z; val h = 1f / z
        val x = (1 - w) / 2 * (1 + panX.coerceIn(-1f, 1f)); val y = (1 - h) / 2 * (1 + panY.coerceIn(-1f, 1f))
        return NRect(x, y, w, h)
    }
}
