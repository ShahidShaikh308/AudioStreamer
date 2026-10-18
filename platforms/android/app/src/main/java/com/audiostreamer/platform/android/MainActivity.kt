package com.audiostreamer.platform.android

import android.os.Bundle
import androidx.activity.ComponentActivity
import androidx.activity.compose.setContent
import androidx.compose.foundation.Image
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.ColumnScope
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.verticalScroll
import androidx.compose.material3.Button
import androidx.compose.material3.Card
import androidx.compose.material3.CardDefaults
import androidx.compose.material3.HorizontalDivider
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedButton
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.Scaffold
import androidx.compose.material3.Surface
import androidx.compose.material3.Tab
import androidx.compose.material3.PrimaryTabRow
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.collectAsState
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableIntStateOf
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.layout.ContentScale
import androidx.compose.ui.res.painterResource
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
                    onStartStream = node::startStream,
                    onStopStream = node::stopStream,
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
    onStartStream: (Long, Long) -> Unit,
    onStopStream: (Long, Long) -> Unit,
) {
    var selectedTab by remember { mutableIntStateOf(0) }
    var nodeName by remember(settings.nodeName) { mutableStateOf(settings.nodeName) }
    var destinationIp by remember(settings.destinationIp) { mutableStateOf(settings.destinationIp) }
    var destinationPort by remember(settings.destinationPort) { mutableStateOf(settings.destinationPort.toString()) }
    var receivePort by remember(settings.receivePort) { mutableStateOf(settings.receivePort.toString()) }
    var controlPort by remember(settings.controlPort) { mutableStateOf(settings.controlPort.toString()) }
    var logLevel by remember(settings.logLevel) { mutableStateOf(settings.logLevel) }
    var formError by remember { mutableStateOf<String?>(null) }

    fun currentSettings(): NodeSettings {
        val updated = NodeSettings(
            nodeName = nodeName.trim(),
            destinationIp = destinationIp.trim(),
            destinationPort = destinationPort.toIntOrNull() ?: 0,
            receivePort = receivePort.toIntOrNull() ?: 0,
            logLevel = logLevel.trim().uppercase(),
            nodeId = settings.nodeId,
            controlPort = controlPort.toIntOrNull() ?: 0,
            controllerSecretHex = settings.controllerSecretHex,
        )
        require(updated.nodeName.isNotBlank()) { "Enter a name for this device" }
        require(updated.destinationPort in 1..65535) { "Destination port must be 1–65535" }
        require(updated.receivePort in 1..65535) { "Receive port must be 1–65535" }
        require(updated.controlPort in 49152..65535) { "Control port must be 49152–65535" }
        return updated
    }

    Scaffold(containerColor = MaterialTheme.colorScheme.background) { insets ->
        Column(Modifier.fillMaxSize().padding(insets)) {
            Row(
                modifier = Modifier.fillMaxWidth().padding(horizontal = 20.dp, vertical = 16.dp),
                verticalAlignment = Alignment.CenterVertically,
                horizontalArrangement = Arrangement.spacedBy(14.dp),
            ) {
                Image(
                    painter = painterResource(R.drawable.audiostreamer_logo),
                    contentDescription = "AudioStreamer logo",
                    modifier = Modifier.size(58.dp).clip(RoundedCornerShape(16.dp)),
                    contentScale = ContentScale.Crop,
                )
                Column(Modifier.weight(1f)) {
                    Text("AudioStreamer", style = MaterialTheme.typography.headlineSmall, fontWeight = FontWeight.Bold)
                    Text("Wireless audio node", style = MaterialTheme.typography.bodyMedium, color = MaterialTheme.colorScheme.onSurfaceVariant)
                }
                StatusPill(state.status.name)
            }

            PrimaryTabRow(selectedTabIndex = selectedTab) {
                listOf("Overview", "Settings").forEachIndexed { index, title ->
                    Tab(selected = selectedTab == index, onClick = { selectedTab = index }, text = { Text(title) })
                }
            }

            Column(
                modifier = Modifier.fillMaxSize().verticalScroll(rememberScrollState()).padding(horizontal = 18.dp, vertical = 16.dp),
                verticalArrangement = Arrangement.spacedBy(14.dp),
            ) {
                if (selectedTab == 0) {
                    NodeSummary(state, settings)
                    KnownNodesCard(
                        state = state,
                        localNodeId = settings.nodeId,
                        canCapture = canCapture,
                        onStartStream = onStartStream,
                        onStopStream = onStopStream,
                        onFailure = { formError = it.message ?: "Could not start the stream" },
                    )
                    LaneCard(
                        title = "Audio sender",
                        status = state.sender.name,
                        detailLabel = "Capture",
                        detail = state.captureStatus,
                        metrics = listOf(
                            "Packets sent" to state.senderDiagnostics.packetsSent.toString(),
                            "Data sent" to formatBytes(state.senderDiagnostics.bytesSent),
                        ),
                        primaryText = "Start sender",
                        onPrimary = {
                            runCatching { onStartSender(currentSettings()); formError = null }
                                .onFailure { formError = it.message ?: "Could not start the sender" }
                        },
                        primaryEnabled = canCapture && state.sender in setOf(LaneState.STOPPED, LaneState.ERROR),
                        onStop = onStopSender,
                        stopEnabled = state.sender in setOf(LaneState.STARTING, LaneState.RUNNING, LaneState.ERROR),
                    )
                    LaneCard(
                        title = "Audio receiver",
                        status = state.receiver.name,
                        detailLabel = "Playback",
                        detail = state.playbackStatus,
                        metrics = listOf(
                            "Packets received" to state.receiverDiagnostics.packetsReceived.toString(),
                            "Dropped / invalid" to "${state.receiverDiagnostics.droppedPackets} / ${state.receiverDiagnostics.invalidPackets}",
                            "Data received" to formatBytes(state.receiverDiagnostics.bytesReceived),
                            "Buffer" to "${state.playbackDiagnostics.queuedPacketDurationMs} / ${state.playbackDiagnostics.targetQueueDurationMs} ms",
                        ),
                        primaryText = "Start receiver",
                        onPrimary = {
                            runCatching { onStartReceiver(currentSettings()); formError = null }
                                .onFailure { formError = it.message ?: "Could not start the receiver" }
                        },
                        primaryEnabled = state.receiver in setOf(LaneState.STOPPED, LaneState.ERROR),
                        onStop = onStopReceiver,
                        stopEnabled = state.receiver in setOf(LaneState.STARTING, LaneState.RUNNING, LaneState.ERROR),
                    )
                    state.error?.let { ErrorCard(it) }
                    formError?.let { ErrorCard(it) }
                    Text(
                        "Mixed playback capture requires Android 10 or later. Some apps restrict audio capture.",
                        style = MaterialTheme.typography.bodySmall,
                        color = MaterialTheme.colorScheme.onSurfaceVariant,
                    )
                } else {
                    SettingsCard(
                        nodeName = nodeName,
                        onNodeNameChange = { nodeName = it },
                        destinationIp = destinationIp,
                        onDestinationIpChange = { destinationIp = it },
                        destinationPort = destinationPort,
                        onDestinationPortChange = { destinationPort = it.filter(Char::isDigit).take(5) },
                        receivePort = receivePort,
                        onReceivePortChange = { receivePort = it.filter(Char::isDigit).take(5) },
                        controlPort = controlPort,
                        onControlPortChange = { controlPort = it.filter(Char::isDigit).take(5) },
                        logLevel = logLevel,
                        onLogLevelChange = { logLevel = it.uppercase().take(8) },
                        onSave = {
                            runCatching { currentSettings().also(onSaveSettings) }
                                .onSuccess { formError = null }
                                .onFailure { formError = it.message ?: "Check the settings and try again" }
                        },
                        error = formError,
                        nodeId = settings.nodeId,
                    )
                    Text(
                        "Controller authorization is configured automatically on this build.",
                        style = MaterialTheme.typography.bodySmall,
                        color = MaterialTheme.colorScheme.onSurfaceVariant,
                    )
                }
                Spacer(Modifier.height(8.dp))
            }
        }
    }
}

