package app.kissik.camera

import android.content.Context
import android.net.Uri
import java.net.InetSocketAddress
import java.net.Socket
import java.security.MessageDigest
import java.security.SecureRandom
import java.security.cert.CertificateException
import java.security.cert.X509Certificate
import javax.net.ssl.SSLContext
import javax.net.ssl.SSLSocketFactory
import javax.net.ssl.X509TrustManager

/** The PC this phone is paired with: parsed from the QR link `https://<pc-ip>:47820/?t=<token>`. */
data class Pairing(val host: String, val port: Int, val token: String, val pin: String?) {
    val wifiUrl get() = "wss://$host:$port/ws?t=$token"
    val usbUrl get() = "ws://localhost:${port + 1}/ws?t=$token"

    companion object {
        /** Accepts the QR link (or anything with host, port and a 16-hex-digit token). Null if it isn't a KISSIK link. */
        fun parse(link: String): Pairing? = runCatching {
            val u = Uri.parse(link.trim())
            val t = u.getQueryParameter("t") ?: return null
            val host = u.host ?: return null
            if (u.scheme != "https" || !t.matches(Regex("[0-9a-fA-F]{16}"))) return null
            Pairing(host, if (u.port > 0) u.port else 47820, t.lowercase(), null)
        }.getOrNull()

        fun load(ctx: Context): Pairing? {
            val p = Prefs.of(ctx)
            val host = p.getString("host", null) ?: return null
            return Pairing(host, p.getInt("port", 47820), p.getString("token", "")!!, p.getString("pin", null))
        }
    }

    fun save(ctx: Context) = Prefs.of(ctx).edit().putString("host", host).putInt("port", port)
        .putString("token", token).putString("pin", pin).apply()
}

fun forgetPairing(ctx: Context) = Prefs.of(ctx).edit().remove("host").remove("port").remove("token").remove("pin").apply()

/** True when `adb reverse` has made the PC's USB listener reachable as this phone's localhost. */
fun usbReachable(port: Int): Boolean = runCatching {
    Socket().use { it.connect(InetSocketAddress("127.0.0.1", port), 300); true }
}.getOrDefault(false)

/**
 * Trust-on-first-use for the PC's self-made certificate: the first connect after scanning records its SHA-256,
 * and every later connect must present that exact certificate. Nothing else is trusted.
 */
class PinningTrust(private val pinned: String?, private val onFirstPin: (String) -> Unit) : X509TrustManager {
    override fun checkClientTrusted(chain: Array<out X509Certificate>, authType: String) = throw CertificateException()
    override fun checkServerTrusted(chain: Array<out X509Certificate>, authType: String) {
        val fp = MessageDigest.getInstance("SHA-256").digest(chain[0].encoded).joinToString("") { "%02x".format(it) }
        if (pinned == null) onFirstPin(fp)
        else if (pinned != fp) throw CertificateException("PC certificate changed — scan the code again")
    }
    override fun getAcceptedIssuers(): Array<X509Certificate> = emptyArray()

    fun socketFactory(): SSLSocketFactory =
        SSLContext.getInstance("TLS").apply { init(null, arrayOf(this@PinningTrust), SecureRandom()) }.socketFactory
}
