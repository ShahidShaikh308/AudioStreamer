package com.audiostreamer.receiver

import android.content.pm.PackageManager
import android.os.Build
import android.os.Bundle
import androidx.activity.ComponentActivity
import androidx.activity.compose.setContent
import androidx.activity.result.contract.ActivityResultContracts
import androidx.compose.foundation.background
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
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
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Scaffold
import androidx.compose.material3.Surface
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import com.audiostreamer.receiver.audio.AudioPlayer
import com.audiostreamer.receiver.audio.PlaybackDiagnostics
import com.audiostreamer.receiver.network.NetworkDiagnostics
import com.audiostreamer.receiver.network.UdpReceiver
import com.audiostreamer.receiver.ui.theme.AudioStreamerTheme
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.Job
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.cancel
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext

private enum class ReceiverStatus(val label: String) {
    WAITING("Waiting for server..."),
    RECEIVING("Receiving audio..."),
    CONNECTION_LOST("Connection lost"),
    INVALID_PACKET("Invalid packet"),
    STOPPING("Stopping playback..."),
    STOPPED("Playback stopped"),
}

private data class ReceiverUiState(
    val isRunning: Boolean = false,
    val isStopping: Boolean = false,
    val status: ReceiverStatus = ReceiverStatus.STOPPED,
    val network: NetworkDiagnostics = NetworkDiagnostics(),
    val playback: PlaybackDiagnostics = PlaybackDiagnostics(),
    val error: String? = null,
)

class MainActivity : ComponentActivity() {
    private val receiverScope = CoroutineScope(SupervisorJob() + Dispatchers.Main.immediate)
    private val cleanupScope = CoroutineScope(SupervisorJob() + Dispatchers.IO)
    private var receiver: UdpReceiver? = null
    private var audioPlayer: AudioPlayer? = null
    private var receiverJob: Job? = null
    private var playbackJob: Job? = null
    private var activeSessionId: Long? = null
    private var nextSessionId = 0L
    private var isStopping = false
    @Volatile private var isClosing = false
    private var receiverUiState by mutableStateOf(ReceiverUiState())

