package com.example.android.network

import java.nio.ByteBuffer
import java.nio.ByteOrder

enum class PacketType(val wireValue: Int) {
    AUDIO(1),
    CONTROL(2),
    KEEPALIVE(3),
    UNKNOWN(-1);

    companion object {
        fun fromWire(value: Int): PacketType = entries.firstOrNull { it.wireValue == value } ?: UNKNOWN
    }
}

enum class SampleFormat(val wireValue: Int) {
    UNKNOWN(0),
    FLOAT32_LE(1),
    PCM_S16_LE(2),
    PCM_S24_LE(3),
    PCM_S32_LE(4),
}

data class AudioPacket(
    val protocolVersion: Int,
    val packetType: PacketType,
    val flags: Int,
    val sequenceNumber: Long,
    val streamId: Long,
    val timestampNs: Long,
    val sampleRate: Int,
    val channels: Int,
    val sampleFormat: SampleFormat,
    val payload: ByteArray,
)

class PacketParseException(message: String) : IllegalArgumentException(message)

/** Parses the shared AudioStreamer v1 and v2 wire formats, independent of sockets. */
class PacketParser {
    fun parse(datagram: ByteArray, length: Int = datagram.size): AudioPacket {
        if (length < 5) throw PacketParseException("Datagram is too short to identify the protocol")
        if (length > MAX_DATAGRAM_SIZE) throw PacketParseException("Datagram exceeds 1200 bytes")
        if (length > datagram.size) throw PacketParseException("Length exceeds input buffer")
        if (!datagram.copyOfRange(0, 4).contentEquals(MAGIC)) {
            throw PacketParseException("Invalid protocol magic")
        }

        return when (datagram[4].toInt() and 0xFF) {
            VERSION_V1 -> parseV1(datagram, length)
            VERSION_V2 -> parseV2(datagram, length)
            else -> throw PacketParseException("Unsupported protocol version: ${datagram[4].toInt() and 0xFF}")
        }
    }

    private fun parseV1(data: ByteArray, length: Int): AudioPacket {
        if (length < V1_HEADER_SIZE) throw PacketParseException("V1 header is truncated")
        val buffer = ByteBuffer.wrap(data, 0, length).order(ByteOrder.BIG_ENDIAN)
        buffer.position(5)
        val format = unsignedByte(buffer.get())
        val flags = unsignedShort(buffer.short)
        val sequence = unsignedInt(buffer.int)
        val timestamp = buffer.long
        val sampleRate = buffer.int
        val channels = unsignedShort(buffer.short)
        val payloadLength = unsignedShort(buffer.short)

        if (format != SampleFormat.FLOAT32_LE.wireValue) {
            throw PacketParseException("V1 only supports FLOAT32_LE")
        }
        if (flags != 0) throw PacketParseException("V1 reserved field must be zero")
        if (payloadLength != length - V1_HEADER_SIZE) {
            throw PacketParseException("V1 payload length does not match datagram")
        }
        val payload = data.copyOfRange(V1_HEADER_SIZE, length)
        validateAudio(sampleRate, channels, format, payload)
        return AudioPacket(
            protocolVersion = VERSION_V1,
            packetType = PacketType.AUDIO,
            flags = 0,
            sequenceNumber = sequence,
            streamId = 0L,
            timestampNs = timestamp,
            sampleRate = sampleRate,
            channels = channels,
            sampleFormat = SampleFormat.FLOAT32_LE,
            payload = payload,
        )
    }

