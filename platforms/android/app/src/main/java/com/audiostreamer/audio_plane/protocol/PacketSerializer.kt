package com.audiostreamer.audio_plane.protocol

/** Writes protocol v3 datagrams as specified by the repository-root PROTOCOL.md. */
class PacketSerializer {
    /** Write one frame-aligned payload slice into a reusable datagram buffer. */
    fun write(
        target: ByteArray,
        sequenceNumber: Long,
        streamId: Long,
        sampleRate: Int,
        channels: Int,
        payload: ByteArray,
        payloadOffset: Int,
        payloadLength: Int,
    ): Int {
        require(target.size >= ProtocolSpec.MAX_DATAGRAM_SIZE)
        require(sequenceNumber in 0..UINT32_MASK)
        require(streamId != 0L)
        require(sampleRate > 0)
        require(channels == 2)
        require(payloadOffset >= 0 && payloadLength > 0)
        require(payloadOffset + payloadLength <= payload.size)
        require(payloadLength <= MAX_PAYLOAD_SIZE)
        require(payloadLength % (channels * ProtocolSpec.SAMPLE_WIDTH_BYTES) == 0)

        var offset = 0
        ProtocolSpec.magic.copyInto(target, offset)
        offset += ProtocolSpec.magic.size
        target[offset++] = ProtocolSpec.VERSION.toByte()
        offset = writeInt(target, offset, sequenceNumber.toInt())
        offset = writeLong(target, offset, streamId)
        offset = writeInt(target, offset, sampleRate)
        target[offset++] = channels.toByte()
        target[offset++] = SampleFormat.FLOAT32_LE.wireValue.toByte()
        target[offset++] = (payloadLength ushr 8).toByte()
        target[offset++] = payloadLength.toByte()
        payload.copyInto(target, offset, payloadOffset, payloadOffset + payloadLength)
        return ProtocolSpec.HEADER_SIZE + payloadLength
    }

    private fun writeInt(target: ByteArray, offset: Int, value: Int): Int {
        target[offset] = (value ushr 24).toByte()
        target[offset + 1] = (value ushr 16).toByte()
        target[offset + 2] = (value ushr 8).toByte()
        target[offset + 3] = value.toByte()
        return offset + Int.SIZE_BYTES
    }

    private fun writeLong(target: ByteArray, offset: Int, value: Long): Int {
        for (byteIndex in 0 until Long.SIZE_BYTES) {
            val shift = (Long.SIZE_BYTES - 1 - byteIndex) * 8
            target[offset + byteIndex] = (value ushr shift).toByte()
        }
        return offset + Long.SIZE_BYTES
    }

    private companion object {
        const val MAX_PAYLOAD_SIZE = ProtocolSpec.MAX_DATAGRAM_SIZE - ProtocolSpec.HEADER_SIZE
        const val UINT32_MASK = 0xFFFF_FFFFL
    }
}
