package app.kissik.studio.crash

import kotlinx.serialization.json.*
import kotlin.random.Random

/**
 * Opt-in crash reports, the same as the desktop's CrashReports.cs: only the error text (scrubbed of user names,
 * paths, e-mail addresses and the device name), the KISSIK version and the OS version go to the project's Sentry
 * inbox. Never video, audio, files or settings. Nothing is sent unless the person said yes.
 */
object CrashReports {
    /** Same Sentry project DSN as the desktop app. */
    const val DSN = "https://a8a20e3e7d86bf3fd8363ab4b1eaeb74@o4512361962340352.ingest.de.sentry.io/4512361969877072"

    data class Endpoint(val url: String, val key: String, val project: String)

    fun parseDsn(dsn: String): Endpoint? {
        if (!dsn.startsWith("https://")) return null
        val rest = dsn.removePrefix("https://")
        val at = rest.indexOf('@'); if (at <= 0) return null
        val key = rest.substring(0, at).substringBefore(':')
        val hostPort = rest.substring(at + 1).substringBefore('/')
        val project = rest.substring(at + 1).substringAfter('/', "").trim('/').substringAfterLast('/')
        if (key.isEmpty() || hostPort.isEmpty() || project.isEmpty()) return null
        return Endpoint("https://$hostPort/api/$project/envelope/", key, project)
    }

    val isConfigured get() = parseDsn(DSN) != null

    /** Removes anything personal. [deviceName] is the phone's name (e.g. "Asha's Pixel"), scrubbed like the PC name. */
    fun scrub(input: String, deviceName: String? = null): String {
        var text = input
        text = Regex("""(?i)([a-z]:\\Users\\)[^\\"'\r\n]+""").replace(text, "$1<user>")
        text = Regex("""(?i)(/Users/|/home/)[^/"'\r\n]+""").replace(text, "$1<user>")
        // Android app data paths carry the user/profile number and package; iOS paths carry the app container id.
        text = Regex("""/data/user/\d+/""").replace(text, "/data/user/<n>/")
        text = Regex("""(?i)/var/mobile/Containers/Data/Application/[0-9A-F-]+""").replace(text, "/var/mobile/Containers/Data/Application/<app>")
        text = Regex("""[\w.+-]+@[\w-]+\.[\w.-]+""").replace(text, "<email>")
        if (deviceName != null && deviceName.length > 2) text = Regex(Regex.escape(deviceName), RegexOption.IGNORE_CASE).replace(text, "<device>")
        return if (text.length > 16000) text.takeLast(16000) else text
    }

    /** Splits crash.log text into individual crashes (each starts with an ISO timestamp). */
    fun splitEntries(log: String): List<String> =
        log.split(Regex("(?m)^(?=\\d{4}-\\d{2}-\\d{2}T)")).map { it.trim() }.filter { it.isNotEmpty() }

    /** The Sentry envelope for one crash (header line, item header line, event JSON). */
    fun buildEnvelope(dsn: String, report: String, appVersion: String, osName: String, osVersion: String, isoNow: String,
                      deviceName: String? = null, id: String = newEventId()): String {
        val lines = report.split('\n')
        val first = lines.firstOrNull { Regex("""\w+(\.\w+)*(Exception|Error)""").containsMatchIn(it) } ?: lines.first()
        val m = Regex("""([\w.$]+(?:Exception|Error)):?\s*(.*)""").find(first)
        val type = m?.groupValues?.get(1) ?: "Crash"
        val value = m?.groupValues?.get(2)?.trim() ?: first.trim()
        val evt = buildJsonObject {
            put("event_id", id); put("timestamp", isoNow); put("platform", "java"); put("level", "fatal")
            put("release", "kissik-mobile@$appVersion"); put("environment", "production")
            putJsonObject("contexts") { putJsonObject("os") { put("name", osName); put("version", osVersion) } }
            putJsonObject("exception") { putJsonArray("values") { addJsonObject { put("type", type); put("value", scrub(value, deviceName)) } } }
            putJsonObject("extra") { put("report", scrub(report, deviceName)) }
        }
        val header = buildJsonObject { put("event_id", id); put("dsn", dsn); put("sent_at", isoNow) }
        return "$header\n{\"type\":\"event\"}\n$evt\n"
    }

    fun authHeader(key: String, appVersion: String) = "Sentry sentry_version=7, sentry_key=$key, sentry_client=kissik-mobile/$appVersion"

    private fun newEventId(): String = buildString { repeat(32) { append("0123456789abcdef"[Random.nextInt(16)]) } }
}

/** The consent states: nothing is sent before the person picks "Send automatically" or "Ask each time". */
enum class CrashConsent { NotAsked, Never, AskEachTime, Always }
