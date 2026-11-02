package app.kissik.studio.captions

/**
 * Turns a stream of partial and final recognition results into the two short lines shown on screen. Final text is
 * kept for a few seconds; partial text replaces itself as the recogniser refines it.
 */
class CaptionTrack(private val maxChars: Int = 42, private val holdMs: Long = 4000) {
    private val finals = ArrayDeque<Pair<String, Long>>()
    private var partial = ""

    fun onPartial(text: String) { partial = text.trim() }
    fun onFinal(text: String, nowMs: Long) {
        val t = text.trim()
        if (t.isNotEmpty()) finals.addLast(t to nowMs)
        partial = ""
        while (finals.size > 6) finals.removeFirst()
    }

    /** Up to two lines, newest at the bottom, wrapped on word boundaries. */
    fun lines(nowMs: Long): List<String> {
        while (finals.isNotEmpty() && nowMs - finals.first().second > holdMs) finals.removeFirst()
        val text = (finals.map { it.first } + partial).filter { it.isNotEmpty() }.joinToString(" ")
        return wrap(text, maxChars).takeLast(2)
    }

    fun clear() { finals.clear(); partial = "" }

    companion object {
        fun wrap(text: String, width: Int): List<String> {
            val out = mutableListOf<String>()
            var line = StringBuilder()
            for (word in text.split(' ').filter { it.isNotEmpty() }) {
                if (line.isNotEmpty() && line.length + 1 + word.length > width) { out += line.toString(); line = StringBuilder() }
                if (line.isNotEmpty()) line.append(' ')
                line.append(word)
            }
            if (line.isNotEmpty()) out += line.toString()
            return out
        }
    }
}

enum class CaptionEngineKind(val label: String, val detail: String) {
    Fast("Fast", "The phone's own on-device speech recognition"),
    Accurate("Accurate", "Whisper on the phone: slower, better with accents and noise"),
}

/** SRT subtitle file from timed caption segments, saved next to recordings. */
object Srt {
    data class Cue(val startMs: Long, val endMs: Long, val text: String)
    fun write(cues: List<Cue>): String = buildString {
        cues.forEachIndexed { i, c ->
            append(i + 1).append('\n').append(ts(c.startMs)).append(" --> ").append(ts(c.endMs)).append('\n').append(c.text).append("\n\n")
        }
    }
    private fun ts(ms: Long): String {
        val h = ms / 3_600_000; val m = ms / 60_000 % 60; val s = ms / 1000 % 60; val r = ms % 1000
        return "${h.toString().padStart(2, '0')}:${m.toString().padStart(2, '0')}:${s.toString().padStart(2, '0')},${r.toString().padStart(3, '0')}"
    }
}
