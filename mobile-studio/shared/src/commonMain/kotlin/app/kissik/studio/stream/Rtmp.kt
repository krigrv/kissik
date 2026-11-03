package app.kissik.studio.stream

import kotlinx.serialization.Serializable

/** One place to stream to. The key is kept separately so it can be hidden in the UI. */
@Serializable
data class Destination(val id: String, val name: String, val url: String, val key: String = "", val enabled: Boolean = true) {
    /** Full publish URL (server URL + stream key). */
    val publishUrl: String get() = if (key.isBlank()) url.trimEnd('/') else url.trimEnd('/') + "/" + key.trim()
    val masked: String get() = if (key.isBlank()) url else url.trimEnd('/') + "/" + "•".repeat(minOf(8, key.length))
}

object Rtmp {
    /** Checks an RTMP(S) URL. Returns an error message for the user, or null when it's fine. */
    fun validate(url: String): String? {
        val u = url.trim()
        if (u.isEmpty()) return "Enter the server address"
        if (!(u.startsWith("rtmp://") || u.startsWith("rtmps://"))) return "The address must start with rtmp:// or rtmps://"
        val host = u.substringAfter("://").substringBefore('/').substringBefore(':')
        if (host.isEmpty()) return "The address has no server name"
        val port = u.substringAfter("://").substringBefore('/').substringAfter(':', "")
        if (port.isNotEmpty() && (port.toIntOrNull() ?: -1) !in 1..65535) return "The port must be a number from 1 to 65535"
        if (u.substringAfter("://").substringAfter('/', "").isEmpty()) return "Add the app path, for example rtmp://server/live"
        return null
    }

    /** Splits a pasted full URL into server and key at the last path segment, e.g. rtmp://host/live/abc → (rtmp://host/live, abc). */
    fun split(full: String): Pair<String, String> {
        val u = full.trim().trimEnd('/')
        val path = u.substringAfter("://").substringAfter('/', "")
        return if (path.contains('/')) u.substringBeforeLast('/') to u.substringAfterLast('/') else u to ""
    }
}

enum class VideoCodec(val label: String, val mime: String) { H264("H.264", "video/avc"), HEVC("HEVC", "video/hevc") }

/** Encoder settings for streaming, with a bitrate suggestion per resolution. */
data class StreamSettings(val shortSide: Int = 720, val fps: Int = 30, val codec: VideoCodec = VideoCodec.H264, val bitrateKbps: Int = suggestedKbps(720, 30)) {
    companion object {
        fun suggestedKbps(shortSide: Int, fps: Int): Int {
            val base = when { shortSide >= 2160 -> 20000; shortSide >= 1440 -> 9000; shortSide >= 1080 -> 4500; shortSide >= 720 -> 2500; else -> 1200 }
            return if (fps > 30) base * 3 / 2 else base
        }
    }
}

/** Per-destination live status shown in the UI. */
enum class LinkState { Idle, Connecting, Live, Failed }
