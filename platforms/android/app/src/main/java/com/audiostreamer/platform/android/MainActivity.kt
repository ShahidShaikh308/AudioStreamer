package com.audiostreamer.platform.android

import android.os.Bundle
import androidx.activity.ComponentActivity
import androidx.activity.compose.setContent
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.verticalScroll
import androidx.compose.material3.Button
import androidx.compose.material3.Card
import androidx.compose.material3.CardDefaults
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.Scaffold
import androidx.compose.material3.Surface
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.collectAsState
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Modifier
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.unit.dp
import com.audiostreamer.core.node.LaneState
import com.audiostreamer.core.node.NodeSettings
import com.audiostreamer.core.node.NodeSnapshot
import com.audiostreamer.platform.android.node.AndroidNode
import com.audiostreamer.platform.android.ui.theme.AudioStreamerTheme

/** Activity is a view/controller for AndroidNode; it owns no audio or networking resources. */
class MainActivity : ComponentActivity() {
    private lateinit var node: AndroidNode

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        node = AndroidNode(this)
        setContent {
            AudioStreamerTheme {
                val state by node.state.collectAsState()
                NodeScreen(
                    state = state,
                    settings = node.settings,
                    canCapture = node.capabilities.canCapture,
                    onSaveSettings = { settings ->
                        node.updateSettings(settings)
                        node.saveConfiguration()
                    },
                    onStartSender = {
                        node.updateSettings(it)
                        node.startSender()
                    },
                    onStopSender = node::stopSender,
                    onStartReceiver = {
                        node.updateSettings(it)
                        node.startReceiver()
                    },
                    onStopReceiver = node::stopReceiver,
                )
            }
        }
    }

    override fun onDestroy() {
        if (::node.isInitialized) node.shutdown()
        super.onDestroy()
    }
}

