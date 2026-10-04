package com.audiostreamer.audio_plane.protocol

import java.nio.ByteBuffer
import java.nio.ByteOrder

/** Wire constants from root PROTOCOL.md; field reads remain sequential. */
internal object ProtocolSpec {
    val magic = byteArrayOf(0x41, 0x53, 0x41, 0x55)
    const val VERSION = 3
    const val HEADER_SIZE = 25
    const val MAX_DATAGRAM_SIZE = 1200
    const val SAMPLE_WIDTH_BYTES = 4
}

enum class SampleFormat(val wireValue: Int) {
    FLOAT32_LE(1),
}

data class AudioPacket(
    val sequenceNumber: Long,
    val streamId: Long,
    val sampleRate: Int,
    val channels: Int,
    val sampleFormat: SampleFormat,
    val payload: ByteArray,
)

class PacketParseException(message: String) : IllegalArgumentException(message)

/** Parses only the active AudioStreamer protocol v3 wire format. */
class PacketParser {
    fun parse(datagram: ByteArray, length: Int = datagram.size): AudioPacket {
        if (length > datagram.size) throw PacketParseException("Length exceeds input buffer")
        if (length > ProtocolSpec.MAX_DATAGRAM_SIZE) {
            throw PacketParseException("Datagram exceeds ${ProtocolSpec.MAX_DATAGRAM_SIZE} bytes")
        }
        if (length < ProtocolSpec.HEADER_SIZE) {
            throw PacketParseException("Datagram is shorter than the ${ProtocolSpec.HEADER_SIZE}-byte v3 header")
        }

        for (index in ProtocolSpec.magic.indices) {
            if (datagram[index] != ProtocolSpec.magic[index]) {
                throw PacketParseException("Invalid protocol magic")
            }
        }

        val buffer = ByteBuffer.wrap(datagram, 0, length).order(ByteOrder.BIG_ENDIAN)
        buffer.position(ProtocolSpec.magic.size)
        val version = unsignedByte(buffer.get())
        if (version != ProtocolSpec.VERSION) {
            throw PacketParseException("Unsupported protocol version: $version")
        }
        val sequence = unsignedInt(buffer.int)
        val streamId = buffer.long
        val sampleRate = buffer.int
        val channels = unsignedByte(buffer.get())
        val formatValue = unsignedByte(buffer.get())
        val payloadLength = unsignedShort(buffer.short)

        if (streamId == 0L) throw PacketParseException("Stream ID zero is reserved")
        if (sampleRate <= 0) throw PacketParseException("Sample rate must be positive")
        if (channels != 2) throw PacketParseException("This receiver supports stereo audio only")
        if (formatValue != SampleFormat.FLOAT32_LE.wireValue) {
            throw PacketParseException("Unsupported sample format: $formatValue")
        }
        if (payloadLength != length - ProtocolSpec.HEADER_SIZE) {
            throw PacketParseException("Payload length does not match datagram size")
        }
        if (payloadLength == 0 || payloadLength % (channels * ProtocolSpec.SAMPLE_WIDTH_BYTES) != 0) {
            throw PacketParseException("Payload must contain complete stereo float32 frames")
        }

        return AudioPacket(
            sequenceNumber = sequence,
            streamId = streamId,
            sampleRate = sampleRate,
            channels = channels,
            sampleFormat = SampleFormat.FLOAT32_LE,
            payload = datagram.copyOfRange(ProtocolSpec.HEADER_SIZE, length),
        )
    }

    private fun unsignedByte(value: Byte): Int = value.toInt() and 0xFF
    private fun unsignedShort(value: Short): Int = value.toInt() and 0xFFFF
    private fun unsignedInt(value: Int): Long = value.toLong() and 0xFFFF_FFFFL
}
