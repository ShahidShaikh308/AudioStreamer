package com.audiostreamer.audio_plane.transport

import android.util.Log
import com.audiostreamer.audio_plane.protocol.PacketSerializer
import com.audiostreamer.audio_plane.protocol.ProtocolSpec
import java.net.DatagramPacket
import java.net.DatagramSocket
import java.net.Inet4Address
import java.net.InetAddress
import java.security.SecureRandom

data class SenderDiagnostics(
    val packetsSent: Long = 0,
    val bytesSent: Long = 0,
    val sampleRate: Int = 0,
    val channels: Int = 2,
)

/** Android UDP transport for existing protocol v3; the caller owns one sender per lane. */
class UdpSender(
    destinationIp: String,
    private val destinationPort: Int,
) : AutoCloseable {
    private val validatedDestinationPort = destinationPort.also {
        require(it in 1..65535) { "UDP destination port must be 1..65535" }
    }
    private val destination: InetAddress = InetAddress.getByName(destinationIp).also {
        require(it is Inet4Address) { "Protocol v3 currently supports IPv4 destinations only" }
    }
    private val socket = DatagramSocket()
    private val serializer = PacketSerializer()
    private val datagramBuffer = ByteArray(ProtocolSpec.MAX_DATAGRAM_SIZE)
    private val datagram = DatagramPacket(datagramBuffer, datagramBuffer.size, destination, validatedDestinationPort)
    private val streamId = SecureRandom().nextLong().let { if (it == 0L) 1L else it }
    private var sequenceNumber = 0L
    private var packetsSent = 0L
    private var bytesSent = 0L
    private var isClosed = false

    @Synchronized
    fun sendPcm(data: ByteArray, length: Int, sampleRate: Int, channels: Int = 2) {
        check(!isClosed) { "UDP sender is closed" }
        require(sampleRate > 0 && channels == 2)
        require(length > 0 && length <= data.size)
        val bytesPerFrame = channels * Float.SIZE_BYTES
        require(length % bytesPerFrame == 0) { "PCM is not aligned to complete stereo frames" }

        val maxPayload = (ProtocolSpec.MAX_DATAGRAM_SIZE - ProtocolSpec.HEADER_SIZE) / bytesPerFrame * bytesPerFrame
        var offset = 0
        while (offset < length) {
            val payloadLength = minOf(maxPayload, length - offset)
            val datagramLength = serializer.write(
                target = datagramBuffer,
                sequenceNumber = sequenceNumber,
                streamId = streamId,
                sampleRate = sampleRate,
                channels = channels,
                payload = data,
                payloadOffset = offset,
                payloadLength = payloadLength,
            )
            datagram.setData(datagramBuffer, 0, datagramLength)
            try {
                socket.send(datagram)
            } catch (error: Exception) {
                Log.w(TAG, "network_send_error destination=${destination.hostAddress}:$destinationPort", error)
                throw error
            }
            sequenceNumber = (sequenceNumber + 1) and UINT32_MASK
            packetsSent++
            bytesSent += datagramLength
            offset += payloadLength
        }
    }

    @Synchronized
    fun diagnostics(sampleRate: Int): SenderDiagnostics = SenderDiagnostics(
        packetsSent = packetsSent,
        bytesSent = bytesSent,
        sampleRate = sampleRate,
    )

    @Synchronized
    override fun close() {
        if (isClosed) return
        isClosed = true
        socket.close()
        Log.i(TAG, "sender_stopped packets=$packetsSent bytes=$bytesSent")
    }

    private companion object {
        const val TAG = "AudioStreamerSender"
        const val UINT32_MASK = 0xFFFF_FFFFL
    }
}