@Composable
private fun NodeScreen(
    state: NodeSnapshot,
    settings: NodeSettings,
    canCapture: Boolean,
    onSaveSettings: (NodeSettings) -> Unit,
    onStartSender: (NodeSettings) -> Unit,
    onStopSender: () -> Unit,
    onStartReceiver: (NodeSettings) -> Unit,
    onStopReceiver: () -> Unit,
) {
    var nodeName by remember(settings.nodeName) { mutableStateOf(settings.nodeName) }
    var destinationIp by remember(settings.destinationIp) { mutableStateOf(settings.destinationIp) }
    var destinationPort by remember(settings.destinationPort) { mutableStateOf(settings.destinationPort.toString()) }
    var receivePort by remember(settings.receivePort) { mutableStateOf(settings.receivePort.toString()) }
    var logLevel by remember(settings.logLevel) { mutableStateOf(settings.logLevel) }
    var formError by remember { mutableStateOf<String?>(null) }

    fun currentSettings(): NodeSettings {
        val updated = NodeSettings(
            nodeName = nodeName.trim(),
            destinationIp = destinationIp.trim(),
            destinationPort = destinationPort.toIntOrNull() ?: 0,
            receivePort = receivePort.toIntOrNull() ?: 0,
            logLevel = logLevel.trim().uppercase(),
        )
        require(updated.destinationPort in 1..65535) { "Destination port must be 1–65535" }
        require(updated.receivePort in 1..65535) { "Receive port must be 1–65535" }
        return updated
    }

    Scaffold(containerColor = MaterialTheme.colorScheme.surface) { insets ->
        Column(
            modifier = Modifier
                .fillMaxSize()
                .verticalScroll(rememberScrollState())
                .padding(insets)
                .padding(horizontal = 20.dp, vertical = 18.dp),
            verticalArrangement = Arrangement.spacedBy(14.dp),
        ) {
            Column {
                Text("AudioStreamer Node", style = MaterialTheme.typography.headlineMedium, fontWeight = FontWeight.SemiBold)
                Text("${state.nodeName} · ${state.localIp}", style = MaterialTheme.typography.bodyMedium)
            }

            Card(shape = RoundedCornerShape(18.dp), colors = CardDefaults.cardColors(
                containerColor = MaterialTheme.colorScheme.surfaceContainerLow,
            )) {
                Column(Modifier.fillMaxWidth().padding(16.dp), verticalArrangement = Arrangement.spacedBy(10.dp)) {
                    Text("Node configuration", style = MaterialTheme.typography.titleMedium)
                    OutlinedTextField(nodeName, { nodeName = it }, label = { Text("Node name") }, singleLine = true, modifier = Modifier.fillMaxWidth())
                    OutlinedTextField(destinationIp, { destinationIp = it }, label = { Text("Destination IPv4") }, singleLine = true, modifier = Modifier.fillMaxWidth())
                    Row(horizontalArrangement = Arrangement.spacedBy(10.dp)) {
                        OutlinedTextField(destinationPort, { destinationPort = it }, label = { Text("Destination port") }, singleLine = true, modifier = Modifier.weight(1f))
                        OutlinedTextField(receivePort, { receivePort = it }, label = { Text("Receive port") }, singleLine = true, modifier = Modifier.weight(1f))
                    }
                    OutlinedTextField(logLevel, { logLevel = it }, label = { Text("Log level") }, singleLine = true, modifier = Modifier.fillMaxWidth())
                    formError?.let { Text(it, color = MaterialTheme.colorScheme.error) }
                    Button(onClick = {
                        runCatching { currentSettings().also(onSaveSettings) }
                            .onSuccess { formError = null }
                            .onFailure { formError = it.message }
                    }, modifier = Modifier.fillMaxWidth()) { Text("Save configuration") }
                }
            }

            Card(shape = RoundedCornerShape(18.dp), colors = CardDefaults.cardColors(
                containerColor = MaterialTheme.colorScheme.surfaceContainerLow,
            )) {
                Column(Modifier.fillMaxWidth().padding(16.dp), verticalArrangement = Arrangement.spacedBy(8.dp)) {
                    Text("Audio sender", style = MaterialTheme.typography.titleMedium)
                    Metric("State", state.sender.name)
                    Metric("Capture", state.captureStatus)
                    Metric("Packets sent", state.senderDiagnostics.packetsSent.toString())
                    Metric("Bytes sent", state.senderDiagnostics.bytesSent.toString())
                    Row(horizontalArrangement = Arrangement.spacedBy(10.dp)) {
                        Button(
                            onClick = { runCatching { onStartSender(currentSettings()); formError = null }.onFailure { formError = it.message } },
                            enabled = canCapture && state.sender in setOf(LaneState.STOPPED, LaneState.ERROR),
                            modifier = Modifier.weight(1f),
                        ) { Text("Start sender") }
                        Button(onClick = onStopSender, enabled = state.sender in setOf(LaneState.STARTING, LaneState.RUNNING, LaneState.ERROR), modifier = Modifier.weight(1f)) {
                            Text("Stop sender")
                        }
                    }
                }
            }

            Card(shape = RoundedCornerShape(18.dp), colors = CardDefaults.cardColors(
                containerColor = MaterialTheme.colorScheme.surfaceContainerLow,
            )) {
                Column(Modifier.fillMaxWidth().padding(16.dp), verticalArrangement = Arrangement.spacedBy(8.dp)) {
                    Text("Audio receiver", style = MaterialTheme.typography.titleMedium)
                    Metric("State", state.receiver.name)
                    Metric("Playback", state.playbackStatus)
                    Metric("Packets received", state.receiverDiagnostics.packetsReceived.toString())
                    Metric("Dropped / invalid", "${state.receiverDiagnostics.droppedPackets} / ${state.receiverDiagnostics.invalidPackets}")
                    Metric("Bytes received", state.receiverDiagnostics.bytesReceived.toString())
                    Metric("Connected", if (state.receiverDiagnostics.isConnected) "Yes" else "No")
                    Metric("Queue", "${state.playbackDiagnostics.queuedPacketDurationMs} ms / ${state.playbackDiagnostics.targetQueueDurationMs} ms · ${state.playbackDiagnostics.droppedPackets} overflow drops")
                    Row(horizontalArrangement = Arrangement.spacedBy(10.dp)) {
                        Button(
                            onClick = { runCatching { onStartReceiver(currentSettings()); formError = null }.onFailure { formError = it.message } },
                            enabled = state.receiver in setOf(LaneState.STOPPED, LaneState.ERROR),
                            modifier = Modifier.weight(1f),
                        ) { Text("Start receiver") }
                        Button(onClick = onStopReceiver, enabled = state.receiver in setOf(LaneState.STARTING, LaneState.RUNNING, LaneState.ERROR), modifier = Modifier.weight(1f)) {
                            Text("Stop receiver")
                        }
                    }
                }
            }

            Row(horizontalArrangement = Arrangement.spacedBy(10.dp)) {
                Metric("Sample rate", state.sampleRate.takeIf { it > 0 }?.let { "$it Hz · stereo float32" } ?: "—", Modifier.weight(1f))
                Metric("Node", state.status.name, Modifier.weight(1f))
            }
            state.error?.let { message ->
                Surface(color = MaterialTheme.colorScheme.errorContainer, shape = RoundedCornerShape(12.dp)) {
                    Text("Error: $message", Modifier.padding(14.dp), color = MaterialTheme.colorScheme.onErrorContainer)
                }
            }
            Text("Mixed playback capture requires Android 10+ and runs as a foreground service. Some source apps may block capture.", style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.onSurfaceVariant)
            Spacer(Modifier.height(2.dp))
        }
    }
}

@Composable
private fun Metric(label: String, value: String, modifier: Modifier = Modifier) {
    Column(modifier = modifier, verticalArrangement = Arrangement.spacedBy(3.dp)) {
        Text(label, style = MaterialTheme.typography.labelMedium, color = MaterialTheme.colorScheme.onSurfaceVariant)
        Text(value, style = MaterialTheme.typography.bodyLarge, fontWeight = FontWeight.Medium)
    }
}