@Composable
private fun NodeSummary(state: NodeSnapshot, settings: NodeSettings) {
    Card(colors = CardDefaults.cardColors(containerColor = MaterialTheme.colorScheme.primaryContainer)) {
        Column(Modifier.fillMaxWidth().padding(18.dp), verticalArrangement = Arrangement.spacedBy(10.dp)) {
            Text("This device", style = MaterialTheme.typography.titleMedium, color = MaterialTheme.colorScheme.onPrimaryContainer)
            Text(state.nodeName, style = MaterialTheme.typography.headlineSmall, fontWeight = FontWeight.SemiBold, color = MaterialTheme.colorScheme.onPrimaryContainer)
            Text("${state.localIp}  ·  ${state.controlStatus}", style = MaterialTheme.typography.bodyMedium, color = MaterialTheme.colorScheme.onPrimaryContainer)
            HorizontalDivider(color = MaterialTheme.colorScheme.onPrimaryContainer.copy(alpha = 0.18f))
            Row(horizontalArrangement = Arrangement.spacedBy(24.dp)) {
                Metric("Audio format", state.sampleRate.takeIf { it > 0 }?.let { "$it Hz · stereo" } ?: "Ready when started")
                Metric("Device ID", settings.nodeId.toULong().toString(16).takeLast(8).uppercase())
            }
        }
    }
}

