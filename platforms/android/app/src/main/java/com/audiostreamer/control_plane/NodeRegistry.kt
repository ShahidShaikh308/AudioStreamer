package com.audiostreamer.control_plane

import java.util.concurrent.ConcurrentHashMap

data class NodeRecord(
    val nodeId: Long,
    val name: String,
    val platform: String,
    val capabilities: Int,
    val address: String,
    val receivePort: Int,
    val status: NodeStatus,
    val lastSeenMillis: Long,
)

/** Thread-safe peer snapshots. The registry owns no sockets or protocol behavior. */
class NodeRegistry {
    private val nodes = ConcurrentHashMap<Long, NodeRecord>()
    fun get(nodeId: Long): NodeRecord? = nodes[nodeId]
    fun snapshot(): List<NodeRecord> = nodes.values.sortedBy { it.nodeId }
    internal fun put(record: NodeRecord) { nodes[record.nodeId] = record }
    internal fun remove(nodeId: Long): NodeRecord? = nodes.remove(nodeId)
    internal fun expire(nowMillis: Long, timeoutMillis: Long): List<NodeRecord> {
        val expired = nodes.values.filter { nowMillis - it.lastSeenMillis >= timeoutMillis }
        expired.forEach { nodes.remove(it.nodeId, it) }
        return expired
    }
}
