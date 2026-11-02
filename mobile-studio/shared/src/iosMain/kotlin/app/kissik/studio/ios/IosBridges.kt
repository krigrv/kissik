@file:OptIn(kotlinx.cinterop.ExperimentalForeignApi::class, kotlinx.cinterop.BetaInteropApi::class)

package app.kissik.studio.ios

import app.kissik.studio.image.Frame
import app.kissik.studio.image.parseColor
import app.kissik.studio.scenes.SceneDocument
import app.kissik.studio.stream.LinkState
import app.kissik.studio.stream.StreamSettings
import kotlinx.cinterop.*
import platform.CoreCrypto.CC_SHA256
import platform.CoreCrypto.CC_SHA256_DIGEST_LENGTH
import platform.CoreFoundation.CFDataGetBytePtr
import platform.CoreFoundation.CFDataGetLength
import platform.CoreFoundation.CFRelease
import platform.CoreGraphics.*
import platform.Foundation.*
import platform.Security.*
import platform.UIKit.*
import platform.darwin.NSObject

/**
 * Hooks the Swift side implements (see iosApp/iosApp/Bridges.swift). Kotlin hands over finished BGRA frames; Swift
 * does the Apple-framework work that is simpler there (VideoToolbox, HaishinKit).
 */
object Bridges {
    /** RTMP publishing, e.g. HaishinKit (BSD-3-Clause). */
    var rtmp: RtmpBridge? = null
    /** H.264 encoder (VideoToolbox) for the PC webcam link. */
    var h264: H264Bridge? = null
}

interface RtmpBridge {
    /** Connect each (id, publish URL); report states through [state]. */
    fun start(destinations: List<List<String>>, width: Int, height: Int, fps: Int, bitrateKbps: Int, hevc: Boolean, state: (String, String) -> Unit)
    /** One finished frame: BGRA bytes, row stride = width × 4. */
    fun video(bgra: NSData, width: Int, height: Int, timeUs: Long)
    /** Mono float samples. */
    fun audio(samples: NSData, sampleRate: Int, timeUs: Long)
    fun stop()
}

interface H264Bridge {
    /** Starts an encoder; [output] gets Annex-B access units (keyframes with SPS/PPS in front). */
    fun start(width: Int, height: Int, fps: Int, bitrateKbps: Int, output: (NSData, Boolean, Long) -> Unit)
    fun encode(bgra: NSData, width: Int, height: Int, timeUs: Long)
    fun requestKeyFrame()
    fun stop()
}

fun Frame.toBgraData(): NSData = pixels.usePinned { NSData.dataWithBytes(it.addressOf(0), (pixels.size * 4).toULong()) }
fun FloatArray.toNSData(): NSData = if (isEmpty()) NSData() else usePinned { NSData.dataWithBytes(it.addressOf(0), (size * 4).toULong()) }

/** Live streaming through the Swift RTMP bridge, fed from the camera's finished frames and the mic. */
class IosLiveStream(private val camera: IosCamera, private val audio: IosAudio) : app.kissik.studio.platform.LiveService {
    override val states = kotlinx.coroutines.flow.MutableStateFlow<Map<String, LinkState>>(emptyMap())
    override val supportsHevc = true
    private var sink: IosFrameSink? = null
    private var pcm: IosAudio.PcmListener? = null

    override fun start(destinations: List<app.kissik.studio.stream.Destination>, settings: StreamSettings) {
        val b = Bridges.rtmp ?: run { states.value = destinations.associate { it.id to LinkState.Failed }; return }
        val f = camera.lastFrame
        val portrait = f != null && f.height > f.width
        val short = settings.shortSide; val long = short * 16 / 9
        val (w, h) = if (portrait) short to long else long to short
        b.start(destinations.map { listOf(it.id, it.publishUrl) }, w, h, settings.fps, settings.bitrateKbps,
            settings.codec == app.kissik.studio.stream.VideoCodec.HEVC) { id, s ->
            onMain { states.value = states.value + (id to (LinkState.entries.firstOrNull { it.name == s } ?: LinkState.Failed)) }
        }
        sink = IosFrameSink { fr, t -> b.video(fr.toBgraData(), fr.width, fr.height, t) }.also { camera.sinks += it }
        pcm = IosAudio.PcmListener { s, rate, t -> b.audio(s.toNSData(), rate, t) }.also { audio.addListener(it) }
    }

    override fun stop() {
        sink?.let { camera.sinks -= it }; sink = null
        pcm?.let { audio.removeListener(it) }; pcm = null
        Bridges.rtmp?.stop()
        states.value = states.value.mapValues { LinkState.Idle }
    }
}

// ── Scene burn-in ────────────────────────────────────────────────────────────

