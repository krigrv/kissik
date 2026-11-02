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
 * Hardware H.264 encoder. With [surfaceInput] the camera draws straight into the encoder (zero copy: the main
 * processor never touches a pixel, so 4K stays smooth and cool). Without it, frames are copied in with [encode],
 * a slower fallback. Each output is sent as one video packet:
 *
 *   'V' | flags (1 = keyframe) | uint64 LE presentation time in µs | H.264 Annex-B bytes
 *
 * Keyframes carry SPS/PPS in front, so the PC can start decoding at any keyframe.
 */
class H264Encoder(val width: Int, val height: Int, fps: Int, surfaceInput: Boolean = false,
                  private val onPacket: (ByteArray, Boolean) -> Unit) {
    private val codec = MediaCodec.createEncoderByType(MediaFormat.MIMETYPE_VIDEO_AVC)
    private var config: ByteArray? = null
    /** Where the camera draws, in zero-copy mode. */
    val surface: android.view.Surface?
    @Volatile private var running = true
    private var bitrate = bitrate(width, height, fps)
    val maxBitrate = bitrate

    init {
        val format = MediaFormat.createVideoFormat(MediaFormat.MIMETYPE_VIDEO_AVC, width, height).apply {
            setInteger(MediaFormat.KEY_COLOR_FORMAT,
                if (surfaceInput) MediaCodecInfo.CodecCapabilities.COLOR_FormatSurface else MediaCodecInfo.CodecCapabilities.COLOR_FormatYUV420Flexible)
            if (surfaceInput) setLong(MediaFormat.KEY_REPEAT_PREVIOUS_FRAME_AFTER, 100_000) // a still scene keeps sending frames
            setInteger(MediaFormat.KEY_BIT_RATE, bitrate)
            setInteger(MediaFormat.KEY_FRAME_RATE, fps)
            setInteger(MediaFormat.KEY_I_FRAME_INTERVAL, 2)
            setInteger(MediaFormat.KEY_PRIORITY, 0) // real-time
        }
        codec.configure(format, null, null, MediaCodec.CONFIGURE_FLAG_ENCODE)
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
                        p.put('V'.code.toByte()).put((if (isKey) 1 else 0).toByte()).putLong(info.presentationTimeUs).put(body)
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

    companion object {
        /** About 0.1 bits per pixel per frame: ~6 Mbps at 1080p30, ~25 Mbps at 4K30. Fine on a home network or USB. */
        fun bitrate(w: Int, h: Int, fps: Int) = (w.toLong() * h * fps / 10).coerceIn(1_000_000, 60_000_000).toInt()
    }
}
