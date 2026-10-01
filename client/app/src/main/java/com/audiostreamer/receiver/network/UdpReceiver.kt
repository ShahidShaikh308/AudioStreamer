package com.audiostreamer.receiver.network

import java.net.DatagramPacket
import java.net.DatagramSocket
import java.net.InetSocketAddress
import java.net.SocketTimeoutException
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.CoroutineStart
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.Job
import kotlinx.coroutines.currentCoroutineContext
import kotlinx.coroutines.isActive
import kotlinx.coroutines.launch

data class NetworkDiagnostics(
    val isListening: Boolean = false,
    val isConnected: Boolean = false,
    val hasReceivedAudio: Boolean = false,
    val lastPacketInvalid: Boolean = false,
    val lastInvalidReason: String? = null,
    val sampleRate: Int = 0,
    val channels: Int = 0,
    val packetsReceived: Long = 0,
    val droppedPackets: Long = 0,
    val invalidPackets: Long = 0,
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
    private var invalidPackets = 0L
    private var lastPacketInvalid = false
    private var lastInvalidReason: String? = null
    private var lastStatusAtMs = 0L
    private var currentRate = 0
    private var currentChannels = 0

    init {
        require(port in 1..65535) { "UDP port must be between 1 and 65535" }
    }

    @Synchronized
    fun start(
        scope: CoroutineScope,
        onPacket: (AudioPacket) -> Unit,
        onDiagnostics: (NetworkDiagnostics) -> Unit,
        onError: (Throwable) -> Unit,
    ): Job {
        check(receiveJob == null) { "UDP receiver is already running or stopping" }
        resetDiagnostics()

        val job = scope.launch(Dispatchers.IO, start = CoroutineStart.LAZY) {
            var localSocket: DatagramSocket? = null
            try {
                val udp = DatagramSocket(null)
                localSocket = udp
                udp.use {
                    it.reuseAddress = true
                    it.bind(InetSocketAddress(port))
                    it.soTimeout = RECEIVE_TIMEOUT_MS
                    socket = it
                    emitDiagnostics(onDiagnostics, force = true)

                    val buffer = ByteArray(MAX_DATAGRAM_SIZE + 1)
                    val datagram = DatagramPacket(buffer, buffer.size)
                    while (currentCoroutineContext().isActive) {
                        // receive() updates packet.length to the last datagram size.
                        // Restore capacity so a later full-size datagram is not truncated.
                        datagram.length = buffer.size
                        try {
                            it.receive(datagram)
                        } catch (_: SocketTimeoutException) {
                            emitDiagnostics(onDiagnostics)
                            continue
                        }

                        val packet = try {
                            parser.parse(datagram.data, datagram.length)
                        } catch (error: PacketParseException) {
                            invalidPackets++
                            droppedPackets++
                            lastPacketInvalid = true
                            lastInvalidReason = error.message
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
                        lastPacketInvalid = false
                        lastInvalidReason = null
                        onPacket(packet)
                        emitDiagnostics(onDiagnostics)
                    }
                }
            } catch (cancelled: CancellationException) {
                throw cancelled
            } catch (error: Exception) {
                if (currentCoroutineContext().isActive) runCatching { onError(error) }
            } finally {
                if (socket === localSocket) socket = null
                emitDiagnostics(onDiagnostics, force = true, listening = false)
            }
        }
        receiveJob = job
        job.invokeOnCompletion {
            synchronized(this@UdpReceiver) {
                if (receiveJob === job) receiveJob = null
            }
        }
        job.start()
        return job
    }

    private fun resetDiagnostics() {
        streamId = null
        lastSequence = null
        lastAudioAtMs = 0L
        packetsReceived = 0L
        droppedPackets = 0L
        invalidPackets = 0L
        lastPacketInvalid = false
        lastInvalidReason = null
        lastStatusAtMs = 0L
        currentRate = 0
        currentChannels = 0
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
        val diagnostics = NetworkDiagnostics(
            isListening = listening,
            isConnected = connected,
            hasReceivedAudio = packetsReceived > 0,
            lastPacketInvalid = lastPacketInvalid,
            lastInvalidReason = lastInvalidReason,
            sampleRate = currentRate,
            channels = currentChannels,
            packetsReceived = packetsReceived,
            droppedPackets = droppedPackets,
            invalidPackets = invalidPackets,
        )
        runCatching { callback(diagnostics) }
    }

    /** Close the socket first so a blocking receive wakes immediately, then cancel the job. */
    @Synchronized
    fun stop(): Job? {
        val job = receiveJob ?: return null
        socket?.close()
        job.cancel()
        return job
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
