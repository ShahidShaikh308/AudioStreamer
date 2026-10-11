package com.audiostreamer.core.node

import com.audiostreamer.audio_plane.playback.PlaybackDiagnostics
import com.audiostreamer.audio_plane.transport.NetworkDiagnostics
import com.audiostreamer.audio_plane.transport.SenderDiagnostics
import com.audiostreamer.control_plane.NodeRecord
import kotlinx.coroutines.flow.StateFlow

enum class LaneState { STOPPED, STARTING, RUNNING, STOPPING, ERROR }
enum class NodeStatus { STOPPED, STARTING, RUNNING, STOPPING, ERROR }

data class NodeCapabilities(
    val canCapture: Boolean,
    val canSend: Boolean,
    val canReceive: Boolean,
    val canPlay: Boolean,
)

data class NodeSettings(
    val nodeName: String,
    val destinationIp: String,
    val destinationPort: Int,
    val receivePort: Int,
    val logLevel: String,
    val nodeId: Long = 0L,
    val controlPort: Int = 55050,
)

data class NodeSnapshot(
    val status: NodeStatus = NodeStatus.STOPPED,
    val sender: LaneState = LaneState.STOPPED,
    val receiver: LaneState = LaneState.STOPPED,
    val nodeName: String = "AudioStreamer",
    val localIp: String = "Unknown",
    val senderDiagnostics: SenderDiagnostics = SenderDiagnostics(),
    val receiverDiagnostics: NetworkDiagnostics = NetworkDiagnostics(),
    val playbackDiagnostics: PlaybackDiagnostics = PlaybackDiagnostics(),
    val captureStatus: String = "Stopped",
    val playbackStatus: String = "Stopped",
    val sampleRate: Int = 0,
    val error: String? = null,
    val knownNodes: List<NodeRecord> = emptyList(),
    val controlStatus: String = "Starting discovery",
)

interface INode {
    val capabilities: NodeCapabilities
    val settings: NodeSettings
    val state: StateFlow<NodeSnapshot>

    fun updateSettings(settings: NodeSettings)
    fun saveConfiguration()
    fun startSender()
    fun stopSender()
    fun startReceiver()
    fun stopReceiver()
    fun startStream(senderNodeId: Long, receiverNodeId: Long)
    fun stopStream(senderNodeId: Long, receiverNodeId: Long)
    fun shutdown()
}

internal fun aggregateStatus(sender: LaneState, receiver: LaneState): NodeStatus = when {
    sender == LaneState.ERROR || receiver == LaneState.ERROR -> NodeStatus.ERROR
    sender == LaneState.RUNNING || receiver == LaneState.RUNNING -> NodeStatus.RUNNING
    sender == LaneState.STARTING || receiver == LaneState.STARTING -> NodeStatus.STARTING
    sender == LaneState.STOPPING || receiver == LaneState.STOPPING -> NodeStatus.STOPPING
    else -> NodeStatus.STOPPED
}