@Composable
private fun KnownNodesCard(
    state: NodeSnapshot,
    localNodeId: Long,
    canCapture: Boolean,
    onStartStream: (Long, Long) -> Unit,
    onStopStream: (Long, Long) -> Unit,
    onFailure: (Throwable) -> Unit,
) {
    SectionCard(title = "Nearby devices", subtitle = "Start a stream directly from a discovered node") {
        if (state.knownNodes.isEmpty()) {
            Surface(color = MaterialTheme.colorScheme.surfaceContainerHigh, shape = RoundedCornerShape(14.dp)) {
                Column(Modifier.fillMaxWidth().padding(16.dp), verticalArrangement = Arrangement.spacedBy(4.dp)) {
                    Text("Looking for devices", style = MaterialTheme.typography.titleSmall)
                    Text("Keep your devices on the same local network.", style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.onSurfaceVariant)
                }
            }
        }
        state.knownNodes.forEachIndexed { index, peer ->
            if (index > 0) HorizontalDivider()
            Column(verticalArrangement = Arrangement.spacedBy(9.dp), modifier = Modifier.padding(vertical = 5.dp)) {
                Row(verticalAlignment = Alignment.CenterVertically, horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                    Surface(modifier = Modifier.size(10.dp), shape = CircleShape, color = MaterialTheme.colorScheme.primary) {}
                    Text(peer.name, style = MaterialTheme.typography.titleMedium, fontWeight = FontWeight.SemiBold, modifier = Modifier.weight(1f))
                    Text(peer.status.name, style = MaterialTheme.typography.labelMedium, color = MaterialTheme.colorScheme.primary)
                }
                Text("${peer.platform}  ·  ${peer.address}:${peer.receivePort}", style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.onSurfaceVariant)
                val capture = peer.capabilities and com.audiostreamer.control_plane.NodeCapability.CAPTURE != 0
                val playback = peer.capabilities and com.audiostreamer.control_plane.NodeCapability.PLAYBACK != 0
                Text(
                    "${if (capture) "Sender" else ""}${if (capture && playback) "  ·  " else ""}${if (playback) "Receiver" else ""}".ifBlank { "Audio capabilities unavailable" },
                    style = MaterialTheme.typography.labelMedium,
                )
                Row(horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                    Button(
                        onClick = { runCatching { onStartStream(localNodeId, peer.nodeId) }.onFailure(onFailure) },
                        enabled = canCapture && playback,
                        modifier = Modifier.weight(1f),
                    ) { Text("Send audio") }
                    OutlinedButton(
                        onClick = { runCatching { onStartStream(peer.nodeId, localNodeId) }.onFailure(onFailure) },
                        enabled = capture,
                        modifier = Modifier.weight(1f),
                    ) { Text("Receive audio") }
                }
                OutlinedButton(
                    onClick = { runCatching { onStopStream(localNodeId, peer.nodeId); onStopStream(peer.nodeId, localNodeId) }.onFailure(onFailure) },
                    modifier = Modifier.fillMaxWidth(),
                ) { Text("Stop streams with this device") }
            }
        }
    }
}

@Composable
private fun LaneCard(
    title: String,
    status: String,
    detailLabel: String,
    detail: String,
    metrics: List<Pair<String, String>>,
    primaryText: String,
    onPrimary: () -> Unit,
    primaryEnabled: Boolean,
    onStop: () -> Unit,
    stopEnabled: Boolean,
) {
    SectionCard(title = title) {
        Row(verticalAlignment = Alignment.CenterVertically, horizontalArrangement = Arrangement.spacedBy(8.dp)) {
            StatusPill(status)
            Text("$detailLabel  ·  $detail", style = MaterialTheme.typography.bodyMedium)
        }
        metrics.chunked(2).forEach { row ->
            Row(horizontalArrangement = Arrangement.spacedBy(12.dp), modifier = Modifier.fillMaxWidth()) {
                row.forEach { (label, value) -> Metric(label, value, Modifier.weight(1f)) }
                if (row.size == 1) Spacer(Modifier.weight(1f))
            }
        }
        Row(horizontalArrangement = Arrangement.spacedBy(10.dp)) {
            Button(onClick = onPrimary, enabled = primaryEnabled, modifier = Modifier.weight(1f)) { Text(primaryText) }
            OutlinedButton(onClick = onStop, enabled = stopEnabled, modifier = Modifier.weight(1f)) { Text("Stop") }
        }
    }
}

