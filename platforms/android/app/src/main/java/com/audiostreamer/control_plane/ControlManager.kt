package com.audiostreamer.control_plane

import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.cancel
import kotlinx.coroutines.channels.Channel
import kotlinx.coroutines.isActive
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext
import java.net.DatagramPacket
import java.net.DatagramSocket
import java.net.InetAddress
import java.net.InetSocketAddress
import java.net.SocketTimeoutException
import java.util.LinkedHashMap
import java.util.concurrent.atomic.AtomicBoolean

sealed interface ControlEvent {
    data class NodeDiscovered(val node: NodeRecord) : ControlEvent
    data class NodeUpdated(val node: NodeRecord) : ControlEvent
    data class NodeLost(val node: NodeRecord) : ControlEvent
    data class Error(val message: String) : ControlEvent
    data class Stream(val packet: ControlPacket) : ControlEvent
}

/**
 * Android Control Plane client. One IO coroutine owns its UDP socket and performs every
 * receive/send operation. The UI only receives events and queues command requests here.
 */
class ControlManager(
    private val nodeId: Long,
    private val nodeName: () -> String,
    private val platform: String,
    private val capabilities: () -> Int,
    private val receivePort: () -> Int,
    private val controlPort: () -> Int,
    private val controllerSecretHex: () -> String,
    private val statusProvider: () -> NodeStatus,
    private val commandHandler: (ControlPacket) -> Unit,
    private val onEvent: (ControlEvent) -> Unit,
) {
    val registry = NodeRegistry()
    private val scope = CoroutineScope(SupervisorJob() + Dispatchers.IO)
    private val outbound = Channel<Outbound>(capacity = 128)
    private val started = AtomicBoolean(false)
    private val closed = AtomicBoolean(false)
    private val replayGate = Any()
    private val controllerNonces = LinkedHashMap<String, Long>()
    private data class Outbound(val message: ControlPacket, val host: String, val port: Int)

    fun start() {
        check(!closed.get()) { "Control Manager is closed" }
        if (!started.compareAndSet(false, true)) return
        scope.launch { runSocketOwner() }
    }

    fun updateLocal() {
        queue(HelloPacket(nodeId, nodeName(), platform, capabilities(), receivePort()), broadcastAddress(), controlPort())
    }

    fun sendStartStream(senderId: Long, receiverId: Long) = sendPair(StartStreamPacket(senderId, receiverId), senderId, receiverId)
    fun sendStopStream(senderId: Long, receiverId: Long) = sendPair(StopStreamPacket(senderId, receiverId), senderId, receiverId)

    fun reportStreamStatus(senderId: Long, receiverId: Long, state: StreamState, errorCode: Int = 0, transactionId: Int? = null) {
        val packet = StreamStatusPacket(senderId, receiverId, state, errorCode, transactionId)
        linkedSetOf(senderId, receiverId).filter { it != nodeId }.forEach { id ->
            registry.get(id)?.let { queue(packet, it.address, controlPort()) }
        }
    }

    fun reportError(nodeId: Long, errorCode: Int, message: String, transactionId: Int? = null, senderId: Long? = null, receiverId: Long? = null) {
        val packet = ErrorPacket(nodeId, errorCode, message, transactionId)
        if (senderId == null || receiverId == null) queue(packet, broadcastAddress(), controlPort())
        else linkedSetOf(senderId, receiverId).filter { it != this.nodeId }.forEach { id ->
            registry.get(id)?.let { queue(packet, it.address, controlPort()) }
        }
    }

    /** Nonblocking. Shutdown signaling and socket cleanup stay on the IO worker. */
    fun close() {
        if (!closed.compareAndSet(false, true)) return
        if (started.get()) {
            outbound.trySend(Outbound(GoodbyePacket(nodeId), broadcastAddress(), controlPort()))
            outbound.close()
        } else {
            scope.cancel()
        }
    }

    private suspend fun runSocketOwner() = withContext(Dispatchers.IO) {
        try {
            DatagramSocket(null).use { socket ->
                socket.reuseAddress = false
                socket.bind(InetSocketAddress(controlPort()))
                socket.broadcast = true
                socket.soTimeout = 100
                updateLocal()
                var nextHeartbeat = android.os.SystemClock.elapsedRealtime() + HEARTBEAT_MS
                val receiveBuffer = ByteArray(ControlPacketCodec.MAX_DATAGRAM_SIZE + 1)
                while (scope.isActive) {
                    var queueClosed = false
                    while (true) {
                        val result = outbound.tryReceive()
                        val pending = result.getOrNull()
                        if (pending == null) {
                            queueClosed = result.isClosed
                            break
                        }
                        transmit(socket, pending)
                    }
                    if (queueClosed) break
                    val now = android.os.SystemClock.elapsedRealtime()
                    if (now >= nextHeartbeat && !closed.get()) {
                        transmit(socket, Outbound(HelloPacket(nodeId, nodeName(), platform, capabilities(), receivePort()), broadcastAddress(), controlPort()))
                        transmit(socket, Outbound(HeartbeatPacket(nodeId, statusProvider()), broadcastAddress(), controlPort()))
                        nextHeartbeat = now + HEARTBEAT_MS
                        registry.expire(now, PEER_TIMEOUT_MS).forEach { onEvent(ControlEvent.NodeLost(it)) }
                    }
                    try {
                        val packet = DatagramPacket(receiveBuffer, receiveBuffer.size)
                        socket.receive(packet)
                        if (packet.length <= ControlPacketCodec.MAX_DATAGRAM_SIZE && packet.port == controlPort()) {
                            handle(ControlPacketCodec.decode(packet.data.copyOf(packet.length), packet.address,
                                controllerSecretHex().takeIf { it.isNotBlank() }))
                        }
                    } catch (_: SocketTimeoutException) {
                        // Timed receive keeps heartbeat and cancellation latency bounded.
                    } catch (error: Exception) {
                        onEvent(ControlEvent.Error("Invalid Control Plane packet: ${error.message}"))
                    }
                }
            }
        } catch (cancelled: CancellationException) {
            throw cancelled
        } catch (error: Exception) {
            onEvent(ControlEvent.Error(error.message ?: "Control Plane socket failed"))
        } finally {
            scope.cancel()
        }
    }

    private fun handle(decoded: DecodedControlPacket) {
        when (val packet = decoded.packet) {
            is HelloPacket -> if (packet.nodeId != nodeId) {
                val address = decoded.address.hostAddress ?: return
                val record = NodeRecord(packet.nodeId, packet.name, packet.platform, packet.capabilities,
                    address, packet.receivePort, NodeStatus.ONLINE, android.os.SystemClock.elapsedRealtime())
                val wasNew = registry.get(packet.nodeId) == null
                registry.put(record)
                onEvent(if (wasNew) ControlEvent.NodeDiscovered(record) else ControlEvent.NodeUpdated(record))
                if (wasNew) {
                    queue(HelloPacket(nodeId, nodeName(), platform, capabilities(), receivePort()), address, controlPort())
                }
            }
            is HeartbeatPacket -> if (packet.nodeId != nodeId) {
                registry.get(packet.nodeId)?.let {
                    val updated = it.copy(address = decoded.address.hostAddress ?: it.address,
                        status = packet.status, lastSeenMillis = android.os.SystemClock.elapsedRealtime())
                    registry.put(updated)
                    onEvent(ControlEvent.NodeUpdated(updated))
                }
            }
            is GoodbyePacket -> if (packet.nodeId != nodeId) registry.remove(packet.nodeId)?.let { onEvent(ControlEvent.NodeLost(it)) }
            is StartStreamPacket, is StopStreamPacket, is AuthenticatedControllerCommandPacket -> {
                val authenticated = packet is AuthenticatedControllerCommandPacket
                val pair = when (packet) {
                    is StartStreamPacket -> packet.senderNodeId to packet.receiverNodeId
                    is StopStreamPacket -> packet.senderNodeId to packet.receiverNodeId
                    is AuthenticatedControllerCommandPacket -> packet.senderNodeId to packet.receiverNodeId
                    else -> error("unreachable")
                }
                if (authenticated) {
                    val controller = packet as AuthenticatedControllerCommandPacket
                    val nonceKey = "${controller.controllerNodeId.toULong()}:${controller.nonce.joinToString("") { "%02x".format(it) }}"
                    if (!acceptControllerNonce(nonceKey)) {
                        onEvent(ControlEvent.Error("Rejected replayed controller command"))
                        return
                    }
                    if (nodeId == pair.first || nodeId == pair.second) {
                        commandHandler(if (controller.isStart) StartStreamPacket(pair.first, pair.second, controller.transactionId)
                            else StopStreamPacket(pair.first, pair.second, controller.transactionId))
                    }
                } else if (nodeId == pair.first || nodeId == pair.second) {
                    val peerId = if (nodeId == pair.first) pair.second else pair.first
                    if (registry.get(peerId)?.address == decoded.address.hostAddress) commandHandler(packet)
                }
            }
            is StreamStatusPacket, is ErrorPacket -> onEvent(ControlEvent.Stream(packet))
        }
    }

    private fun sendPair(packet: ControlPacket, senderId: Long, receiverId: Long) {
        linkedSetOf(senderId, receiverId).filter { it != nodeId }.forEach { id ->
            val peer = registry.get(id) ?: throw IllegalStateException("Control Plane node $id is not online")
            queue(packet, peer.address, controlPort())
        }
        if (nodeId == senderId || nodeId == receiverId) commandHandler(packet)
    }

    private fun queue(packet: ControlPacket, host: String, port: Int) {
        require(port in 1..65535)
        val result = outbound.trySend(Outbound(packet, host, port))
        if (result.isFailure && !closed.get()) onEvent(ControlEvent.Error("Control Plane send queue is unavailable"))
    }

    private fun transmit(socket: DatagramSocket, outbound: Outbound) {
        val bytes = ControlPacketCodec.encode(outbound.message)
        socket.send(DatagramPacket(bytes, bytes.size, InetAddress.getByName(outbound.host), outbound.port))
    }

    private fun acceptControllerNonce(key: String): Boolean = synchronized(replayGate) {
        val now = android.os.SystemClock.elapsedRealtime()
        val iterator = controllerNonces.entries.iterator()
        while (iterator.hasNext()) if (now - iterator.next().value >= CONTROLLER_REPLAY_RETENTION_MS) iterator.remove()
        if (controllerNonces.containsKey(key)) return@synchronized false
        while (controllerNonces.size >= MAX_CONTROLLER_NONCES) {
            val oldest = controllerNonces.entries.iterator()
            if (!oldest.hasNext()) break
            oldest.next()
            oldest.remove()
        }
        controllerNonces[key] = now
        true
    }

    private fun broadcastAddress(): String = InetAddress.getByAddress(byteArrayOf(-1, -1, -1, -1)).hostAddress!!

    private companion object {
        const val HEARTBEAT_MS = 3000L
        const val PEER_TIMEOUT_MS = 10000L
        const val CONTROLLER_REPLAY_RETENTION_MS = 10 * 60 * 1000L
        const val MAX_CONTROLLER_NONCES = 1024
    }
}
