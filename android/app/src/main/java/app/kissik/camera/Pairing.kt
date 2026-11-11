package app.kissik.camera

import android.content.Context
import android.net.Uri
import java.net.HttpURLConnection
import java.net.InetSocketAddress
import java.net.Socket
import java.net.URL
import java.security.MessageDigest
import java.security.SecureRandom
import java.security.cert.CertificateException
import java.security.cert.X509Certificate
import javax.crypto.Mac
import javax.crypto.spec.SecretKeySpec
import javax.net.ssl.SSLContext
import javax.net.ssl.SSLSocketFactory
import javax.net.ssl.X509TrustManager

/** The PC this phone is paired with: parsed from the QR link `https://<pc-ip>:47820/?t=<token>`. */
data class Pairing(val host: String, val port: Int, val token: String, val pin: String?) {
    val wifiUrl get() = "wss://$host:$port/ws?t=$token"
    val usbUrl get() = "ws://localhost:${port + 1}/ws?t=$token"

    companion object {
        /** Accepts the QR link (host, port and a 32-hex-digit token; older KISSIK used 16). Null if it isn't a KISSIK link. */
        fun parse(link: String): Pairing? = runCatching {
            val u = Uri.parse(link.trim())
            val t = u.getQueryParameter("t") ?: return null
            val host = u.host ?: return null
            if (u.scheme != "https" || !t.matches(Regex("[0-9a-fA-F]{16}([0-9a-fA-F]{16})?"))) return null
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

/** True for addresses a PC on the same Wi-Fi can have: private ranges (10/8, 172.16/12, 192.168/16) and link-local. */
fun isLocalNetworkHost(host: String): Boolean {
    val p = host.split('.').mapNotNull { it.toIntOrNull() }
    if (p.size != 4 || p.any { it !in 0..255 }) return false
    return p[0] == 10 || (p[0] == 172 && p[1] in 16..31) || (p[0] == 192 && p[1] == 168) || (p[0] == 169 && p[1] == 254)
}

fun forgetPairing(ctx: Context) = Prefs.of(ctx).edit().remove("host").remove("port").remove("token").remove("pin").apply()

/** True when `adb reverse` has made the PC's USB listener reachable as this phone's localhost. */
fun usbReachable(port: Int): Boolean = runCatching {
    Socket().use { it.connect(InetSocketAddress("127.0.0.1", port), 300); true }
}.getOrDefault(false)

/** What KISSIK on the PC must answer for [nonce] to show it knows the pairing token (HMAC-SHA256, lowercase hex). */
fun usbProof(token: String, nonce: String): String {
    val mac = Mac.getInstance("HmacSHA256").apply { init(SecretKeySpec(token.toByteArray(Charsets.US_ASCII), "HmacSHA256")) }
    return mac.doFinal("kissik-usb:$nonce".toByteArray(Charsets.US_ASCII)).joinToString("") { "%02x".format(it) }
}

/**
 * Any app on this phone can listen on the USB port, so the plain-text USB link is used only after the listener
 * proves it knows the pairing token, without the phone ever sending it: the phone sends a random challenge and the
 * PC must answer with its HMAC. True when it did. KISSIK versions from before this check can't answer.
 */
fun usbTrusted(p: Pairing): Boolean = runCatching {
    val nonce = ByteArray(16).also { SecureRandom().nextBytes(it) }.joinToString("") { "%02x".format(it) }
    val conn = (URL("http://127.0.0.1:${p.port + 1}/proof?n=$nonce").openConnection() as HttpURLConnection).apply {
        connectTimeout = 500; readTimeout = 1500; instanceFollowRedirects = false
    }
    try {
        if (conn.responseCode != 200) return false
        val answer = conn.inputStream.bufferedReader().use { it.readText() }.trim().lowercase()
        MessageDigest.isEqual(answer.toByteArray(), usbProof(p.token, nonce).toByteArray())
    } finally { conn.disconnect() }
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
