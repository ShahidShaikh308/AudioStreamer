package com.audiostreamer.platform.android.node

import android.os.SystemClock
import androidx.activity.ComponentActivity
import com.audiostreamer.audio_plane.playback.AudioPlayer
import com.audiostreamer.audio_plane.playback.PlaybackDiagnostics
import com.audiostreamer.audio_plane.transport.NetworkDiagnostics
import com.audiostreamer.audio_plane.transport.SenderDiagnostics
import com.audiostreamer.audio_plane.transport.UdpReceiver
import com.audiostreamer.audio_plane.transport.UdpSender
import com.audiostreamer.core.node.INode
import com.audiostreamer.core.node.LaneState
import com.audiostreamer.core.node.NodeCapabilities
import com.audiostreamer.core.node.NodeSettings
import com.audiostreamer.core.node.NodeSnapshot
import com.audiostreamer.core.node.aggregateStatus
import com.audiostreamer.platform.android.capture.AndroidPlaybackCapture
import com.audiostreamer.platform.android.capture.CaptureCallback
import java.net.NetworkInterface
import java.util.Collections
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.CoroutineStart
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.Job
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.cancel
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.launch

/** Android INode implementation. UI and future control code interact only through this contract. */
class AndroidNode(activity: ComponentActivity) : INode {
    private val lock = Any()
    private val configStore = AndroidNodeConfigStore(activity.applicationContext)
    private var currentSettings = configStore.load()
    private var logger = AndroidNodeLogger(currentSettings.logLevel)
    private val capture = AndroidPlaybackCapture(activity)
    private val localNetworkPermission = LocalNetworkPermission(activity)
    private val nodeScope = CoroutineScope(SupervisorJob() + Dispatchers.IO)
    private val mutableState = MutableStateFlow(
        NodeSnapshot(
            nodeName = currentSettings.nodeName,
            localIp = findLocalIp(),
        ),
    )
    private var sender: UdpSender? = null
    private var receiver: UdpReceiver? = null
    private var receiverJob: Job? = null
    private var player: AudioPlayer? = null
    private var receiverCleanupJob: Job? = null
    private var captureRequested = false
    private var lastSenderDiagnosticsAt = 0L
    private var shutdownRequested = false

    override val capabilities = NodeCapabilities(
        canCapture = android.os.Build.VERSION.SDK_INT >= android.os.Build.VERSION_CODES.Q,
        canSend = true,
        canReceive = true,
        canPlay = true,
    )
    override val state: StateFlow<NodeSnapshot> = mutableState.asStateFlow()
    override val settings: NodeSettings
        get() = synchronized(lock) { currentSettings }

    init {
        if (!capabilities.canCapture) {
            mutableState.value = mutableState.value.copy(captureStatus = "Requires Android 10 or newer")
        }
        logger.info("node_started name=${currentSettings.nodeName} localIp=${mutableState.value.localIp} protocol=v3")
    }

    override fun updateSettings(settings: NodeSettings) {
        validateSettings(settings)
        synchronized(lock) {
            check(!shutdownRequested) { "Node is shutting down" }
            currentSettings = settings.copy(logLevel = settings.logLevel.uppercase())
            logger = AndroidNodeLogger(currentSettings.logLevel)
            mutableState.value = mutableState.value.copy(nodeName = currentSettings.nodeName)
        }
    }

    override fun saveConfiguration() {
        synchronized(lock) {
            configStore.save(currentSettings)
            logger.info("configuration_saved name=${currentSettings.nodeName} " +
                "destination=${currentSettings.destinationIp}:${currentSettings.destinationPort} " +
                "receivePort=${currentSettings.receivePort}")
        }
    }

