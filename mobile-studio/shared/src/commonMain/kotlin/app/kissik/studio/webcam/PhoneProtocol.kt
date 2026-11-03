package app.kissik.studio.webcam

import app.kissik.studio.camera.CameraState
import kotlinx.serialization.json.*

/**
 * The KISSIK phone protocol (as served by the desktop's PhoneServer and used by the phone-webcam app): the phone
 * connects over a WebSocket and sends JPEG or H.264 'V' frames, 'A' audio and a status JSON; the PC sends text
 * commands back.
 */
data class Pairing(val host: String, val port: Int, val token: String) {
    val wifiUrl get() = "wss://$host:$port/ws?t=$token"
    val usbUrl get() = "ws://localhost:${port + 1}/ws?t=$token"

    companion object {
        /** Accepts the PC's QR link `https://<pc-ip>:47820/?t=<token>` (32 hex digits; older builds used 16). */
        fun parse(link: String): Pairing? {
            val s = link.trim()
            if (!s.startsWith("https://")) return null
            val rest = s.removePrefix("https://")
            val authority = rest.substringBefore('/').substringBefore('?')
            val query = rest.substringAfter('?', "")
            val token = query.split('&').firstOrNull { it.startsWith("t=") }?.removePrefix("t=") ?: return null
            if (!Regex("[0-9a-fA-F]{16}([0-9a-fA-F]{16})?").matches(token)) return null
            val host: String; val port: Int
            if (authority.startsWith("[")) { // IPv6
                host = authority.substringAfter('[').substringBefore(']')
                port = authority.substringAfter("]:", "").toIntOrNull() ?: 47820
            } else {
                host = authority.substringBefore(':')
                port = authority.substringAfter(':', "").toIntOrNull() ?: 47820
            }
            if (host.isEmpty()) return null
            return Pairing(host, port, token.lowercase())
        }
    }
}

/** A command from the PC. Unknown text becomes [Unknown] so newer PCs never break older phones. */
sealed interface PcCommand {
    data class Facing(val front: Boolean) : PcCommand
    data class Lens(val id: String) : PcCommand
    data class Quality(val shortSide: Int) : PcCommand
    data class Torch(val on: Boolean) : PcCommand
    data class Zoom(val factor: Float) : PcCommand
    data class Ev(val index: Int) : PcCommand
    data class Iso(val value: Int?) : PcCommand
    data class Shutter(val ns: Long?) : PcCommand
    data object ExposureAuto : PcCommand
    data class Focus(val value: Float?) : PcCommand
    data class FocusTap(val x: Float, val y: Float) : PcCommand
    data class WhiteBalance(val mode: String) : PcCommand
    data class Fps(val fps: Int) : PcCommand // 0 = auto
    data class Stabilization(val on: Boolean) : PcCommand
    data class Hdr(val on: Boolean) : PcCommand
    data class Codec(val name: String) : PcCommand
    data object KeyFrame : PcCommand
    data class Effect(val name: String) : PcCommand
    data class Mic(val on: Boolean) : PcCommand
    data class MicDevice(val id: String?) : PcCommand
    data class MicMode(val mode: String) : PcCommand
    data class NoiseSuppression(val on: Boolean) : PcCommand
    data class Unknown(val text: String) : PcCommand

    companion object {
        fun parse(text: String): PcCommand {
            val t = text.trim()
            val key = t.substringBefore(':'); val v = t.substringAfter(':', "")
            fun onOff() = v == "on"
            return when (key) {
                "front" -> Facing(true)
                "back" -> Facing(false)
                "lens" -> Lens(v)
                "q" -> v.toIntOrNull()?.let { Quality(it) }
                "torch" -> Torch(onOff())
                "zoom" -> v.toFloatOrNull()?.let { Zoom(it) }
                "ev" -> v.toIntOrNull()?.let { Ev(it) }
                "iso" -> if (v == "auto") Iso(null) else v.toIntOrNull()?.let { Iso(it) }
                "shutter" -> if (v == "auto") Shutter(null) else v.toLongOrNull()?.let { Shutter(it) }
                "exposure" -> if (v == "auto") ExposureAuto else null
                "focus" -> when {
                    v == "auto" -> Focus(null)
                    v.startsWith("tap:") -> v.removePrefix("tap:").split(',').let { p ->
                        val x = p.getOrNull(0)?.toFloatOrNull(); val y = p.getOrNull(1)?.toFloatOrNull()
                        if (x != null && y != null) FocusTap(x.coerceIn(0f, 1f), y.coerceIn(0f, 1f)) else null
                    }
                    else -> v.toFloatOrNull()?.let { Focus(it.coerceIn(0f, 1f)) }
                }
                "wb" -> WhiteBalance(v)
                "fps" -> if (v == "auto") Fps(0) else v.toIntOrNull()?.let { Fps(it) }
                "stab" -> Stabilization(onOff())
                "hdr" -> Hdr(onOff())
                "codec" -> if (v == "jpeg" || v == "h264") Codec(v) else null
                "key" -> KeyFrame
                "effect" -> Effect(v)
                "mic" -> Mic(onOff())
                "micdev" -> MicDevice(if (v == "auto") null else v)
                "micmode" -> MicMode(v)
                "ns" -> NoiseSuppression(onOff())
                else -> null
            } ?: Unknown(t)
        }
    }
}

