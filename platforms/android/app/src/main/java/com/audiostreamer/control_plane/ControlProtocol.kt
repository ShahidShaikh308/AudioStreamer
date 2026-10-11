package com.audiostreamer.control_plane

import java.net.InetAddress
import java.nio.ByteBuffer
import java.nio.ByteOrder

/** Control Protocol v1 values shared with Windows and the ESP32 firmware. */
object NodeCapability {
    const val CAPTURE = 1
    const val PLAYBACK = 2
    const val SENDER = 4
    const val RECEIVER = 8
}

enum class NodeStatus(val wireValue: Int) { OFFLINE(0), ONLINE(1), BUSY(2), STREAMING(3), ERROR(4) }
enum class StreamState(val wireValue: Int) { RUNNING(1), STOPPED(2), FAILED(3) }

sealed interface ControlPacket
data class HelloPacket(val nodeId: Long, val name: String, val platform: String, val capabilities: Int, val receivePort: Int) : ControlPacket
data class GoodbyePacket(val nodeId: Long) : ControlPacket
data class HeartbeatPacket(val nodeId: Long, val status: NodeStatus) : ControlPacket
data class StartStreamPacket(val senderNodeId: Long, val receiverNodeId: Long, val transactionId: Int? = null) : ControlPacket
data class StopStreamPacket(val senderNodeId: Long, val receiverNodeId: Long, val transactionId: Int? = null) : ControlPacket
data class StreamStatusPacket(val senderNodeId: Long, val receiverNodeId: Long, val state: StreamState, val errorCode: Int = 0, val transactionId: Int? = null) : ControlPacket
data class ErrorPacket(val nodeId: Long, val errorCode: Int, val message: String, val transactionId: Int? = null) : ControlPacket

/** Encodes and parses v1 datagrams. All multi-byte fields use network byte order. */
object ControlPacketCodec {
    const val MAX_DATAGRAM_SIZE = 1200
    private const val HEADER_SIZE = 6
    private const val VERSION = 1
    private const val MAGIC = 0x4143

    fun encode(packet: ControlPacket): ByteArray {
        val type: Int
        val body = when (packet) {
            is HelloPacket -> {
                type = 1
                require(packet.nodeId != 0L && packet.receivePort in 1..65535)
                require(packet.capabilities and 0xFFF0 == 0 && packet.capabilities >= 0)
                val name = utf8(packet.name)
                val platform = utf8(packet.platform)
                ByteBuffer.allocate(8 + 1 + name.size + 1 + platform.size + 4).order(ByteOrder.BIG_ENDIAN)
                    .putLong(packet.nodeId).put(name.size.toByte()).put(name).put(platform.size.toByte()).put(platform)
                    .putShort(packet.capabilities.toShort()).putShort(packet.receivePort.toShort()).array()
            }
            is GoodbyePacket -> { type = 2; require(packet.nodeId != 0L); ByteBuffer.allocate(8).putLong(packet.nodeId).array() }
            is HeartbeatPacket -> { type = 3; require(packet.nodeId != 0L && packet.status != NodeStatus.OFFLINE); ByteBuffer.allocate(9).putLong(packet.nodeId).put(packet.status.wireValue.toByte()).array() }
            is StartStreamPacket -> { type = 4; streamPair(packet.senderNodeId, packet.receiverNodeId, packet.transactionId) }
            is StopStreamPacket -> { type = 5; streamPair(packet.senderNodeId, packet.receiverNodeId, packet.transactionId) }
            is StreamStatusPacket -> {
                type = 6
                require(packet.senderNodeId != 0L && packet.receiverNodeId != 0L && packet.senderNodeId != packet.receiverNodeId)
                require(packet.errorCode in 0..65535)
                val tx = packet.transactionId
                ByteBuffer.allocate(19 + if (tx == null) 0 else 2).putLong(packet.senderNodeId).putLong(packet.receiverNodeId)
                    .put(packet.state.wireValue.toByte()).putShort(packet.errorCode.toShort()).apply { if (tx != null) { require(tx in 0..65535); putShort(tx.toShort()) } }.array()
            }
            is ErrorPacket -> {
                type = 7
                require(packet.nodeId != 0L && packet.errorCode in 0..65535)
                val message = utf8(packet.message, allowEmpty = true)
                ByteBuffer.allocate(11 + message.size + if (packet.transactionId == null) 0 else 2).putLong(packet.nodeId)
                    .putShort(packet.errorCode.toShort()).put(message.size.toByte()).put(message)
                    .apply { packet.transactionId?.let { require(it in 0..65535); putShort(it.toShort()) } }.array()
            }
        }
        require(body.size + HEADER_SIZE <= MAX_DATAGRAM_SIZE)
        return ByteBuffer.allocate(HEADER_SIZE + body.size).order(ByteOrder.BIG_ENDIAN)
            .putShort(MAGIC.toShort()).put(VERSION.toByte()).put(type.toByte()).putShort(body.size.toShort()).put(body).array()
    }