@Composable
private fun SettingsCard(
    nodeName: String,
    onNodeNameChange: (String) -> Unit,
    destinationIp: String,
    onDestinationIpChange: (String) -> Unit,
    destinationPort: String,
    onDestinationPortChange: (String) -> Unit,
    receivePort: String,
    onReceivePortChange: (String) -> Unit,
    controlPort: String,
    onControlPortChange: (String) -> Unit,
    logLevel: String,
    onLogLevelChange: (String) -> Unit,
    onSave: () -> Unit,
    error: String?,
    nodeId: Long,
) {
    SectionCard(title = "Device settings", subtitle = "Network and identity") {
        OutlinedTextField(nodeName, onNodeNameChange, label = { Text("Device name") }, singleLine = true, modifier = Modifier.fillMaxWidth())
        OutlinedTextField(destinationIp, onDestinationIpChange, label = { Text("Default destination IPv4") }, singleLine = true, modifier = Modifier.fillMaxWidth())
        Row(horizontalArrangement = Arrangement.spacedBy(10.dp)) {
            OutlinedTextField(destinationPort, onDestinationPortChange, label = { Text("Send port") }, singleLine = true, modifier = Modifier.weight(1f))
            OutlinedTextField(receivePort, onReceivePortChange, label = { Text("Receive port") }, singleLine = true, modifier = Modifier.weight(1f))
        }
        OutlinedTextField(controlPort, onControlPortChange, label = { Text("Control port") }, singleLine = true, modifier = Modifier.fillMaxWidth())
        OutlinedTextField(logLevel, onLogLevelChange, label = { Text("Log level") }, singleLine = true, modifier = Modifier.fillMaxWidth())
        Text("Node ID  ·  ${nodeId.toULong().toString(16).padStart(16, '0').uppercase()}", style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.onSurfaceVariant)
        error?.let { Text(it, color = MaterialTheme.colorScheme.error, style = MaterialTheme.typography.bodySmall) }
        Button(onClick = onSave, modifier = Modifier.fillMaxWidth()) { Text("Save settings") }
    }
}

@Composable
private fun SectionCard(title: String, subtitle: String? = null, content: @Composable ColumnScope.() -> Unit) {
    Card(shape = RoundedCornerShape(20.dp), colors = CardDefaults.cardColors(containerColor = MaterialTheme.colorScheme.surfaceContainerLow)) {
        Column(Modifier.fillMaxWidth().padding(16.dp), verticalArrangement = Arrangement.spacedBy(13.dp)) {
            Column(verticalArrangement = Arrangement.spacedBy(3.dp)) {
                Text(title, style = MaterialTheme.typography.titleLarge, fontWeight = FontWeight.SemiBold)
                subtitle?.let { Text(it, style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.onSurfaceVariant) }
            }
            content(this)
        }
    }
}

@Composable
private fun StatusPill(status: String) {
    val active = status.equals("RUNNING", ignoreCase = true) || status.equals("CONNECTED", ignoreCase = true)
    Surface(shape = RoundedCornerShape(50), color = if (active) MaterialTheme.colorScheme.secondaryContainer else MaterialTheme.colorScheme.surfaceContainerHigh) {
        Text(status.lowercase().replaceFirstChar { it.uppercase() }, modifier = Modifier.padding(horizontal = 11.dp, vertical = 6.dp), style = MaterialTheme.typography.labelMedium, fontWeight = FontWeight.Medium)
    }
}

@Composable
private fun Metric(label: String, value: String, modifier: Modifier = Modifier) {
    Column(modifier = modifier, verticalArrangement = Arrangement.spacedBy(3.dp)) {
        Text(label, style = MaterialTheme.typography.labelMedium, color = MaterialTheme.colorScheme.onSurfaceVariant)
        Text(value, style = MaterialTheme.typography.bodyLarge, fontWeight = FontWeight.Medium)
    }
}

@Composable
private fun ErrorCard(message: String) {
    Surface(color = MaterialTheme.colorScheme.errorContainer, shape = RoundedCornerShape(14.dp)) {
        Text(message, Modifier.fillMaxWidth().padding(14.dp), color = MaterialTheme.colorScheme.onErrorContainer)
    }
}

private fun formatBytes(bytes: Long): String = when {
    bytes >= 1_000_000 -> "%.1f MB".format(bytes / 1_000_000.0)
    bytes >= 1_000 -> "%.1f KB".format(bytes / 1_000.0)
    else -> "$bytes B"
}