    private val localNetworkPermissionRequest = registerForActivityResult(
        ActivityResultContracts.RequestPermission(),
    ) { granted ->
        if (granted) {
            startReceiver()
        } else {
            receiverUiState = receiverUiState.copy(
                error = "Local network permission is required to receive audio from the server",
            )
        }
    }

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        setContent {
            AudioStreamerTheme {
                ReceiverScreen(
                    state = receiverUiState,
                    onToggle = {
                        if (receiverUiState.isRunning) stopReceiver() else requestReceiverStart()
                    },
                )
            }
        }
    }

    private fun requestReceiverStart() {
        if (isStopping || receiverUiState.isRunning) return
        if (Build.VERSION.SDK_INT >= 37 &&
            checkSelfPermission(LOCAL_NETWORK_PERMISSION) != PackageManager.PERMISSION_GRANTED
        ) {
            localNetworkPermissionRequest.launch(LOCAL_NETWORK_PERMISSION)
        } else {
            startReceiver()
        }
    }

    private fun startReceiver() {
        if (receiver != null || isStopping || isClosing) return
        val sessionId = ++nextSessionId
        activeSessionId = sessionId
        receiverUiState = ReceiverUiState(
            isRunning = true,
            status = ReceiverStatus.WAITING,
        )

        val player = AudioPlayer()
        audioPlayer = player
        try {
            playbackJob = player.start(
                scope = receiverScope,
                onDiagnostics = { playback ->
                    updateSession(sessionId) { it.copy(playback = playback) }
                },
                onError = { error -> stopSessionWithError(sessionId, error) },
            )

            val udpReceiver = UdpReceiver()
            receiver = udpReceiver
            receiverJob = udpReceiver.start(
                scope = receiverScope,
                onPacket = { packet -> player.enqueue(packet) },
                onDiagnostics = { network ->
                    updateSession(sessionId) { current ->
                        val status = when {
                            network.lastPacketInvalid -> ReceiverStatus.INVALID_PACKET
                            network.isConnected -> ReceiverStatus.RECEIVING
                            network.hasReceivedAudio -> ReceiverStatus.CONNECTION_LOST
                            network.isListening -> ReceiverStatus.WAITING
                            else -> current.status
                        }
                        current.copy(network = network, status = status)
                    }
                },
                onError = { error -> stopSessionWithError(sessionId, error) },
            )
        } catch (error: Exception) {
            stopReceiver(error.message ?: "Could not start the receiver")
        }
    }

    private fun updateSession(sessionId: Long, update: (ReceiverUiState) -> ReceiverUiState) {
        runOnUiThread {
            if (!isClosing && activeSessionId == sessionId) {
                receiverUiState = update(receiverUiState)
            }
        }
    }

    private fun stopSessionWithError(sessionId: Long, error: Throwable) {
        runOnUiThread {
            if (!isClosing && activeSessionId == sessionId) {
                stopReceiver(error.message ?: "Audio receiver stopped unexpectedly")
            }
        }
    }

    private fun stopReceiver(error: String? = null) {
        if (isStopping) {
            if (error != null) receiverUiState = receiverUiState.copy(error = error)
            return
        }

        val udpReceiver = receiver
        val player = audioPlayer
        val networkJob = receiverJob ?: udpReceiver?.stop()
        val currentPlaybackJob = playbackJob
        if (udpReceiver == null && player == null && networkJob == null && currentPlaybackJob == null) {
            receiverUiState = receiverUiState.copy(
                isRunning = false,
                status = ReceiverStatus.STOPPED,
                error = error ?: receiverUiState.error,
            )
            if (isClosing) cleanupScope.cancel()
            return
        }

        activeSessionId = null
        isStopping = true
        receiverUiState = receiverUiState.copy(
            isRunning = false,
            isStopping = true,
            status = ReceiverStatus.STOPPING,
            error = error ?: receiverUiState.error,
        )
        receiver = null
        audioPlayer = null
        receiverJob = null
        playbackJob = null
        udpReceiver?.stop()

        cleanupScope.launch {
            var cleanupMessage: String? = null
            try {
                networkJob?.join()
                player?.closeAndJoin()
            } catch (cleanupFailure: Exception) {
                android.util.Log.e(TAG, "Receiver shutdown failed", cleanupFailure)
                cleanupMessage = cleanupFailure.message
            } finally {
                withContext(Dispatchers.Main.immediate) {
                    isStopping = false
                    if (!isClosing) {
                        receiverUiState = receiverUiState.copy(
                            isRunning = false,
                            isStopping = false,
                            status = ReceiverStatus.STOPPED,
                            network = receiverUiState.network.copy(
                                isListening = false,
                                isConnected = false,
                            ),
                            playback = PlaybackDiagnostics(),
                            error = error ?: cleanupMessage ?: receiverUiState.error,
                        )
                    }
                }
                if (isClosing) cleanupScope.cancel()
            }
        }
    }

    override fun onDestroy() {
        isClosing = true
        stopReceiver()
        receiverScope.cancel()
        super.onDestroy()
    }

    private companion object {
        const val LOCAL_NETWORK_PERMISSION = "android.permission.ACCESS_LOCAL_NETWORK"
        const val TAG = "AudioStreamer"
    }
}

