package com.audiostreamer.platform.android.node

import android.os.SystemClock
import android.os.Handler
import android.os.Looper
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
import com.audiostreamer.core.node.NodeStatus
import com.audiostreamer.core.node.aggregateStatus
import com.audiostreamer.control_plane.ControlManager
import com.audiostreamer.control_plane.ControlEvent
import com.audiostreamer.control_plane.ErrorPacket
import com.audiostreamer.control_plane.NodeCapability
import com.audiostreamer.control_plane.NodeStatus as ControlNodeStatus
import com.audiostreamer.control_plane.StartStreamPacket
import com.audiostreamer.control_plane.StopStreamPacket
import com.audiostreamer.control_plane.StreamState
import com.audiostreamer.control_plane.StreamStatusPacket
import com.audiostreamer.platform.android.capture.AndroidPlaybackCapture
import com.audiostreamer.platform.android.capture.CaptureCallback
import java.net.NetworkInterface
import java.util.Collections
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.CoroutineStart
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.Job
import kotlinx.coroutines.NonCancellable
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.cancel
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext
import kotlin.coroutines.coroutineContext

/** Android INode implementation. UI and future control code interact only through this contract. */
class AndroidNode(activity: ComponentActivity) : INode {
    private val lock = Any()
    private val configStore = AndroidNodeConfigStore(activity.applicationContext)
    private var currentSettings = configStore.load()
    private var logger = AndroidNodeLogger(currentSettings.logLevel)
    private val capture = AndroidPlaybackCapture(activity)
    private val localNetworkPermission = LocalNetworkPermission(activity)
    private val mainHandler = Handler(Looper.getMainLooper())
    private val nodeScope = CoroutineScope(SupervisorJob() + Dispatchers.IO)
    private val mutableState = MutableStateFlow(
        NodeSnapshot(
            nodeName = currentSettings.nodeName,
            localIp = "Detecting network…",
            controlStatus = "Waiting for local-network permission",
        ),
    )
    private val controlManager = ControlManager(
        nodeId = currentSettings.nodeId,
        nodeName = { settings.nodeName },
        platform = "Android",
        capabilities = ::controlCapabilities,
        receivePort = { settings.receivePort },
        controlPort = { settings.controlPort },
        controllerSecretHex = { settings.controllerSecretHex },
        statusProvider = ::controlStatus,
        commandHandler = ::dispatchControlCommand,
        onEvent = ::onControlEvent,
    )
    private var controlPermissionDenied = false
    private var sender: AndroidAudioSender? = null
    private var senderSetupJob: Job? = null
    private var senderCleanupJob: Job? = null
    private var receiver: UdpReceiver? = null
    private var receiverJob: Job? = null
    private var player: AudioPlayer? = null
    private var receiverCleanupJob: Job? = null
    private var captureRequested = false
    @Volatile private var activeStreamPair: Pair<Long, Long>? = null
    @Volatile private var activeTransactionId: Int? = null
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
        nodeScope.launch {
            val localIp = findLocalIp()
            setState { it.copy(localIp = localIp) }
        }
        if (!capabilities.canCapture) {
            mutableState.value = mutableState.value.copy(captureStatus = "Requires Android 10 or newer")
        }
        logger.info("node_started name=${currentSettings.nodeName} localIp=${mutableState.value.localIp} protocol=v3")
        localNetworkPermission.runWhenAllowed(
            action = {
                runCatching { controlManager.start() }
                    .onSuccess { setState { it.copy(controlStatus = "Discovering peers") } }
                    .onFailure { logger.error("control_start_failed", it); setState { it.copy(controlStatus = "Control Plane error") } }
            },
            onDenied = {
                controlPermissionDenied = true
                setState { it.copy(controlStatus = "Local-network permission denied") }
            },
        )
    }

    override fun updateSettings(settings: NodeSettings) {
        validateSettings(settings)
        synchronized(lock) {
            check(!shutdownRequested) { "Node is shutting down" }
            require(settings.nodeId == currentSettings.nodeId) { "Node ID is permanent and cannot be changed" }
            currentSettings = settings.copy(logLevel = settings.logLevel.uppercase())
            logger = AndroidNodeLogger(currentSettings.logLevel)
            mutableState.value = mutableState.value.copy(nodeName = currentSettings.nodeName)
        }
        controlManager.updateLocal()
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
            if (shutdownRequested || captureRequested || senderCleanupJob?.isActive == true ||
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
                synchronized(lock) {
                    if (shutdownRequested || senderLane() != LaneState.STARTING) return@runWhenAllowed
                }
                val config = settings
                val setup = nodeScope.launch(start = CoroutineStart.LAZY) {
                    var candidate: AndroidAudioSender? = null
                    try {
                        candidate = withContext(NonCancellable + Dispatchers.IO) {
                            AndroidAudioSender(
                                UdpSender(config.destinationIp, config.destinationPort),
                            ) { error -> mainHandler.post { senderFailure(error) } }
                        }
                        withContext(Dispatchers.Main.immediate) {
                            val install = synchronized(lock) {
                                if (shutdownRequested || senderLane() != LaneState.STARTING) false
                                else {
                                    sender = candidate
                                    candidate = null
                                    captureRequested = true
                                    true
                                }
                            }
                            if (install) capture.start(senderCaptureCallback)
                        }
                    } catch (cancelled: CancellationException) {
                        throw cancelled
                    } catch (error: Exception) {
                        withContext(Dispatchers.Main.immediate) { senderFailure(error) }
                    } finally {
                        candidate?.let { active ->
                            withContext(NonCancellable + Dispatchers.IO) { active.closeAndJoin() }
                        }
                        synchronized(lock) {
                            if (senderSetupJob === coroutineContext[Job]) senderSetupJob = null
                        }
                        maybeFinishShutdown()
                    }
                }
                synchronized(lock) { senderSetupJob = setup }
                setup.start()
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
                senderSetupJob?.cancel()
                senderSetupJob = null
                val abandonedSender = sender
                sender = null
                closeSenderAsync(abandonedSender)
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
            val abandonedSender: AndroidAudioSender?
            synchronized(lock) {
                abandonedSender = sender
                sender = null
                setSenderLaneLocked(LaneState.STOPPED)
            }
            closeSenderAsync(abandonedSender)
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

    override fun startStream(senderNodeId: Long, receiverNodeId: Long) {
        controlManager.sendStartStream(senderNodeId, receiverNodeId)
    }

    override fun stopStream(senderNodeId: Long, receiverNodeId: Long) {
        controlManager.sendStopStream(senderNodeId, receiverNodeId)
    }

    override fun shutdown() {
        synchronized(lock) { shutdownRequested = true }
        logger.info("node_shutdown_requested")
        controlManager.close()
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
                val activeSender = synchronized(lock) { sender } ?: return
                activeSender.enqueuePcm(payload, length, sampleRate, channels)
                val now = SystemClock.elapsedRealtime()
                if (now - lastSenderDiagnosticsAt >= SENDER_STATS_INTERVAL_MS) {
                    lastSenderDiagnosticsAt = now
                    synchronized(lock) {
                        if (sender === activeSender) {
                            setStateLocked {
                                it.copy(senderDiagnostics = activeSender.diagnostics(sampleRate), sampleRate = sampleRate)
                            }
                        }
                    }
                }
            } catch (error: Exception) {
                logger.warning("sender_network_error", error)
                setError(error)
                mainHandler.post { senderFailure(error) }
            }
        }

        override fun onStopped() {
            val stoppedSender: AndroidAudioSender?
            synchronized(lock) {
                captureRequested = false
                stoppedSender = sender
                sender = null
                if (senderLane() != LaneState.ERROR) setSenderLaneLocked(LaneState.STOPPED)
                setStateLocked { it.copy(captureStatus = "Stopped") }
            }
            logger.info("capture_stopped")
            logger.info("sender_stopped")
            closeSenderAsync(stoppedSender)
            maybeFinishShutdown()
        }

        override fun onError(error: Throwable) { mainHandler.post { senderFailure(error) } }
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
        if (Looper.myLooper() != Looper.getMainLooper()) {
            mainHandler.post { senderFailure(error) }
            return
        }
        val failedSender: AndroidAudioSender?
        synchronized(lock) {
            if (senderLane() in setOf(LaneState.STOPPED, LaneState.STOPPING)) return
            failedSender = sender
            sender = null
            setSenderLaneLocked(LaneState.ERROR)
            setStateLocked { it.copy(captureStatus = "Error", error = error.message ?: error.javaClass.simpleName) }
        }
        closeSenderAsync(failedSender)
        capture.stop()
    }

    private fun closeSenderAsync(activeSender: AndroidAudioSender?) {
        if (activeSender == null) return
        synchronized(lock) {
            if (senderCleanupJob?.isActive == true) return
            val cleanup = nodeScope.launch(Dispatchers.IO, start = CoroutineStart.LAZY) {
                try {
                    activeSender.closeAndJoin()
                } catch (error: Exception) {
                    logger.error("sender_cleanup_error", error)
                } finally {
                    synchronized(lock) { senderCleanupJob = null }
                    maybeFinishShutdown()
                }
            }
            senderCleanupJob = cleanup
            cleanup.start()
        }
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

    private fun dispatchControlCommand(packet: com.audiostreamer.control_plane.ControlPacket) {
        mainHandler.post {
            runCatching { handleControlCommand(packet) }
                .onFailure { error ->
                    logger.error("control_command_failed", error)
                    setError(error)
                    val pair = activeStreamPair
                    val transactionId = activeTransactionId
                    if (pair != null && transactionId != null) {
                        controlManager.reportError(currentSettings.nodeId, 1,
                            error.message ?: "command failed", transactionId, pair.first, pair.second)
                    }
                }
        }
    }

    private fun handleControlCommand(packet: com.audiostreamer.control_plane.ControlPacket) {
        if (shutdownRequested) return
        val senderId: Long
        val receiverId: Long
        when (packet) {
            is StartStreamPacket -> { senderId = packet.senderNodeId; receiverId = packet.receiverNodeId }
            is StopStreamPacket -> { senderId = packet.senderNodeId; receiverId = packet.receiverNodeId }
            else -> return
        }
        if (currentSettings.nodeId != senderId && currentSettings.nodeId != receiverId) return
        activeStreamPair = senderId to receiverId
        activeTransactionId = when (packet) {
            is StartStreamPacket -> packet.transactionId
            is StopStreamPacket -> packet.transactionId
            else -> null
        }
        if (packet is StartStreamPacket) {
            if (currentSettings.nodeId == senderId) {
                check(capabilities.canCapture) { "This Android version cannot capture mixed playback" }
                val peer = controlManager.registry.get(receiverId) ?: error("Receiver node is no longer online")
                check((peer.capabilities and NodeCapability.RECEIVER) != 0) { "Selected node does not advertise receiver capability" }
                updateSettings(settings.copy(destinationIp = peer.address, destinationPort = peer.receivePort))
                startSender()
            }
            if (currentSettings.nodeId == receiverId) {
                val peer = controlManager.registry.get(senderId)
                check(peer == null || (peer.capabilities and NodeCapability.SENDER) != 0) {
                    "Selected node does not advertise sender capability"
                }
                startReceiver()
            }
        } else if (packet is StopStreamPacket) {
            if (currentSettings.nodeId == senderId) stopSender()
            if (currentSettings.nodeId == receiverId) stopReceiver()
        }
        val lane = if (currentSettings.nodeId == senderId) senderLane() else receiverLane()
        val streamState = when {
            lane == LaneState.RUNNING -> StreamState.RUNNING
            lane == LaneState.STOPPED && packet is StopStreamPacket -> StreamState.STOPPED
            else -> null
        }
        streamState?.let { state -> controlManager.reportStreamStatus(senderId, receiverId,
            state, transactionId = activeTransactionId) }
    }

    private fun onControlEvent(event: ControlEvent) {
        when (event) {
            is ControlEvent.NodeDiscovered -> logger.info("node_discovered id=${event.node.nodeId} name=${event.node.name} ip=${event.node.address}")
            is ControlEvent.NodeLost -> logger.info("node_lost id=${event.node.nodeId} name=${event.node.name}")
            is ControlEvent.Error -> logger.warning("control_error ${event.message}")
            is ControlEvent.Stream -> when (val packet = event.packet) {
                is StreamStatusPacket -> logger.info("stream_status sender=${packet.senderNodeId} receiver=${packet.receiverNodeId} state=${packet.state} code=${packet.errorCode}")
                is ErrorPacket -> logger.error("peer_error nodeId=${packet.nodeId} code=${packet.errorCode} message=${packet.message}")
                else -> Unit
            }
            else -> Unit
        }
        setState { state ->
            state.copy(
                knownNodes = controlManager.registry.snapshot(),
                controlStatus = when {
                    controlPermissionDenied -> "Local-network permission denied"
                    controlManager.registry.snapshot().isEmpty() -> "Discovering peers"
                    else -> "${controlManager.registry.snapshot().size} peer(s) online"
                },
            )
        }
    }

    private fun controlCapabilities(): Int {
        var mask = 0
        if (capabilities.canCapture) mask = mask or NodeCapability.CAPTURE
        if (capabilities.canPlay) mask = mask or NodeCapability.PLAYBACK
        if (capabilities.canSend) mask = mask or NodeCapability.SENDER
        if (capabilities.canReceive) mask = mask or NodeCapability.RECEIVER
        return mask
    }

    private fun controlStatus(): ControlNodeStatus {
        val state = mutableState.value
        return when {
            state.status == NodeStatus.ERROR -> ControlNodeStatus.ERROR
            state.sender in setOf(LaneState.STARTING, LaneState.STOPPING) ||
                state.receiver in setOf(LaneState.STARTING, LaneState.STOPPING) -> ControlNodeStatus.BUSY
            state.sender == LaneState.RUNNING || state.receiver == LaneState.RUNNING -> ControlNodeStatus.STREAMING
            else -> ControlNodeStatus.ONLINE
        }
    }

    private fun setSenderLaneLocked(state: LaneState) {
        val current = mutableState.value.sender
        validateTransition(current, state)
        setStateLocked { it.copy(sender = state) }
        reportStreamState(state)
    }

    private fun setReceiverLaneLocked(state: LaneState) {
        val current = mutableState.value.receiver
        validateTransition(current, state)
        setStateLocked { it.copy(receiver = state) }
        reportStreamState(state)
    }

    private fun reportStreamState(state: LaneState) {
        val pair = activeStreamPair ?: return
        val streamState = when (state) {
            LaneState.RUNNING -> StreamState.RUNNING
            LaneState.STOPPED -> StreamState.STOPPED
            LaneState.ERROR -> StreamState.FAILED
            else -> return
        }
        controlManager.reportStreamStatus(pair.first, pair.second, streamState,
            errorCode = if (state == LaneState.ERROR) 1 else 0,
            transactionId = activeTransactionId)
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
            shutdownRequested && receiverCleanupJob == null && senderCleanupJob == null && senderSetupJob == null &&
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
        require(settings.nodeName.none { it == '\n' || it == '\r' || it == '\u0000' }) { "Node name must be a single line" }
        require(settings.nodeName.toByteArray(Charsets.UTF_8).size <= 255) { "Node name must fit the 255-byte Control Protocol v1 name limit" }
        require(settings.destinationIp.matches(IPV4_PATTERN)) { "Destination must be an IPv4 address" }
        require(settings.destinationIp.split('.').all { it.toIntOrNull() in 0..255 }) { "Destination IPv4 address is invalid" }
        require(settings.destinationPort in 1..65535) { "Destination port must be 1..65535" }
        require(settings.receivePort in 1..65535) { "Receive port must be 1..65535" }
        require(settings.controlPort in 49152..65535) { "Control port must be 49152..65535" }
        if (settings.controllerSecretHex.isNotBlank())
            com.audiostreamer.control_plane.ControlPacketCodec.parseControllerSecret(settings.controllerSecretHex)
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
