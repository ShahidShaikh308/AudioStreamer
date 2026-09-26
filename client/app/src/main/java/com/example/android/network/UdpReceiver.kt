package com.example.android.network

import java.net.DatagramPacket
import java.net.DatagramSocket
import java.net.InetSocketAddress
import java.net.SocketException
import java.net.SocketTimeoutException
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.Job
import kotlinx.coroutines.currentCoroutineContext
import kotlinx.coroutines.isActive
import kotlinx.coroutines.launch

data class NetworkDiagnostics(
    val isListening: Boolean = false,
    val isConnected: Boolean = false,
    val sampleRate: Int = 0,
    val channels: Int = 0,
    val packetsReceived: Long = 0,
    val droppedPackets: Long = 0,
)

/** Receives and validates UDP packets on Dispatchers.IO. */
class UdpReceiver(
    private val port: Int = 5005,
    private val parser: PacketParser = PacketParser(),
) {
    @Volatile private var socket: DatagramSocket? = null
    @Volatile private var receiveJob: Job? = null
    private var streamId: Long? = null
    private var lastSequence: Long? = null
    private var lastAudioAtMs = 0L
    private var packetsReceived = 0L
    private var droppedPackets = 0L
    private var lastStatusAtMs = 0L
    private var currentRate = 0
    private var currentChannels = 0

    init {
        require(port in 1..65535) { "UDP port must be between 1 and 65535" }
    }

    fun start(
        scope: CoroutineScope,
        onPacket: (AudioPacket) -> Unit,
        onDiagnostics: (NetworkDiagnostics) -> Unit,
        onError: (Throwable) -> Unit,
    ): Job {
        check(receiveJob?.isActive != true) { "UDP receiver is already running" }
        receiveJob = scope.launch(Dispatchers.IO) {
            try {
                DatagramSocket(null).use { udp ->
                    udp.reuseAddress = true
                    udp.bind(InetSocketAddress(port))
                    udp.soTimeout = RECEIVE_TIMEOUT_MS
                    socket = udp
                    emitDiagnostics(onDiagnostics, force = true)

                    val buffer = ByteArray(MAX_DATAGRAM_SIZE + 1)
                    while (currentCoroutineContext().isActive) {
                        val datagram = DatagramPacket(buffer, buffer.size)
                        try {
                            udp.receive(datagram)
                        } catch (_: SocketTimeoutException) {
                            emitDiagnostics(onDiagnostics)
                            continue
                        }

                        val packet = try {
                            parser.parse(datagram.data, datagram.length)
                        } catch (_: PacketParseException) {
                            droppedPackets++
                            emitDiagnostics(onDiagnostics)
                            continue
                        }

                        if (!acceptSequence(packet)) {
                            emitDiagnostics(onDiagnostics)
                            continue
                        }
                        if (packet.packetType != PacketType.AUDIO) continue

                        packetsReceived++
                        currentRate = packet.sampleRate
                        currentChannels = packet.channels
                        lastAudioAtMs = android.os.SystemClock.elapsedRealtime()
                        onPacket(packet)
                        emitDiagnostics(onDiagnostics)
                    }
                }
            } catch (cancelled: CancellationException) {
                throw cancelled
            } catch (error: SocketException) {
                if (currentCoroutineContext().isActive) onError(error)
            } catch (error: Throwable) {
                if (currentCoroutineContext().isActive) onError(error)
            } finally {
                socket = null
                emitDiagnostics(onDiagnostics, force = true, listening = false)
            }
        }
        return receiveJob!!
    }

    private fun acceptSequence(packet: AudioPacket): Boolean {
        if (packet.streamId != streamId) {
            streamId = packet.streamId
            lastSequence = null
        }
        if ((packet.flags and FLAG_DISCONTINUITY) != 0) lastSequence = null

        val previous = lastSequence
        if (previous != null) {
            val distance = (packet.sequenceNumber - previous) and UINT32_MASK
            if (distance == 0L || distance >= HALF_SEQUENCE_SPACE) {
                droppedPackets++
                return false
            }
            if (distance > 1L) droppedPackets += distance - 1L
        }
        lastSequence = packet.sequenceNumber
        return true
    }

    private fun emitDiagnostics(
        callback: (NetworkDiagnostics) -> Unit,
        force: Boolean = false,
        listening: Boolean = socket != null,
    ) {
        val now = android.os.SystemClock.elapsedRealtime()
        if (!force && now - lastStatusAtMs < STATUS_INTERVAL_MS) return
        lastStatusAtMs = now
        val connected = listening && lastAudioAtMs != 0L && now - lastAudioAtMs < CONNECTION_TIMEOUT_MS
        callback(
            NetworkDiagnostics(
                isListening = listening,
                isConnected = connected,
                sampleRate = currentRate,
                channels = currentChannels,
                packetsReceived = packetsReceived,
                droppedPackets = droppedPackets,
            ),
        )
    }

    fun stop() {
        socket?.close() // Closing the socket unblocks DatagramSocket.receive().
        receiveJob?.cancel()
        receiveJob = null
    }

    private companion object {
        const val MAX_DATAGRAM_SIZE = 1200
        const val RECEIVE_TIMEOUT_MS = 250
        const val STATUS_INTERVAL_MS = 250L
        const val CONNECTION_TIMEOUT_MS = 1500L
        const val FLAG_DISCONTINUITY = 1
        const val HALF_SEQUENCE_SPACE = 0x8000_0000L
        const val UINT32_MASK = 0xFFFF_FFFFL
    }
}