    fun decode(data: ByteArray, address: InetAddress): DecodedControlPacket {
        require(data.size in HEADER_SIZE..MAX_DATAGRAM_SIZE) { "invalid datagram size" }
        val input = ByteBuffer.wrap(data).order(ByteOrder.BIG_ENDIAN)
        require((input.short.toInt() and 0xFFFF) == MAGIC && (input.get().toInt() and 0xFF) == VERSION) { "invalid control header" }
        val type = input.get().toInt() and 0xFF
        val length = input.short.toInt() and 0xFFFF
        require(length == input.remaining()) { "payload length mismatch" }
        val body = ByteBuffer.wrap(data, HEADER_SIZE, length).slice().order(ByteOrder.BIG_ENDIAN)
        fun id(requireNonzero: Boolean = true): Long {
            require(body.remaining() >= 8)
            return body.long.also { if (requireNonzero) require(it != 0L) }
        }
        fun text(): String { require(body.hasRemaining()); val size = body.get().toInt() and 0xFF; require(size <= body.remaining()); val bytes = ByteArray(size); body.get(bytes); return bytes.toString(Charsets.UTF_8).also { require(it.toByteArray(Charsets.UTF_8).contentEquals(bytes) && '\u0000' !in it) } }
        fun transaction(knownSize: Int): Int? { val remaining = body.remaining() - knownSize; require(remaining >= 0 && remaining != 1); return if (remaining >= 2) body.getShort().toInt() and 0xFFFF else null }
        val packet = when (type) {
            1 -> {
                val nodeId = id(); val name = text(); val platform = text(); require(body.remaining() >= 4)
                val caps = body.short.toInt() and 0xFFFF; val port = body.short.toInt() and 0xFFFF
                require(name.isNotEmpty() && platform.isNotEmpty() && caps and 0xFFF0 == 0 && port != 0)
                HelloPacket(nodeId, name, platform, caps, port)
            }
            2 -> GoodbyePacket(id())
            3 -> { val nodeId = id(); require(body.hasRemaining()); val status = NodeStatus.entries.firstOrNull { it.wireValue == (body.get().toInt() and 0xFF) } ?: error("bad status"); require(status != NodeStatus.OFFLINE); HeartbeatPacket(nodeId, status) }
            4, 5 -> { val sender = id(); val receiver = id(); require(sender != receiver); val tx = transaction(0); if (type == 4) StartStreamPacket(sender, receiver, tx) else StopStreamPacket(sender, receiver, tx) }
            6 -> { val sender = id(false); val receiver = id(false); require(body.remaining() >= 3); val stateValue = body.get().toInt() and 0xFF; val state = StreamState.entries.firstOrNull { it.wireValue == stateValue } ?: error("bad stream state"); val code = body.short.toInt() and 0xFFFF; StreamStatusPacket(sender, receiver, state, code, transaction(0)) }
            7 -> { val nodeId = id(); require(body.remaining() >= 3); val code = body.short.toInt() and 0xFFFF; val message = text(); val tx = transaction(0); ErrorPacket(nodeId, code, message, tx) }
            else -> error("unknown control message type")
        }
        return DecodedControlPacket(packet, address)
    }

    private fun streamPair(sender: Long, receiver: Long, transactionId: Int?): ByteArray {
        require(sender != 0L && receiver != 0L && sender != receiver)
        return ByteBuffer.allocate(16 + if (transactionId == null) 0 else 2).putLong(sender).putLong(receiver)
            .apply { transactionId?.let { require(it in 0..65535); putShort(it.toShort()) } }.array()
    }
    private fun utf8(value: String, allowEmpty: Boolean = false): ByteArray = value.toByteArray(Charsets.UTF_8).also {
        require((allowEmpty || it.isNotEmpty()) && it.size <= 255 && '\u0000' !in value) { "text must fit the v1 UTF-8 field" }
    }
}

data class DecodedControlPacket(val packet: ControlPacket, val address: InetAddress)