/** Draws a scene's overlay layers into a frame for recordings, streams and the PC feed (same layout as the preview). */
object IosSceneRenderer {
    private fun color(text: String, fallback: Int = 0xFFFFFFFF.toInt()): UIColor {
        val c = parseColor(text, fallback)
        return UIColor.colorWithRed(((c shr 16) and 0xFF) / 255.0, ((c shr 8) and 0xFF) / 255.0, (c and 0xFF) / 255.0, ((c ushr 24) and 0xFF) / 255.0)
    }

    fun draw(frame: Frame, scene: SceneDocument): Frame {
        val base = frame.toUIImage() ?: return frame
        val w = frame.width.toDouble(); val h = frame.height.toDouble()
        val fmt = UIGraphicsImageRendererFormat.defaultFormat().apply { scale = 1.0 }
        val img = UIGraphicsImageRenderer(CGSizeMake(w, h), fmt).imageWithActions { _ ->
            base.drawInRect(CGRectMake(0.0, 0.0, w, h))
            for (l in scene.overlays) {
                if (!l.visible) continue
                val r = CGRectMake(l.x * w, l.y * h, l.w * w, l.h * h)
                val radius = l.cornerRadius * h
                val alpha = l.opacity
                when (l.kind) {
                    "shape", "image" -> {
                        val path = if (l.shape == "ellipse") UIBezierPath.bezierPathWithOvalInRect(r) else UIBezierPath.bezierPathWithRoundedRect(r, radius)
                        (if (l.kind == "image") UIColor.whiteColor.colorWithAlphaComponent(0.2) else color(l.color)).colorWithAlphaComponent(alpha * color(l.color).alpha()).setFill()
                        path.fill()
                        if (l.stroke.isNotBlank() && l.strokeWidth > 0) { color(l.stroke).setStroke(); path.lineWidth = l.strokeWidth * h; path.stroke() }
                    }
                    "text" -> {
                        if (l.fill.isNotBlank()) { color(l.fill).setFill(); UIBezierPath.bezierPathWithRoundedRect(r, radius).fill() }
                        val para = NSMutableParagraphStyle().apply {
                            setAlignment(when (l.align) { "center" -> NSTextAlignmentCenter; "right" -> NSTextAlignmentRight; else -> NSTextAlignmentLeft })
                        }
                        val font = if (l.bold) UIFont.boldSystemFontOfSize(l.fontSize * h) else UIFont.systemFontOfSize(l.fontSize * h)
                        val attrs = mapOf<Any?, Any?>(NSFontAttributeName to font, NSForegroundColorAttributeName to color(l.color).colorWithAlphaComponent(alpha),
                            NSParagraphStyleAttributeName to para)
                        (l.text as NSString).drawInRect(r, withAttributes = attrs)
                    }
                }
            }
        }
        return img.toFrame(maxOf(frame.width, frame.height)) ?: frame
    }

    private fun UIColor.alpha(): Double = memScoped { val a = alloc<DoubleVar>(); getRed(null, null, null, a.ptr); a.value }
}

// ── Certificate pinning for the PC link ──────────────────────────────────────

/**
 * Trust-on-first-use for the PC's self-made certificate, like Android: the first connect records its SHA-256 and
 * every later connect must present that exact certificate.
 */
class PinningDelegate(private val store: app.kissik.studio.platform.KeyValueStore, private val host: String) : NSObject(), NSURLSessionDelegateProtocol {
    override fun URLSession(session: NSURLSession, didReceiveChallenge: NSURLAuthenticationChallenge,
                            completionHandler: (NSURLSessionAuthChallengeDisposition, NSURLCredential?) -> Unit) {
        val trust = didReceiveChallenge.protectionSpace.serverTrust
        if (didReceiveChallenge.protectionSpace.authenticationMethod != NSURLAuthenticationMethodServerTrust || trust == null) {
            completionHandler(NSURLSessionAuthChallengePerformDefaultHandling, null); return
        }
        val cert = SecTrustGetCertificateAtIndex(trust, 0) ?: run { completionHandler(NSURLSessionAuthChallengeCancelAuthenticationChallenge, null); return }
        val der = SecCertificateCopyData(cert) ?: run { completionHandler(NSURLSessionAuthChallengeCancelAuthenticationChallenge, null); return }
        val fp = memScoped {
            val digest = allocArray<UByteVar>(CC_SHA256_DIGEST_LENGTH)
            CC_SHA256(CFDataGetBytePtr(der), CFDataGetLength(der).toUInt(), digest)
            (0 until CC_SHA256_DIGEST_LENGTH).joinToString("") { digest[it].toString(16).padStart(2, '0') }
        }
        CFRelease(der)
        val key = "pin:$host"
        val pinned = store.get(key)
        if (pinned == null || pinned == fp) {
            if (pinned == null) store.put(key, fp)
            completionHandler(NSURLSessionAuthChallengeUseCredential, NSURLCredential.credentialForTrust(trust))
        } else completionHandler(NSURLSessionAuthChallengeCancelAuthenticationChallenge, null)
    }
}
