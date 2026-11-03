package app.kissik.studio.recording

/** One encoded sample (H.264/HEVC access unit or AAC frame) with its presentation time. */
class EncodedSample(val data: ByteArray, val timeUs: Long, val keyFrame: Boolean, val isAudio: Boolean = false)

/**
 * Replay buffer: keeps the last [seconds] of encoded video (and audio) in memory. Trimming always leaves a keyframe at
 * the front, so a saved clip starts cleanly. [snapshot] returns what to write when the user taps "Save replay".
 */
class ReplayBuffer(var seconds: Int = 30, private val maxBytes: Long = 200L * 1024 * 1024) {
    private val samples = ArrayDeque<EncodedSample>()
    private var bytes = 0L

    val size get() = samples.size
    val durationUs: Long get() = if (samples.isEmpty()) 0 else samples.last().timeUs - samples.first().timeUs

    fun add(s: EncodedSample) {
        samples.addLast(s); bytes += s.data.size
        trim()
    }

    private fun trim() {
        val limit = seconds * 1_000_000L
        val newest = samples.lastOrNull()?.timeUs ?: return
        // Find the latest video keyframe that still keeps at least [seconds] of history, drop everything before it.
        while (samples.isNotEmpty() && (newest - samples.first().timeUs > limit || bytes > maxBytes)) {
            val nextKey = samples.indexOfFirst { it !== samples.first() && it.keyFrame && !it.isAudio }
            if (nextKey <= 0) break
            if (newest - samples[nextKey].timeUs < limit && bytes <= maxBytes) break
            repeat(nextKey) { bytes -= samples.removeFirst().data.size }
        }
    }

    /** The buffered clip, starting at a video keyframe (audio before it is dropped). */
    fun snapshot(): List<EncodedSample> {
        val firstKey = samples.indexOfFirst { it.keyFrame && !it.isAudio }
        if (firstKey < 0) return emptyList()
        val startUs = samples[firstKey].timeUs
        return samples.filterIndexed { i, s -> i >= firstKey || (s.isAudio && s.timeUs >= startUs) }
            .filter { it.timeUs >= startUs }
    }

    fun clear() { samples.clear(); bytes = 0 }
}

enum class Quality(val label: String, val shortSide: Int) { HD("1080p", 1080), UHD("4K", 2160), SD("720p", 720) }

/** Recording state machine with pause/resume, shared so both platforms behave the same and it's unit tested. */
class RecordingClock {
    enum class State { Idle, Recording, Paused }
    var state = State.Idle; private set
    private var startedAt = 0L
    private var pausedTotal = 0L
    private var pausedAt = 0L

    fun start(nowMs: Long) { state = State.Recording; startedAt = nowMs; pausedTotal = 0 }
    fun pause(nowMs: Long) { if (state == State.Recording) { state = State.Paused; pausedAt = nowMs } }
    fun resume(nowMs: Long) { if (state == State.Paused) { pausedTotal += nowMs - pausedAt; state = State.Recording } }
    fun stop() { state = State.Idle }
    /** Elapsed recorded time, excluding pauses. */
    fun elapsedMs(nowMs: Long): Long = when (state) {
        State.Idle -> 0
        State.Recording -> nowMs - startedAt - pausedTotal
        State.Paused -> pausedAt - startedAt - pausedTotal
    }
    /** Presentation-time offset to subtract from samples so a paused gap doesn't appear in the file. */
    fun pausedUs() = pausedTotal * 1000

    companion object {
        fun format(ms: Long): String {
            val s = ms / 1000
            val h = s / 3600; val m = (s % 3600) / 60; val sec = s % 60
            fun two(v: Long) = v.toString().padStart(2, '0')
            return if (h > 0) "$h:${two(m)}:${two(sec)}" else "${two(m)}:${two(sec)}"
        }
    }
}

/** Time-lapse: which camera frames to keep for a given capture interval and playback rate. */
data class TimeLapse(val intervalMs: Long = 500, val playbackFps: Int = 30) {
    /** How much faster than real time the result plays. */
    val speedUp get() = intervalMs * playbackFps / 1000f
    fun shouldCapture(lastCaptureMs: Long, nowMs: Long) = lastCaptureMs < 0 || nowMs - lastCaptureMs >= intervalMs
    /** Presentation time of the n-th kept frame in the output video. */
    fun frameTimeUs(n: Int) = n * 1_000_000L / playbackFps
}