    override fun startSender() {
        synchronized(lock) {
            if (shutdownRequested || captureRequested ||
                senderLane() in setOf(LaneState.STARTING, LaneState.RUNNING, LaneState.STOPPING)
            ) return
            setSenderLaneLocked(LaneState.STARTING)
            captureRequested = false
            setStateLocked {
                it.copy(
                    captureStatus = "Requesting capture permission",
                    senderDiagnostics = SenderDiagnostics(),
                    error = null,
                )
            }
        }
        localNetworkPermission.runWhenAllowed(
            action = {
                val config = settings
                try {
                    synchronized(lock) {
                        if (shutdownRequested || senderLane() != LaneState.STARTING) return@runWhenAllowed
                        sender = UdpSender(config.destinationIp, config.destinationPort)
                        captureRequested = true
                    }
                    capture.start(senderCaptureCallback)
                } catch (error: Exception) {
                    senderFailure(error)
                }
            },
            onDenied = {
                if (senderLane() == LaneState.STARTING) {
                    senderFailure(SecurityException("Local network permission is required to send audio"))
                }
            },
        )
    }

    override fun stopSender() {
        val stopCapture: Boolean
        synchronized(lock) {
            val lane = senderLane()
            if (lane == LaneState.STOPPED) return
            if (lane == LaneState.STARTING && !captureRequested) {
                sender?.close()
                sender = null
                setSenderLaneLocked(LaneState.STOPPED)
                setStateLocked { it.copy(captureStatus = "Stopped") }
                maybeFinishShutdown()
                return
            }
            stopCapture = lane != LaneState.ERROR
            if (lane != LaneState.ERROR) setSenderLaneLocked(LaneState.STOPPING)
        }
        if (stopCapture) {
            capture.stop()
        } else {
            synchronized(lock) {
                sender?.close()
                sender = null
                setSenderLaneLocked(LaneState.STOPPED)
            }
            maybeFinishShutdown()
        }
    }

    override fun startReceiver() {
        synchronized(lock) {
            if (shutdownRequested || receiverCleanupJob?.isActive == true ||
                receiverLane() in setOf(LaneState.STARTING, LaneState.RUNNING, LaneState.STOPPING)
            ) return
            setReceiverLaneLocked(LaneState.STARTING)
            setStateLocked {
                it.copy(
                    playbackStatus = "Waiting for audio",
                    receiverDiagnostics = NetworkDiagnostics(),
                    playbackDiagnostics = PlaybackDiagnostics(),
                    error = null,
                )
            }
        }
        localNetworkPermission.runWhenAllowed(
            action = {
                try {
                    val receivePort = settings.receivePort
                    val newPlayer = AudioPlayer()
                    val newReceiver = UdpReceiver(receivePort)
                    synchronized(lock) {
                        if (shutdownRequested || receiverLane() != LaneState.STARTING) {
                            nodeScope.launch { newPlayer.closeAndJoin() }
                            return@runWhenAllowed
                        }
                        player = newPlayer
                        receiver = newReceiver
                        newPlayer.start(
                            scope = nodeScope,
                            onDiagnostics = ::onPlaybackDiagnostics,
                            onError = ::receiverFailure,
                        )
                        receiverJob = newReceiver.start(
                            scope = nodeScope,
                            onPacket = { packet -> newPlayer.enqueue(packet) },
                            onDiagnostics = ::onReceiverDiagnostics,
                            onError = ::receiverFailure,
                        )
                    }
                } catch (error: Exception) {
                    receiverFailure(error)
                }
            },
            onDenied = {
                if (receiverLane() == LaneState.STARTING) {
                    receiverFailure(SecurityException("Local network permission is required to receive audio"))
                }
            },
        )
    }

    override fun stopReceiver() {
        val activeReceiver: UdpReceiver?
        val activePlayer: AudioPlayer?
        val activeJob: Job?
        synchronized(lock) {
            if (receiverCleanupJob?.isActive == true) return
            val lane = receiverLane()
            if (lane == LaneState.STOPPED && receiver == null && player == null) return
            if (lane != LaneState.ERROR) setReceiverLaneLocked(LaneState.STOPPING)
            activeReceiver = receiver
            activePlayer = player
            activeJob = receiverJob
            activeReceiver?.stop()
            val cleanup = nodeScope.launch(start = CoroutineStart.LAZY) {
                try {
                    activeJob?.join()
                    activePlayer?.closeAndJoin()
                } catch (cancelled: CancellationException) {
                    throw cancelled
                } catch (error: Exception) {
                    logger.error("receiver_cleanup_error", error)
                    setError(error)
                } finally {
                    synchronized(lock) {
                        if (mutableState.value.playbackStatus == "Playing") logger.info("playback_stopped")
                        receiver = null
                        receiverJob = null
                        player = null
                        receiverCleanupJob = null
                        if (receiverLane() != LaneState.ERROR) setReceiverLaneLocked(LaneState.STOPPED)
                        setStateLocked {
                            it.copy(
                                receiverDiagnostics = it.receiverDiagnostics.copy(
                                    isListening = false,
                                    isConnected = false,
                                ),
                                playbackStatus = "Stopped",
                            )
                        }
                    }
                    logger.info("receiver_stopped")
                    maybeFinishShutdown()
                }
            }
            receiverCleanupJob = cleanup
            cleanup.start()
        }
    }

