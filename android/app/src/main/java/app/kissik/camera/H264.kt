package app.kissik.camera

import android.media.MediaCodec
import android.media.MediaCodecInfo
import android.media.MediaFormat
import android.os.Bundle
import android.os.Handler
import android.os.HandlerThread
import android.view.Surface
import java.nio.ByteBuffer
import java.nio.ByteOrder

/**
 * Hardware H.264 encoder fed straight from the camera through a Surface: no per-frame work on the CPU, so 4K stays
 * cool and smooth. Each output is sent as one video packet:
 *
 *   'V' | flags (1 = keyframe, 2 = codec config) | uint64 LE presentation time in µs | H.264 Annex-B bytes
 *
 * Keyframes carry SPS/PPS in front, so the PC can start decoding at any keyframe.
 */
class H264Encoder(val width: Int, val height: Int, fps: Int, private val onPacket: (ByteArray, Boolean) -> Unit) {
    private val thread = HandlerThread("kissik-h264").apply { start() }
    private val codec = MediaCodec.createEncoderByType(MediaFormat.MIMETYPE_VIDEO_AVC)
    val surface: Surface
    private var config: ByteArray? = null

    init {
        val format = MediaFormat.createVideoFormat(MediaFormat.MIMETYPE_VIDEO_AVC, width, height).apply {
            setInteger(MediaFormat.KEY_COLOR_FORMAT, MediaCodecInfo.CodecCapabilities.COLOR_FormatSurface)
            setInteger(MediaFormat.KEY_BIT_RATE, bitrate(width, height, fps))
            setInteger(MediaFormat.KEY_FRAME_RATE, fps)
            setInteger(MediaFormat.KEY_I_FRAME_INTERVAL, 2)
            setInteger(MediaFormat.KEY_PRIORITY, 0) // real-time
            setLong(MediaFormat.KEY_REPEAT_PREVIOUS_FRAME_AFTER, 100_000) // still scenes keep producing frames
        }
        codec.setCallback(object : MediaCodec.Callback() {
            override fun onInputBufferAvailable(c: MediaCodec, index: Int) {}
            override fun onOutputFormatChanged(c: MediaCodec, format: MediaFormat) {}
            override fun onError(c: MediaCodec, e: MediaCodec.CodecException) {}
            override fun onOutputBufferAvailable(c: MediaCodec, index: Int, info: MediaCodec.BufferInfo) {
                runCatching {
                    val buf = c.getOutputBuffer(index) ?: return@runCatching
                    val data = ByteArray(info.size).also { buf.position(info.offset); buf.get(it) }
                    val isConfig = info.flags and MediaCodec.BUFFER_FLAG_CODEC_CONFIG != 0
                    val isKey = info.flags and MediaCodec.BUFFER_FLAG_KEY_FRAME != 0
                    if (isConfig) { config = data; return@runCatching }
                    val body = if (isKey && config != null) config!! + data else data
                    val p = ByteBuffer.allocate(10 + body.size).order(ByteOrder.LITTLE_ENDIAN)
                    p.put('V'.code.toByte()).put((if (isKey) 1 else 0).toByte()).putLong(info.presentationTimeUs).put(body)
                    onPacket(p.array(), isKey)
                }
                runCatching { c.releaseOutputBuffer(index, false) }
            }
        }, Handler(thread.looper))
        codec.configure(format, null, null, MediaCodec.CONFIGURE_FLAG_ENCODE)
        surface = codec.createInputSurface()
        codec.start()
    }

    /** Asks for a keyframe now (new connection, or after dropping frames on a slow link). */
    fun requestKeyframe() = runCatching { codec.setParameters(Bundle().apply { putInt(MediaCodec.PARAMETER_KEY_REQUEST_SYNC_FRAME, 0) }) }

    fun release() {
        runCatching { codec.stop() }; runCatching { codec.release() }; surface.release(); thread.quitSafely()
    }

    companion object {
        /** About 0.1 bits per pixel per frame: ~6 Mbps at 1080p30, ~25 Mbps at 4K30. Fine on a home network or USB. */
        fun bitrate(w: Int, h: Int, fps: Int) = (w.toLong() * h * fps / 10).coerceIn(1_000_000, 60_000_000).toInt()
    }
}