/** Extra status the camera state doesn't carry. */
data class LinkStatus(
    val device: String,
    val battery: Float,
    val charging: Boolean,
    val codec: String = "jpeg",
    val rot: Int = 0,
    val mic: Boolean = true,
    val mics: List<Pair<String, String>> = emptyList(),
    val micDev: String? = null,
    val micMode: String = "standard",
    val ns: Boolean? = null,
)

object PhoneProtocol {
    /** Builds the status JSON the PC expects. A control the lens lacks is null, so the PC hides it. */
    fun status(cam: CameraState, link: LinkStatus): String = buildJsonObject {
        put("device", link.device); put("battery", link.battery); put("charging", link.charging)
        put("facing", if (cam.front) "front" else "back")
        put("w", cam.width); put("h", cam.height); put("rot", link.rot)
        put("maxW", cam.maxWidth); put("maxH", cam.maxHeight); put("maxFps", cam.maxFps)
        put("torch", if (cam.hasTorch) JsonPrimitive(cam.torch) else JsonNull)
        put("zoom", cam.zoom); put("zoomMin", cam.zoomMin); put("zoomMax", cam.zoomMax)
        putJsonArray("lenses") { cam.lenses.forEach { l -> addJsonObject { put("id", l.id); put("facing", if (l.front) "front" else "back"); put("label", l.label) } } }
        put("lens", cam.lensId?.let { JsonPrimitive(it) } ?: JsonNull)
        put("ev", cam.ev); put("evMin", cam.evMin); put("evMax", cam.evMax); put("evStep", cam.evStep)
        put("manual", cam.manual)
        if (cam.manual) { put("isoMin", cam.isoMin); put("isoMax", cam.isoMax); put("shutterMin", cam.shutterMin); put("shutterMax", cam.shutterMax) }
        put("iso", cam.iso?.let { JsonPrimitive(it) } ?: JsonNull)
        put("shutter", cam.shutter?.let { JsonPrimitive(it) } ?: JsonNull)
        put("focusManual", cam.focusManual)
        put("focus", cam.focus?.let { JsonPrimitive(it) } ?: JsonNull)
        putJsonArray("wbModes") { cam.wbModes.forEach { add(it) } }; put("wb", cam.wb)
        putJsonArray("fpsList") { cam.fpsList.forEach { add(it) } }; put("fps", cam.fps)
        put("stab", cam.stab?.let { JsonPrimitive(it) } ?: JsonNull)
        put("hdr", cam.hdr?.let { JsonPrimitive(it) } ?: JsonNull)
        putJsonArray("codecs") { add("jpeg"); add("h264") }; put("codec", link.codec)
        putJsonArray("effects") {}; put("effect", "none")
        put("mic", link.mic)
        putJsonArray("mics") { link.mics.forEach { (id, label) -> addJsonObject { put("id", id); put("label", label) } } }
        put("micDev", link.micDev?.let { JsonPrimitive(it) } ?: JsonNull)
        putJsonArray("micModes") { listOf("standard", "voice", "camera", "raw").forEach { add(it) } }
        put("micMode", link.micMode)
        put("ns", link.ns?.let { JsonPrimitive(it) } ?: JsonNull)
    }.toString()

    /** 'V' · flags (bit 0 keyframe) · uint64 LE µs timestamp · H.264 Annex-B. */
    fun videoPacket(annexB: ByteArray, keyFrame: Boolean, timestampUs: Long): ByteArray {
        val out = ByteArray(10 + annexB.size)
        out[0] = 'V'.code.toByte(); out[1] = if (keyFrame) 1 else 0
        for (i in 0 until 8) out[2 + i] = (timestampUs ushr (8 * i)).toByte()
        annexB.copyInto(out, 10)
        return out
    }

    /** 'A' · uint32 LE sample rate · mono float32 LE samples. */
    fun audioPacket(samples: FloatArray, sampleRate: Int): ByteArray {
        val out = ByteArray(5 + samples.size * 4)
        out[0] = 'A'.code.toByte()
        for (i in 0 until 4) out[1 + i] = (sampleRate ushr (8 * i)).toByte()
        samples.forEachIndexed { k, f ->
            val bits = f.toRawBits()
            for (i in 0 until 4) out[5 + k * 4 + i] = (bits ushr (8 * i)).toByte()
        }
        return out
    }
}