@Composable
private fun ReceiverScreen(state: ReceiverUiState, onToggle: () -> Unit) {
    val statusColor = when (state.status) {
        ReceiverStatus.RECEIVING -> MaterialTheme.colorScheme.primary
        ReceiverStatus.WAITING -> MaterialTheme.colorScheme.tertiary
        ReceiverStatus.CONNECTION_LOST,
        ReceiverStatus.INVALID_PACKET -> MaterialTheme.colorScheme.error
        ReceiverStatus.STOPPING,
        ReceiverStatus.STOPPED -> MaterialTheme.colorScheme.outline
    }
    val queuedMilliseconds = if (state.network.sampleRate > 0) {
        state.playback.queuedFrames * 1000 / state.network.sampleRate
    } else {
        0
    }
    val queueDescription = if (state.playback.bufferCapacityFrames > 0) {
        "${state.playback.queuedFrames} frames · ${state.playback.queuedPackets} packets · " +
            "~$queuedMilliseconds ms queued"
    } else if (state.playback.queuedPackets > 0) {
        "${state.playback.queuedPackets} packets buffered"
    } else {
        "—"
    }
    val connected = if (state.network.isConnected) "Yes" else "No"

    Scaffold(containerColor = MaterialTheme.colorScheme.surface) { contentPadding ->
        Column(
            modifier = Modifier
                .fillMaxSize()
                .verticalScroll(rememberScrollState())
                .padding(contentPadding)
                .padding(horizontal = 20.dp, vertical = 18.dp),
            verticalArrangement = Arrangement.spacedBy(16.dp),
        ) {
            Column(verticalArrangement = Arrangement.spacedBy(3.dp)) {
                Text(
                    "AudioStreamer",
                    style = MaterialTheme.typography.headlineMedium,
                    fontWeight = FontWeight.SemiBold,
                )
                Text(
                    "Wireless audio receiver",
                    style = MaterialTheme.typography.bodyLarge,
                    color = MaterialTheme.colorScheme.onSurfaceVariant,
                )
            }

            Card(
                colors = CardDefaults.cardColors(
                    containerColor = statusColor.copy(alpha = 0.10f),
                ),
                shape = RoundedCornerShape(20.dp),
            ) {
                Row(
                    modifier = Modifier
                        .fillMaxWidth()
                        .padding(18.dp),
                    verticalAlignment = Alignment.CenterVertically,
                    horizontalArrangement = Arrangement.spacedBy(12.dp),
                ) {
                    Box(
                        modifier = Modifier
                            .size(12.dp)
                            .background(statusColor, CircleShape),
                    )
                    Column(verticalArrangement = Arrangement.spacedBy(3.dp)) {
                        Text(
                            state.status.label,
                            style = MaterialTheme.typography.titleMedium,
                            fontWeight = FontWeight.SemiBold,
                            color = statusColor,
                        )
                        Text(
                            if (state.network.isListening) "Listening on UDP port 5005" else "Receiver is not listening",
                            style = MaterialTheme.typography.bodySmall,
                            color = MaterialTheme.colorScheme.onSurfaceVariant,
                        )
                        if (state.status == ReceiverStatus.INVALID_PACKET) {
                            state.network.lastInvalidReason?.let { reason ->
                                Text(
                                    reason,
                                    style = MaterialTheme.typography.bodySmall,
                                    color = MaterialTheme.colorScheme.error,
                                )
                            }
                        }
                    }
                }
            }

            Card(
                colors = CardDefaults.cardColors(
                    containerColor = MaterialTheme.colorScheme.surfaceContainerLow,
                ),
                shape = RoundedCornerShape(20.dp),
            ) {
                Column(
                    modifier = Modifier.padding(18.dp),
                    verticalArrangement = Arrangement.spacedBy(16.dp),
                ) {
                    Text("Stream diagnostics", style = MaterialTheme.typography.titleMedium)
                    Row(horizontalArrangement = Arrangement.spacedBy(12.dp)) {
                        Metric("Connected", connected, Modifier.weight(1f))
                        Metric(
                            "Sample rate",
                            state.network.sampleRate.takeIf { it > 0 }?.let { "$it Hz" } ?: "—",
                            Modifier.weight(1f),
                        )
                    }
                    Row(horizontalArrangement = Arrangement.spacedBy(12.dp)) {
                        Metric(
                            "Channels",
                            state.network.channels.takeIf { it > 0 }?.toString() ?: "—",
                            Modifier.weight(1f),
                        )
                        Metric("Packets received", state.network.packetsReceived.toString(), Modifier.weight(1f))
                    }
                    Row(horizontalArrangement = Arrangement.spacedBy(12.dp)) {
                        Metric(
                            "Dropped packets",
                            (state.network.droppedPackets + state.playback.droppedPackets).toString(),
                            Modifier.weight(1f),
                        )
                        Metric("Invalid packets", state.network.invalidPackets.toString(), Modifier.weight(1f))
                    }
                    Metric("Current queue", queueDescription, Modifier.fillMaxWidth())
                }
            }

            state.error?.let { message ->
                Surface(
                    color = MaterialTheme.colorScheme.errorContainer,
                    shape = RoundedCornerShape(14.dp),
                ) {
                    Text(
                        "Error: $message",
                        modifier = Modifier.padding(14.dp),
                        color = MaterialTheme.colorScheme.onErrorContainer,
                        style = MaterialTheme.typography.bodyMedium,
                    )
                }
            }

            Button(
                onClick = onToggle,
                enabled = !state.isStopping,
                modifier = Modifier.fillMaxWidth(),
            ) {
                Text(
                    when {
                        state.isStopping -> "Stopping…"
                        state.isRunning -> "Stop Receiver"
                        else -> "Start Receiver"
                    },
                    modifier = Modifier.padding(vertical = 4.dp),
                )
            }

            Text(
                "Keep this screen open while receiving audio.",
                modifier = Modifier.fillMaxWidth(),
                style = MaterialTheme.typography.bodySmall,
                color = MaterialTheme.colorScheme.onSurfaceVariant,
                fontSize = 12.sp,
            )
            Spacer(Modifier.height(2.dp))
        }
    }
}

@Composable
private fun Metric(label: String, value: String, modifier: Modifier = Modifier) {
    Column(modifier = modifier, verticalArrangement = Arrangement.spacedBy(4.dp)) {
        Text(
            label,
            style = MaterialTheme.typography.labelMedium,
            color = MaterialTheme.colorScheme.onSurfaceVariant,
        )
        Text(
            value,
            style = MaterialTheme.typography.bodyLarge,
            fontWeight = FontWeight.Medium,
        )
    }
}
