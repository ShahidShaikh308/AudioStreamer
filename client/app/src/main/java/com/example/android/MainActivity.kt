package com.example.android

import android.os.Bundle
import android.os.Build
import android.content.pm.PackageManager
import androidx.activity.ComponentActivity
import androidx.activity.result.contract.ActivityResultContracts
import androidx.activity.compose.setContent
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.material3.Button
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Scaffold
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.setValue
import androidx.compose.ui.Modifier
import androidx.compose.ui.unit.dp
import com.example.android.audio.AudioPlayer
import com.example.android.audio.PlaybackDiagnostics
import com.example.android.network.NetworkDiagnostics
import com.example.android.network.UdpReceiver
import com.example.android.ui.theme.AndroidTheme
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.Job
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.cancel

private data class ReceiverUiState(
    val isRunning: Boolean = false,
    val network: NetworkDiagnostics = NetworkDiagnostics(),
    val playback: PlaybackDiagnostics = PlaybackDiagnostics(),
    val error: String? = null,
)

class MainActivity : ComponentActivity() {
    private val receiverScope = CoroutineScope(SupervisorJob() + Dispatchers.Main.immediate)
    private var receiver: UdpReceiver? = null
    private var audioPlayer: AudioPlayer? = null
    private var playbackJob: Job? = null
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
            AndroidTheme {
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
        if (Build.VERSION.SDK_INT >= 37 &&
            checkSelfPermission(LOCAL_NETWORK_PERMISSION) != PackageManager.PERMISSION_GRANTED
        ) {
            localNetworkPermissionRequest.launch(LOCAL_NETWORK_PERMISSION)
        } else {
            startReceiver()
        }
    }

    private fun startReceiver() {
        if (receiver != null) return
        receiverUiState = ReceiverUiState(isRunning = true)

        val player = AudioPlayer()
        audioPlayer = player
        playbackJob = player.start(
            scope = receiverScope,
            onDiagnostics = { playback ->
                runOnUiThread { receiverUiState = receiverUiState.copy(playback = playback) }
            },
            onError = { error ->
                runOnUiThread {
                    receiverUiState = receiverUiState.copy(error = error.message ?: "Audio playback failed")
                    stopReceiver()
                }
            },
        )

        val udpReceiver = UdpReceiver()
        receiver = udpReceiver
        udpReceiver.start(
            scope = receiverScope,
            onPacket = { packet -> player.enqueue(packet) },
            onDiagnostics = { network ->
                runOnUiThread { receiverUiState = receiverUiState.copy(network = network) }
            },
            onError = { error ->
                runOnUiThread {
                    receiverUiState = receiverUiState.copy(error = error.message ?: "UDP receiver failed")
                    stopReceiver()
                }
            },
        )
    }

    private fun stopReceiver() {
        receiver?.stop()
        receiver = null
        playbackJob?.cancel()
        playbackJob = null
        audioPlayer?.close()
        audioPlayer = null
        receiverUiState = receiverUiState.copy(
            isRunning = false,
            network = receiverUiState.network.copy(isListening = false, isConnected = false),
            playback = PlaybackDiagnostics(),
        )
    }

    override fun onDestroy() {
        stopReceiver()
        receiverScope.cancel()
        super.onDestroy()
    }

    private companion object {
        const val LOCAL_NETWORK_PERMISSION = "android.permission.ACCESS_LOCAL_NETWORK"
    }
}

@Composable
private fun ReceiverScreen(state: ReceiverUiState, onToggle: () -> Unit) {
    Scaffold { contentPadding ->
        Column(
            modifier = Modifier
                .fillMaxSize()
                .padding(contentPadding)
                .padding(24.dp),
            verticalArrangement = Arrangement.spacedBy(12.dp),
        ) {
            Text("AudioStreamer Receiver", style = MaterialTheme.typography.headlineSmall)
            Text(if (state.network.isListening) "Listening on UDP port 5005" else "Receiver stopped")
            Text(
                "Connected: " + when {
                    state.network.isConnected -> "Yes"
                    state.network.isListening -> "Waiting for audio"
                    else -> "No"
                },
            )
            Text("Sample Rate: ${state.network.sampleRate.takeIf { it > 0 }?.let { "$it Hz" } ?: "—"}")
            Text("Channels: ${state.network.channels.takeIf { it > 0 } ?: "—"}")
            Text("Packets Received: ${state.network.packetsReceived}")
            Text("Dropped Packets: ${state.network.droppedPackets + state.playback.droppedPackets}")

            val rate = state.network.sampleRate
            val capacityFrames = state.playback.bufferCapacityFrames
            val bufferDescription = if (capacityFrames > 0 && rate > 0) {
                val capacityMs = capacityFrames * 1000 / rate
                "$capacityFrames frames (~$capacityMs ms capacity), " +
                    "${state.playback.queuedFrames} frames / ${state.playback.queuedPackets} packets queued"
            } else {
                "—"
            }
            Text("Current Buffer Size: $bufferDescription")

            state.error?.let { Text("Error: $it", color = MaterialTheme.colorScheme.error) }
            Spacer(Modifier.height(4.dp))
            Button(onClick = onToggle) {
                Text(if (state.isRunning) "Stop Receiver" else "Start Receiver")
            }
        }
    }
}
