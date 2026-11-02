package app.kissik.camera

import android.media.MediaCodec
import android.media.MediaCodecInfo
import android.media.MediaFormat
import android.os.Bundle
import androidx.camera.core.ImageProxy
import java.nio.ByteBuffer
import java.nio.ByteOrder
import kotlin.concurrent.thread

/**
 * Hardware video encoder: H.264 ("h264"), H.265 ("hevc", about half the data for the same picture) or 10-bit HLG
 * H.265 ("hevc-hdr"). With [surfaceInput] the camera draws straight into the encoder (zero copy: the main
 * processor never touches a pixel, so 4K stays smooth and cool). Without it, frames are copied in with [encode],
 * a slower fallback. Each output is sent as one video packet:
 *
 *   'V' | flags (1 = keyframe) | uint64 LE presentation time in µs | Annex-B bytes
 *
 * Keyframes carry the parameter sets (SPS/PPS, plus VPS for H.265) in front, so the PC can start at any keyframe.
 */
class H264Encoder(val width: Int, val height: Int, fps: Int, surfaceInput: Boolean = false, val kind: String = "h264",
                  private val onPacket: (ByteArray, Boolean) -> Unit) {
    private val hevc = kind.startsWith("hevc")
    private val hdr = kind == "hevc-hdr"
    private val mime = if (hevc) MediaFormat.MIMETYPE_VIDEO_HEVC else MediaFormat.MIMETYPE_VIDEO_AVC
    private val codec = MediaCodec.createEncoderByType(mime)
    /** Whether this encoder supports gradual intra refresh. */
    val intraRefresh = runCatching {
        codec.codecInfo.getCapabilitiesForType(mime).isFeatureSupported(MediaCodecInfo.CodecCapabilities.FEATURE_IntraRefresh)
    }.getOrDefault(false)
    private var config: ByteArray? = null
    /**
     * Added to the encoder's timestamps to put them on SystemClock.elapsedRealtime: camera timestamps are either
     * already on that clock (REALTIME source) or on the monotonic clock (UNKNOWN source). Set by the service.
     */
    @Volatile var clockOffsetUs = 0L
    /** Capture → encoded delay of recent frames (µs), for the status. */
    val captureToEncodedUs = Window()
    /** Where the camera draws, in zero-copy mode. */
    val surface: android.view.Surface?
    @Volatile private var running = true
    private var bitrate = bitrate(width, height, fps) * (if (hevc) 6 else 10) / 10
    val maxBitrate = bitrate

    init {
        val format = MediaFormat.createVideoFormat(mime, width, height).apply {
            setInteger(MediaFormat.KEY_COLOR_FORMAT,
                if (surfaceInput) MediaCodecInfo.CodecCapabilities.COLOR_FormatSurface else MediaCodecInfo.CodecCapabilities.COLOR_FormatYUV420Flexible)
            if (surfaceInput) setLong(MediaFormat.KEY_REPEAT_PREVIOUS_FRAME_AFTER, 100_000) // a still scene keeps sending frames
            setInteger(MediaFormat.KEY_BIT_RATE, bitrate)
            setInteger(MediaFormat.KEY_FRAME_RATE, fps)
            // Smooth keyframes: where the encoder can, refresh the picture a slice at a time over one second
            // (intra refresh) instead of a big full keyframe every 2 s, which caused decode spikes on the PC.
            // Full keyframes still come on connect and on request; a safety one every 10 s.
            if (intraRefresh) {
                setInteger(MediaFormat.KEY_INTRA_REFRESH_PERIOD, fps)
                setInteger(MediaFormat.KEY_I_FRAME_INTERVAL, 10)
            } else setInteger(MediaFormat.KEY_I_FRAME_INTERVAL, 2)
            setInteger(MediaFormat.KEY_PRIORITY, 0) // real-time
            setInteger(MediaFormat.KEY_OPERATING_RATE, fps)
            // Low latency: no B-frames (they make the encoder hold frames back) and output each frame as it's ready.
            if (android.os.Build.VERSION.SDK_INT >= 29) setInteger(MediaFormat.KEY_MAX_B_FRAMES, 0)
            if (android.os.Build.VERSION.SDK_INT >= 30) setInteger(MediaFormat.KEY_LATENCY, 1)
            when {
                hdr -> {
                    setInteger(MediaFormat.KEY_PROFILE, MediaCodecInfo.CodecProfileLevel.HEVCProfileMain10)
                    setInteger(MediaFormat.KEY_COLOR_STANDARD, MediaFormat.COLOR_STANDARD_BT2020)
                    setInteger(MediaFormat.KEY_COLOR_TRANSFER, MediaFormat.COLOR_TRANSFER_HLG)
                    setInteger(MediaFormat.KEY_COLOR_RANGE, MediaFormat.COLOR_RANGE_LIMITED)
                }
                hevc -> setInteger(MediaFormat.KEY_PROFILE, MediaCodecInfo.CodecProfileLevel.HEVCProfileMain)
                else -> {
                    setInteger(MediaFormat.KEY_PROFILE, MediaCodecInfo.CodecProfileLevel.AVCProfileBaseline)
                    setInteger(MediaFormat.KEY_LEVEL, MediaCodecInfo.CodecProfileLevel.AVCLevel52)
                }
            }
        }
        runCatching { codec.configure(format, null, null, MediaCodec.CONFIGURE_FLAG_ENCODE) }.onFailure {
            // Some encoders refuse the low-latency profile settings: retry with the encoder's own defaults.
            // (Keep the 10-bit profile for HDR: without it the picture would be wrong, so let that fail instead.)
            listOfNotNull(MediaFormat.KEY_PROFILE.takeIf { !hdr }, MediaFormat.KEY_LEVEL, MediaFormat.KEY_LATENCY, MediaFormat.KEY_MAX_B_FRAMES,
                MediaFormat.KEY_INTRA_REFRESH_PERIOD)
                .forEach { format.removeKey(it) }
            codec.reset()
            codec.configure(format, null, null, MediaCodec.CONFIGURE_FLAG_ENCODE)
        }
        surface = if (surfaceInput) codec.createInputSurface() else null
        codec.start()
        thread(name = "kissik-h264-out", isDaemon = true) { drain() }
    }

    /** Copies one camera frame (YUV_420_888) into the encoder. Drops the frame if the encoder is busy. */
    fun encode(image: ImageProxy) {
        if (!running) return
        runCatching {
            val index = codec.dequeueInputBuffer(0)
            if (index < 0) return
            val dst = codec.getInputImage(index) ?: return codec.queueInputBuffer(index, 0, 0, 0, 0)
            val w = minOf(width, image.width); val h = minOf(height, image.height)
            for (p in 0..2) {
                val src = image.planes[p]; val out = dst.planes[p]
                val pw = if (p == 0) w else w / 2; val ph = if (p == 0) h else h / 2
                val sb = src.buffer; val db = out.buffer
                if (src.pixelStride == 1 && out.pixelStride == 1) {
                    // Packed rows: copy a whole row at a time.
                    val row = ByteArray(pw)
                    for (y in 0 until ph) {
                        sb.position(y * src.rowStride); sb.get(row, 0, pw)
                        db.position(y * out.rowStride); db.put(row, 0, pw)
                    }
                } else {
                    for (y in 0 until ph) {
                        val so = y * src.rowStride; val dOff = y * out.rowStride
                        for (x in 0 until pw) db.put(dOff + x * out.pixelStride, sb.get(so + x * src.pixelStride))
                    }
                }
            }
            codec.queueInputBuffer(index, 0, width * height * 3 / 2, image.imageInfo.timestamp / 1000, 0)
        }
    }

    private fun drain() {
        val info = MediaCodec.BufferInfo()
        while (running) {
            val index = runCatching { codec.dequeueOutputBuffer(info, 20_000) }.getOrElse { return }
            if (index < 0) continue
            runCatching {
                val buf = codec.getOutputBuffer(index)
                if (buf != null && info.size > 0) {
                    val data = ByteArray(info.size).also { buf.position(info.offset); buf.get(it) }
                    if (info.flags and MediaCodec.BUFFER_FLAG_CODEC_CONFIG != 0) config = data
                    else {
                        val isKey = info.flags and MediaCodec.BUFFER_FLAG_KEY_FRAME != 0
                        val body = if (isKey && config != null) config!! + data else data
                        val p = ByteBuffer.allocate(10 + body.size).order(ByteOrder.LITTLE_ENDIAN)
                        // Timestamp = when the sensor captured the frame, on the phone's elapsedRealtime clock (µs).
                        val ptsUs = info.presentationTimeUs + clockOffsetUs
                        val nowUs = android.os.SystemClock.elapsedRealtimeNanos() / 1000
                        if (surface != null) captureToEncodedUs.add(nowUs - ptsUs)
                        p.put('V'.code.toByte()).put((if (isKey) 1 else 0).toByte()).putLong(ptsUs).put(body)
                        onPacket(p.array(), isKey)
                    }
                }
                codec.releaseOutputBuffer(index, false)
            }
        }
    }

    /** Asks for a keyframe now (new connection, or after dropping frames on a slow link). */
    fun requestKeyframe() = runCatching { codec.setParameters(Bundle().apply { putInt(MediaCodec.PARAMETER_KEY_REQUEST_SYNC_FRAME, 0) }) }

    /** Smart bitrate: the sender lowers this when the link backs up and raises it again when it clears. */
    fun setBitrate(bps: Int) {
        val b = bps.coerceIn(500_000, maxBitrate)
        if (b == bitrate) return
        bitrate = b
        runCatching { codec.setParameters(Bundle().apply { putInt(MediaCodec.PARAMETER_KEY_VIDEO_BITRATE, b) }) }
    }
    val currentBitrate get() = bitrate

    fun release() {
        running = false
        runCatching { codec.stop() }; runCatching { codec.release() }; surface?.release()
    }

    /** A small rolling average. */
    class Window(private val size: Int = 30) {
        private val values = ArrayDeque<Long>()
        @Synchronized fun add(v: Long) { if (v in 0..5_000_000) { values.addLast(v); if (values.size > size) values.removeFirst() } }
        @Synchronized fun averageMs(): Double? = if (values.isEmpty()) null else values.average() / 1000.0
    }

    companion object {
        /** Which formats this phone's hardware can encode: always "h264"; "hevc" and "hevc-hdr" when available. */
        fun supported(): List<String> {
            val encoders = android.media.MediaCodecList(android.media.MediaCodecList.REGULAR_CODECS).codecInfos.filter { it.isEncoder }
            val hevc = encoders.filter { e -> e.supportedTypes.any { it.equals(MediaFormat.MIMETYPE_VIDEO_HEVC, true) } }
            val main10 = hevc.any { e ->
                runCatching { e.getCapabilitiesForType(MediaFormat.MIMETYPE_VIDEO_HEVC).profileLevels.any { it.profile == MediaCodecInfo.CodecProfileLevel.HEVCProfileMain10 } }.getOrDefault(false)
            }
            return listOfNotNull("h264", "hevc".takeIf { hevc.isNotEmpty() }, "hevc-hdr".takeIf { main10 })
        }

        /** About 0.1 bits per pixel per frame: ~6 Mbps at 1080p30, ~25 Mbps at 4K30. Fine on a home network or USB. */
        fun bitrate(w: Int, h: Int, fps: Int) = (w.toLong() * h * fps / 10).coerceIn(1_000_000, 60_000_000).toInt()
    }
}
