package app.kissik.studio.android

import android.graphics.Bitmap
import android.graphics.Rect
import android.media.MediaCodec
import android.media.MediaCodecInfo
import android.media.MediaCodecList
import android.media.MediaFormat
import android.media.MediaMuxer
import android.util.Log
import android.view.Surface
import app.kissik.studio.recording.EncodedSample
import app.kissik.studio.recording.ReplayBuffer
import java.io.File
import java.nio.ByteBuffer
import java.util.concurrent.atomic.AtomicBoolean

/** Receives encoded output from [VideoEncoder] / [AudioEncoder]. */
interface SampleSink {
    fun onFormat(audio: Boolean, format: MediaFormat)
    fun onSample(audio: Boolean, data: ByteBuffer, info: MediaCodec.BufferInfo)
}

/**
 * Hardware H.264/HEVC encoder fed with finished frames (bitmaps drawn on its input surface). One encoder can feed
 * several sinks: a file, the replay buffer, RTMP destinations, the PC.
 */
class VideoEncoder(val width: Int, val height: Int, bitrate: Int, fps: Int = 30, mime: String = MediaFormat.MIMETYPE_VIDEO_AVC,
                   private val sinks: List<SampleSink>) {
    private val codec = MediaCodec.createEncoderByType(mime)
    private val surface: Surface
    private val running = AtomicBoolean(true)
    private val drain: Thread
    private val dst = Rect(0, 0, width, height)
    @Volatile var requestKey = false

    init {
        val f = MediaFormat.createVideoFormat(mime, width, height).apply {
            setInteger(MediaFormat.KEY_COLOR_FORMAT, MediaCodecInfo.CodecCapabilities.COLOR_FormatSurface)
            setInteger(MediaFormat.KEY_BIT_RATE, bitrate)
            setInteger(MediaFormat.KEY_FRAME_RATE, fps)
            setInteger(MediaFormat.KEY_I_FRAME_INTERVAL, 1)
        }
        codec.configure(f, null, null, MediaCodec.CONFIGURE_FLAG_ENCODE)
        surface = codec.createInputSurface()
        codec.start()
        drain = Thread({ loop(codec, false, sinks, running) }, "kissik-venc").apply { start() }
    }

    /** Draws one frame, letterboxed to the encoder size. */
    fun draw(bmp: Bitmap) {
        if (!running.get()) return
        if (requestKey) { requestKey = false; codec.setParameters(android.os.Bundle().apply { putInt(MediaCodec.PARAMETER_KEY_REQUEST_SYNC_FRAME, 0) }) }
        runCatching {
            val c = surface.lockHardwareCanvas()
            try {
                c.drawColor(android.graphics.Color.BLACK)
                val s = minOf(width.toFloat() / bmp.width, height.toFloat() / bmp.height)
                val w = (bmp.width * s).toInt(); val h = (bmp.height * s).toInt()
                dst.set((width - w) / 2, (height - h) / 2, (width + w) / 2, (height + h) / 2)
                c.drawBitmap(bmp, null, dst, null)
            } finally { surface.unlockCanvasAndPost(c) }
        }
    }

    fun stop() {
        if (!running.getAndSet(false)) return
        runCatching { codec.signalEndOfInputStream() }
        drain.join(1500)
        runCatching { codec.stop() }; codec.release(); surface.release()
    }

    companion object {
        fun supports(mime: String) = MediaCodecList(MediaCodecList.REGULAR_CODECS).codecInfos.any { it.isEncoder && it.supportedTypes.any { t -> t.equals(mime, true) } }
    }
}

/** AAC encoder fed with 16-bit PCM from [AudioCapture]. */
class AudioEncoder(private val sampleRate: Int, private val sinks: List<SampleSink>) {
    private val codec = MediaCodec.createEncoderByType(MediaFormat.MIMETYPE_AUDIO_AAC)
    private val running = AtomicBoolean(true)
    private val drain: Thread
    private var samplesIn = 0L

    init {
        val f = MediaFormat.createAudioFormat(MediaFormat.MIMETYPE_AUDIO_AAC, sampleRate, 1).apply {
            setInteger(MediaFormat.KEY_AAC_PROFILE, MediaCodecInfo.CodecProfileLevel.AACObjectLC)
            setInteger(MediaFormat.KEY_BIT_RATE, 128_000)
            setInteger(MediaFormat.KEY_MAX_INPUT_SIZE, 16384)
        }
        codec.configure(f, null, null, MediaCodec.CONFIGURE_FLAG_ENCODE)
        codec.start()
        drain = Thread({ loop(codec, true, sinks, running) }, "kissik-aenc").apply { start() }
    }

    /** Timestamps follow the wall clock (System.nanoTime), like the video surface, so the two stay in sync. */
    fun feed(pcm: ShortArray, count: Int, timeUs: Long) {
        if (!running.get()) return
        runCatching {
            val i = codec.dequeueInputBuffer(10_000); if (i < 0) return
            val buf = codec.getInputBuffer(i)!!; buf.clear()
            val n = minOf(count, buf.capacity() / 2)
            buf.asShortBuffer().put(pcm, 0, n)
            codec.queueInputBuffer(i, 0, n * 2, timeUs, 0)
            samplesIn += n
        }
    }

    fun stop() {
        if (!running.getAndSet(false)) return
        runCatching {
            val i = codec.dequeueInputBuffer(10_000)
            if (i >= 0) codec.queueInputBuffer(i, 0, 0, System.nanoTime() / 1000, MediaCodec.BUFFER_FLAG_END_OF_STREAM)
        }
        drain.join(1500)
        runCatching { codec.stop() }; codec.release()
    }
}