    private fun parseV2(data: ByteArray, length: Int): AudioPacket {
        if (length < V2_HEADER_SIZE) throw PacketParseException("V2 base header is truncated")
        val buffer = ByteBuffer.wrap(data, 0, length).order(ByteOrder.BIG_ENDIAN)
        buffer.position(5)
        val typeValue = unsignedByte(buffer.get())
        val headerLength = unsignedShort(buffer.short)
        val flags = unsignedShort(buffer.short)
        val sequence = unsignedInt(buffer.int)
        val streamId = buffer.long
        val timestamp = buffer.long
        val sampleRate = buffer.int
        val channels = unsignedShort(buffer.short)
        val formatValue = unsignedByte(buffer.get())
        val reserved = unsignedByte(buffer.get())
        val payloadLength = unsignedShort(buffer.short)

        if (headerLength < V2_HEADER_SIZE || headerLength > length) {
            throw PacketParseException("Invalid V2 header length: $headerLength")
        }
        if (streamId == 0L) throw PacketParseException("V2 stream ID zero is reserved")
        if (reserved != 0) throw PacketParseException("V2 reserved byte must be zero")
        if (payloadLength != length - headerLength) {
            throw PacketParseException("V2 payload length does not match datagram")
        }
        validateExtensions(data, V2_HEADER_SIZE, headerLength)

        val type = PacketType.fromWire(typeValue)
        val format = SampleFormat.entries.firstOrNull { it.wireValue == formatValue }
            ?: SampleFormat.UNKNOWN
        val payload = data.copyOfRange(headerLength, length)
        if (type == PacketType.AUDIO) {
            validateAudio(sampleRate, channels, formatValue, payload)
        } else if (type == PacketType.CONTROL || type == PacketType.KEEPALIVE) {
            if (sampleRate != 0 || channels != 0 || formatValue != SampleFormat.UNKNOWN.wireValue) {
                throw PacketParseException("Non-audio packet has nonzero audio format fields")
            }
            if (type == PacketType.KEEPALIVE && payload.isNotEmpty()) {
                throw PacketParseException("Keepalive payload must be empty")
            }
        }

        return AudioPacket(
            protocolVersion = VERSION_V2,
            packetType = type,
            flags = flags,
            sequenceNumber = sequence,
            streamId = streamId,
            timestampNs = timestamp,
            sampleRate = sampleRate,
            channels = channels,
            sampleFormat = format,
            payload = payload,
        )
    }

    private fun validateAudio(sampleRate: Int, channels: Int, format: Int, payload: ByteArray) {
        if (sampleRate <= 0) throw PacketParseException("Audio sample rate must be positive")
        if (channels != 2) throw PacketParseException("This receiver supports stereo audio only")
        if (format != SampleFormat.FLOAT32_LE.wireValue) {
            throw PacketParseException("This receiver supports FLOAT32_LE audio only")
        }
        if (payload.isEmpty() || payload.size % (channels * Float.SIZE_BYTES) != 0) {
            throw PacketParseException("Audio payload must contain complete stereo float32 frames")
        }
    }

    private fun validateExtensions(data: ByteArray, start: Int, end: Int) {
        var offset = start
        while (offset < end) {
            if (end - offset < 4) throw PacketParseException("Truncated extension TLV")
            val type = ((data[offset].toInt() and 0xFF) shl 8) or (data[offset + 1].toInt() and 0xFF)
            val valueLength = ((data[offset + 2].toInt() and 0xFF) shl 8) or (data[offset + 3].toInt() and 0xFF)
            if (type == 0) throw PacketParseException("Extension type zero is reserved")
            val paddedLength = (4 + valueLength + 3) and -4
            val next = offset + paddedLength
            if (next > end) throw PacketParseException("Extension TLV exceeds header length")
            for (index in offset + 4 + valueLength until next) {
                if (data[index].toInt() != 0) throw PacketParseException("Extension padding must be zero")
            }
            offset = next
        }
    }

    private fun unsignedByte(value: Byte): Int = value.toInt() and 0xFF
    private fun unsignedShort(value: Short): Int = value.toInt() and 0xFFFF
    private fun unsignedInt(value: Int): Long = value.toLong() and 0xFFFF_FFFFL

    private companion object {
        val MAGIC = byteArrayOf(0x41, 0x53, 0x41, 0x55)
        const val VERSION_V1 = 1
        const val VERSION_V2 = 2
        const val V1_HEADER_SIZE = 28
        const val V2_HEADER_SIZE = 40
        const val MAX_DATAGRAM_SIZE = 1200
    }
}