    override fun shutdown() {
        synchronized(lock) { shutdownRequested = true }
        logger.info("node_shutdown_requested")
        stopSender()
        stopReceiver()
        maybeFinishShutdown()
    }

    private val senderCaptureCallback = object : CaptureCallback {
        override fun onStarted(sampleRate: Int, channels: Int) {
            synchronized(lock) {
                if (senderLane() != LaneState.STARTING) return
                setSenderLaneLocked(LaneState.RUNNING)
                setStateLocked { it.copy(sampleRate = sampleRate, captureStatus = "Capturing ${sampleRate} Hz stereo PCM") }
            }
            logger.info("capture_started sampleRate=$sampleRate channels=$channels")
            logger.info("sender_started destination=${settings.destinationIp}:${settings.destinationPort}")
        }

        override fun onPcm(payload: ByteArray, length: Int, sampleRate: Int, channels: Int) {
            try {
                sender?.sendPcm(payload, length, sampleRate, channels)
                val now = SystemClock.elapsedRealtime()
                if (now - lastSenderDiagnosticsAt >= SENDER_STATS_INTERVAL_MS) {
                    lastSenderDiagnosticsAt = now
                    sender?.let { active ->
                        setState { it.copy(senderDiagnostics = active.diagnostics(sampleRate), sampleRate = sampleRate) }
                    }
                }
            } catch (error: Exception) {
                logger.warning("sender_network_error", error)
                setError(error)
                senderFailure(error)
            }
        }

        override fun onStopped() {
            synchronized(lock) {
                captureRequested = false
                sender?.close()
                sender = null
                if (senderLane() != LaneState.ERROR) setSenderLaneLocked(LaneState.STOPPED)
                setStateLocked { it.copy(captureStatus = "Stopped") }
            }
            logger.info("capture_stopped")
            logger.info("sender_stopped")
            maybeFinishShutdown()
        }

        override fun onError(error: Throwable) = senderFailure(error)
    }

    private fun onReceiverDiagnostics(diagnostics: NetworkDiagnostics) {
        synchronized(lock) {
            if (diagnostics.isListening && receiverLane() == LaneState.STARTING) {
                setReceiverLaneLocked(LaneState.RUNNING)
                logger.info("receiver_started port=${settings.receivePort}")
            }
            setStateLocked { current ->
                current.copy(
                    receiverDiagnostics = diagnostics,
                    sampleRate = diagnostics.sampleRate.takeIf { it > 0 } ?: current.sampleRate,
                )
            }
        }
    }

    private fun onPlaybackDiagnostics(diagnostics: PlaybackDiagnostics) {
        synchronized(lock) {
            val playbackStatus = if (diagnostics.bufferCapacityFrames > 0) "Playing" else "Waiting for packets"
            if (playbackStatus == "Playing" && mutableState.value.playbackStatus != "Playing") {
                logger.info("playback_started capacityFrames=${diagnostics.bufferCapacityFrames}")
            }
            setStateLocked { it.copy(playbackDiagnostics = diagnostics, playbackStatus = playbackStatus) }
        }
    }

    private fun senderFailure(error: Throwable) {
        logger.error("sender_or_capture_error", error)
        synchronized(lock) {
            if (senderLane() in setOf(LaneState.STOPPED, LaneState.STOPPING)) return
            sender?.close()
            sender = null
            setSenderLaneLocked(LaneState.ERROR)
            setStateLocked { it.copy(captureStatus = "Error", error = error.message ?: error.javaClass.simpleName) }
        }
        capture.stop()
    }