private fun loop(codec: MediaCodec, audio: Boolean, sinks: List<SampleSink>, running: AtomicBoolean) {
    val info = MediaCodec.BufferInfo()
    while (true) {
        val idx = try { codec.dequeueOutputBuffer(info, 10_000) } catch (e: IllegalStateException) { break }
        when {
            idx == MediaCodec.INFO_OUTPUT_FORMAT_CHANGED -> sinks.forEach { runCatching { it.onFormat(audio, codec.outputFormat) } }
            idx >= 0 -> {
                val buf = codec.getOutputBuffer(idx)
                if (buf != null && info.size > 0 && info.flags and MediaCodec.BUFFER_FLAG_CODEC_CONFIG == 0) {
                    sinks.forEach { s -> runCatching { buf.position(info.offset); buf.limit(info.offset + info.size); s.onSample(audio, buf, info) }
                        .onFailure { Log.w("KISSIK", "sink failed", it) } }
                }
                codec.releaseOutputBuffer(idx, false)
                if (info.flags and MediaCodec.BUFFER_FLAG_END_OF_STREAM != 0) break
            }
            !running.get() && idx == MediaCodec.INFO_TRY_AGAIN_LATER -> { /* wait for EOS */ }
        }
    }
}

/**
 * Writes an MP4. Starts once every expected track has its format. Pause support: [pausedUs] is subtracted from
 * timestamps; [retimeFps] (time-lapse) replaces video timestamps with n / fps.
 */
class MuxerSink(val file: File, private val expectAudio: Boolean, private val retimeFps: Int = 0) : SampleSink {
    private val muxer = MediaMuxer(file.absolutePath, MediaMuxer.OutputFormat.MUXER_OUTPUT_MPEG_4)
    private var videoTrack = -1; private var audioTrack = -1
    private var started = false
    private var firstUs = -1L
    private var lastVideo = -1L; private var lastAudio = -1L
    private var frames = 0
    @Volatile var paused = false
    @Volatile var pausedUs = 0L
    var samplesWritten = 0; private set

    @Synchronized override fun onFormat(audio: Boolean, format: MediaFormat) {
        if (started) return
        if (audio) audioTrack = muxer.addTrack(format) else videoTrack = muxer.addTrack(format)
        if (videoTrack >= 0 && (!expectAudio || audioTrack >= 0)) { muxer.start(); started = true }
    }

    @Synchronized override fun onSample(audio: Boolean, data: ByteBuffer, info: MediaCodec.BufferInfo) {
        if (!started || paused) return
        if (!audio && firstUs < 0 && info.flags and MediaCodec.BUFFER_FLAG_KEY_FRAME == 0) return // start on a keyframe
        if (firstUs < 0) { if (audio) return; firstUs = info.presentationTimeUs }
        var t = if (retimeFps > 0 && !audio) frames * 1_000_000L / retimeFps else info.presentationTimeUs - firstUs - pausedUs
        if (t < 0) return
        if (audio) { if (t <= lastAudio) t = lastAudio + 1; lastAudio = t } else { if (t <= lastVideo) t = lastVideo + 1; lastVideo = t; frames++ }
        val out = MediaCodec.BufferInfo().apply { set(info.offset, info.size, t, info.flags) }
        muxer.writeSampleData(if (audio) audioTrack else videoTrack, data, out)
        samplesWritten++
    }

    /** Returns true when the file holds video. */
    @Synchronized fun finish(): Boolean {
        val ok = started && lastVideo >= 0
        runCatching { if (started) muxer.stop() }; runCatching { muxer.release() }
        if (!ok) file.delete()
        return ok
    }
}

/** Keeps the last N seconds in memory; [save] writes them to an MP4. */
class ReplaySink(seconds: Int) : SampleSink {
    val buffer = ReplayBuffer(seconds)
    private var videoFormat: MediaFormat? = null
    private var audioFormat: MediaFormat? = null

    @Synchronized override fun onFormat(audio: Boolean, format: MediaFormat) { if (audio) audioFormat = format else videoFormat = format }
    @Synchronized override fun onSample(audio: Boolean, data: ByteBuffer, info: MediaCodec.BufferInfo) {
        val bytes = ByteArray(info.size); data.get(bytes)
        buffer.add(EncodedSample(bytes, info.presentationTimeUs, info.flags and MediaCodec.BUFFER_FLAG_KEY_FRAME != 0, audio))
    }

    @Synchronized fun save(file: File): Boolean {
        val vf = videoFormat ?: return false
        val clip = buffer.snapshot(); if (clip.isEmpty()) return false
        val m = MediaMuxer(file.absolutePath, MediaMuxer.OutputFormat.MUXER_OUTPUT_MPEG_4)
        val vt = m.addTrack(vf); val at = audioFormat?.let { m.addTrack(it) } ?: -1
        m.start()
        val t0 = clip.first().timeUs
        val info = MediaCodec.BufferInfo()
        for (s in clip) {
            if (s.isAudio && at < 0) continue
            info.set(0, s.data.size, s.timeUs - t0, if (s.keyFrame) MediaCodec.BUFFER_FLAG_KEY_FRAME else 0)
            m.writeSampleData(if (s.isAudio) at else vt, ByteBuffer.wrap(s.data), info)
        }
        m.stop(); m.release()
        return true
    }
}
