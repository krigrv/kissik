package app.kissik.camera

import java.net.InetAddress
import java.net.Socket
import javax.net.SocketFactory

/** Plain sockets with TCP_NODELAY on, so each video frame leaves at once (no Nagle batching). */
object NoDelaySocketFactory : SocketFactory() {
    private val base = getDefault()
    private fun Socket.fast() = apply { tcpNoDelay = true }
    override fun createSocket(): Socket = base.createSocket().fast()
    override fun createSocket(host: String, port: Int): Socket = base.createSocket(host, port).fast()
    override fun createSocket(host: String, port: Int, local: InetAddress, localPort: Int): Socket = base.createSocket(host, port, local, localPort).fast()
    override fun createSocket(host: InetAddress, port: Int): Socket = base.createSocket(host, port).fast()
    override fun createSocket(host: InetAddress, port: Int, local: InetAddress, localPort: Int): Socket = base.createSocket(host, port, local, localPort).fast()
}
