package com.audiostreamer.control_plane

import java.net.InetAddress
import java.nio.ByteBuffer
import java.nio.ByteOrder
import javax.crypto.Mac
import javax.crypto.spec.SecretKeySpec
import java.security.MessageDigest

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
data class AuthenticatedControllerCommandPacket(val isStart: Boolean, val controllerNodeId: Long,
    val senderNodeId: Long, val receiverNodeId: Long, val transactionId: Int, val nonce: ByteArray) : ControlPacket
data class StreamStatusPacket(val senderNodeId: Long, val receiverNodeId: Long, val state: StreamState, val errorCode: Int = 0, val transactionId: Int? = null) : ControlPacket
data class ErrorPacket(val nodeId: Long, val errorCode: Int, val message: String, val transactionId: Int? = null) : ControlPacket

/** Encodes and parses v1 datagrams. All multi-byte fields use network byte order. */
object ControlPacketCodec {
    const val MAX_DATAGRAM_SIZE = 1200
    private const val HEADER_SIZE = 6
    private const val VERSION = 1
    private const val MAGIC = 0x4143
    private const val CONTROLLER_VERSION = 2
    private const val CONTROLLER_SECRET_BYTES = 32
    private const val CONTROLLER_NONCE_BYTES = 16
    private const val CONTROLLER_TAG_BYTES = 32

    fun encodeAuthenticatedControllerCommand(packet: AuthenticatedControllerCommandPacket,
        secretHex: String): ByteArray {
        val secret = parseControllerSecret(secretHex)
        require(packet.controllerNodeId != 0L && packet.senderNodeId != 0L && packet.receiverNodeId != 0L &&
            packet.senderNodeId != packet.receiverNodeId && packet.transactionId in 0..65535 &&
            packet.nonce.size == CONTROLLER_NONCE_BYTES && packet.nonce.any { it.toInt() != 0 })
        val payloadLength = 8 + 8 + 8 + 2 + CONTROLLER_NONCE_BYTES + CONTROLLER_TAG_BYTES
        val bytes = ByteBuffer.allocate(HEADER_SIZE + payloadLength).order(ByteOrder.BIG_ENDIAN)
            .putShort(MAGIC.toShort()).put(CONTROLLER_VERSION.toByte())
            .put(if (packet.isStart) 4.toByte() else 5.toByte()).putShort(payloadLength.toShort())
            .putLong(packet.controllerNodeId).putLong(packet.senderNodeId).putLong(packet.receiverNodeId)
            .putShort(packet.transactionId.toShort()).put(packet.nonce).array()
        val tag = controllerMac(secret, bytes.copyOfRange(0, bytes.size - CONTROLLER_TAG_BYTES))
        tag.copyInto(bytes, bytes.size - CONTROLLER_TAG_BYTES)
        return bytes
    }

    fun parseControllerSecret(secretHex: String): ByteArray {
        require(secretHex.length == CONTROLLER_SECRET_BYTES * 2 && secretHex.all { it.digitToIntOrNull(16) != null }) {
            "Controller secret must be exactly 64 hexadecimal characters"
        }
        return ByteArray(CONTROLLER_SECRET_BYTES) { index -> secretHex.substring(index * 2, index * 2 + 2).toInt(16).toByte() }
    }

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
            is AuthenticatedControllerCommandPacket -> error("Use encodeAuthenticatedControllerCommand for controller commands")
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
        require((input.short.toInt() and 0xFFFF) == MAGIC) { "invalid control header" }
        val version = input.get().toInt() and 0xFF
        val type = input.get().toInt() and 0xFF
        val length = input.short.toInt() and 0xFFFF
        require(length == input.remaining()) { "payload length mismatch" }
        if (version == CONTROLLER_VERSION) {
            require(type == 4 || type == 5) { "version 2 only supports authenticated controller commands" }
            require(data.size == HEADER_SIZE + 8 + 8 + 8 + 2 + CONTROLLER_NONCE_BYTES + CONTROLLER_TAG_BYTES) { "invalid controller command size" }
            throw IllegalArgumentException("controller secret is required to decode version 2")
        }
        require(version == VERSION) { "invalid control header" }
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

    fun decode(data: ByteArray, address: InetAddress, controllerSecretHex: String?): DecodedControlPacket {
        if (data.size >= 3 && (data[2].toInt() and 0xFF) == CONTROLLER_VERSION) {
            val secret = parseControllerSecret(controllerSecretHex ?: throw IllegalArgumentException("controller secret is not configured"))
            require(data.size == HEADER_SIZE + 8 + 8 + 8 + 2 + CONTROLLER_NONCE_BYTES + CONTROLLER_TAG_BYTES) { "invalid controller command size" }
            val input = ByteBuffer.wrap(data).order(ByteOrder.BIG_ENDIAN)
            require((input.short.toInt() and 0xFFFF) == MAGIC && (input.get().toInt() and 0xFF) == CONTROLLER_VERSION)
            val type = input.get().toInt() and 0xFF
            require(type == 4 || type == 5)
            val length = input.short.toInt() and 0xFFFF
            require(length == input.remaining()) { "payload length mismatch" }
            val tagOffset = data.size - CONTROLLER_TAG_BYTES
            val expected = controllerMac(secret, data.copyOfRange(0, tagOffset))
            require(MessageDigest.isEqual(expected, data.copyOfRange(tagOffset, data.size))) { "controller command authentication failed" }
            val body = ByteBuffer.wrap(data, HEADER_SIZE, tagOffset - HEADER_SIZE).slice().order(ByteOrder.BIG_ENDIAN)
            val controllerId = body.long
            val senderId = body.long
            val receiverId = body.long
            val transactionId = body.short.toInt() and 0xFFFF
            val nonce = ByteArray(CONTROLLER_NONCE_BYTES).also(body::get)
            require(controllerId != 0L && senderId != 0L && receiverId != 0L && senderId != receiverId && nonce.any { it.toInt() != 0 })
            return DecodedControlPacket(AuthenticatedControllerCommandPacket(type == 4, controllerId,
                senderId, receiverId, transactionId, nonce), address)
        }
        return decode(data, address)
    }

    private fun streamPair(sender: Long, receiver: Long, transactionId: Int?): ByteArray {
        require(sender != 0L && receiver != 0L && sender != receiver)
        return ByteBuffer.allocate(16 + if (transactionId == null) 0 else 2).putLong(sender).putLong(receiver)
            .apply { transactionId?.let { require(it in 0..65535); putShort(it.toShort()) } }.array()
    }
    private fun utf8(value: String, allowEmpty: Boolean = false): ByteArray = value.toByteArray(Charsets.UTF_8).also {
        require((allowEmpty || it.isNotEmpty()) && it.size <= 255 && '\u0000' !in value) { "text must fit the v1 UTF-8 field" }
    }

    private fun controllerMac(secret: ByteArray, bytes: ByteArray): ByteArray = Mac.getInstance("HmacSHA256")
        .apply { init(SecretKeySpec(secret, "HmacSHA256")) }.doFinal(bytes)
}

data class DecodedControlPacket(val packet: ControlPacket, val address: InetAddress)
