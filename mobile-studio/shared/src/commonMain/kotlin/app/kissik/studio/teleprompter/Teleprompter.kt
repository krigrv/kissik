package app.kissik.studio.teleprompter

/** Scroll model for the teleprompter: words per minute → pixels per second for the laid-out text. */
class Teleprompter(var wordsPerMinute: Int = 140) {
    var offsetPx = 0f; private set
    var playing = false

    /** Advances by [dtSec]; [totalHeightPx] and [wordCount] come from the laid-out text. Returns the new offset. */
    fun tick(dtSec: Float, totalHeightPx: Float, wordCount: Int): Float {
        if (!playing || wordCount == 0 || totalHeightPx <= 0f) return offsetPx
        val pxPerWord = totalHeightPx / wordCount
        offsetPx = (offsetPx + pxPerWord * wordsPerMinute / 60f * dtSec).coerceAtMost(totalHeightPx)
        if (offsetPx >= totalHeightPx) playing = false
        return offsetPx
    }

    fun reset() { offsetPx = 0f; playing = false }
    fun nudge(px: Float, totalHeightPx: Float) { offsetPx = (offsetPx + px).coerceIn(0f, totalHeightPx) }

    companion object {
        fun wordCount(text: String) = text.split(Regex("\\s+")).count { it.isNotBlank() }
        /** Reading time at [wpm], for the "about N min" hint. */
        fun minutes(text: String, wpm: Int) = wordCount(text).toFloat() / wpm.coerceAtLeast(1)
    }
}