    private fun receiverFailure(error: Throwable) {
        logger.error("receiver_or_playback_error", error)
        synchronized(lock) {
            if (receiverLane() in setOf(LaneState.STOPPED, LaneState.STOPPING)) return
            setReceiverLaneLocked(LaneState.ERROR)
            setStateLocked { it.copy(playbackStatus = "Error", error = error.message ?: error.javaClass.simpleName) }
        }
        stopReceiver()
    }

    private fun setError(error: Throwable) {
        synchronized(lock) {
            setStateLocked { it.copy(error = error.message ?: error.javaClass.simpleName) }
        }
    }

    private fun setSenderLaneLocked(state: LaneState) {
        val current = mutableState.value.sender
        validateTransition(current, state)
        setStateLocked { it.copy(sender = state) }
    }

    private fun setReceiverLaneLocked(state: LaneState) {
        val current = mutableState.value.receiver
        validateTransition(current, state)
        setStateLocked { it.copy(receiver = state) }
    }

    private inline fun setStateLocked(transform: (NodeSnapshot) -> NodeSnapshot) {
        val next = transform(mutableState.value)
        mutableState.value = next.copy(status = aggregateStatus(next.sender, next.receiver))
    }

    private inline fun setState(crossinline transform: (NodeSnapshot) -> NodeSnapshot) {
        synchronized(lock) { setStateLocked { transform(it) } }
    }

    private fun senderLane(): LaneState = mutableState.value.sender
    private fun receiverLane(): LaneState = mutableState.value.receiver

    private fun maybeFinishShutdown() {
        val complete = synchronized(lock) {
            shutdownRequested && receiverCleanupJob == null &&
                senderLane() in setOf(LaneState.STOPPED, LaneState.ERROR) &&
                receiverLane() in setOf(LaneState.STOPPED, LaneState.ERROR)
        }
        if (complete) {
            logger.info("node_shutdown_complete")
            nodeScope.cancel()
        }
    }

    private fun findLocalIp(): String = try {
        Collections.list(NetworkInterface.getNetworkInterfaces())
            .flatMap { Collections.list(it.inetAddresses) }
            .firstOrNull { it is java.net.Inet4Address && !it.isLoopbackAddress }
            ?.hostAddress ?: "127.0.0.1"
    } catch (_: Exception) {
        "127.0.0.1"
    }

    private fun validateSettings(settings: NodeSettings) {
        require(settings.nodeName.isNotBlank() && settings.nodeName.length <= 128) { "Node name is required (max 128 characters)" }
        require(settings.destinationIp.matches(IPV4_PATTERN)) { "Destination must be an IPv4 address" }
        require(settings.destinationIp.split('.').all { it.toIntOrNull() in 0..255 }) { "Destination IPv4 address is invalid" }
        require(settings.destinationPort in 1..65535) { "Destination port must be 1..65535" }
        require(settings.receivePort in 1..65535) { "Receive port must be 1..65535" }
        require(settings.logLevel.uppercase() in LOG_LEVELS) { "Log level must be DEBUG, INFO, WARNING, or ERROR" }
    }

    private fun validateTransition(from: LaneState, to: LaneState) {
        if (from == to) return
        val allowed = when (from) {
            LaneState.STOPPED -> setOf(LaneState.STARTING)
            LaneState.STARTING -> setOf(LaneState.RUNNING, LaneState.STOPPING, LaneState.STOPPED, LaneState.ERROR)
            LaneState.RUNNING -> setOf(LaneState.STOPPING, LaneState.STOPPED, LaneState.ERROR)
            LaneState.STOPPING -> setOf(LaneState.STOPPED, LaneState.ERROR)
            LaneState.ERROR -> setOf(LaneState.STARTING, LaneState.STOPPING, LaneState.STOPPED)
        }
        check(to in allowed) { "Invalid node lane transition: $from -> $to" }
    }

    private companion object {
        const val SENDER_STATS_INTERVAL_MS = 250L
        val IPV4_PATTERN = Regex("^(\\d{1,3}\\.){3}\\d{1,3}$")
        val LOG_LEVELS = setOf("DEBUG", "INFO", "WARNING", "ERROR")
    }
}
