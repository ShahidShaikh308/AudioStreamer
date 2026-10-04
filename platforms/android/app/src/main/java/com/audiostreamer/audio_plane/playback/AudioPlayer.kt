package com.audiostreamer.audio_plane.playback

import android.media.AudioAttributes
import android.media.AudioFormat
import android.media.AudioTrack
import android.os.Build
import android.os.Process
import android.os.SystemClock
import android.util.Log
import com.audiostreamer.audio_plane.buffering.AudioPacketQueue
import com.audiostreamer.audio_plane.protocol.AudioPacket
import com.audiostreamer.audio_plane.protocol.ProtocolSpec
import com.audiostreamer.audio_plane.protocol.SampleFormat
import java.util.concurrent.Executors
import java.util.concurrent.atomic.AtomicBoolean
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.CoroutineDispatcher
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.ExecutorCoroutineDispatcher
import kotlinx.coroutines.Job
import kotlinx.coroutines.NonCancellable
import kotlinx.coroutines.asCoroutineDispatcher
import kotlinx.coroutines.cancelAndJoin
import kotlinx.coroutines.launch
import kotlinx.coroutines.sync.Mutex
import kotlinx.coroutines.withContext
import kotlinx.coroutines.withTimeoutOrNull

private const val DEFAULT_TARGET_QUEUE_MS = 80L

data class PlaybackDiagnostics(
    val bufferCapacityFrames: Int = 0,
    val queuedFrames: Long = 0,
    val queuedPackets: Int = 0,
    val queuedPacketDurationMs: Long = 0,
    val targetQueueDurationMs: Long = DEFAULT_TARGET_QUEUE_MS,
    val droppedPackets: Long = 0,
)

/** Plays protocol PCM on a dedicated audio-priority thread with a time-bounded queue. */
class AudioPlayer(
    private val dispatcher: CoroutineDispatcher? = null,
    private val targetQueueDurationMs: Long = DEFAULT_TARGET_QUEUE_MS,
) {
    private val playbackDispatcher: CoroutineDispatcher = dispatcher ?: Executors
        .newSingleThreadExecutor { runnable ->
            Thread({
                runCatching { Process.setThreadPriority(Process.THREAD_PRIORITY_AUDIO) }
                runnable.run()
            }, "AudioTrackWriter").apply { isDaemon = true }
        }
        .asCoroutineDispatcher()
    private val ownsDispatcher = dispatcher == null
    private val closed = AtomicBoolean(false)
    private val closeMutex = Mutex()
    private val packetQueue = AudioPacketQueue(targetQueueDurationMs)

    @Volatile private var track: AudioTrack? = null
    @Volatile private var playbackJob: Job? = null
    private var activeFormat: Pair<Int, Int>? = null
    private var framesWritten = 0L
    private var lastDiagnosticsAt = 0L
    private val sampleScratch = FloatArray(MAX_SAMPLES_PER_DATAGRAM)

    fun enqueue(packet: AudioPacket): Boolean = packetQueue.offer(packet)

    @Synchronized
    fun start(
        scope: CoroutineScope,
        onDiagnostics: (PlaybackDiagnostics) -> Unit,
        onError: (Throwable) -> Unit,
    ): Job {
        check(!closed.get()) { "Audio player is closed" }
        check(playbackJob == null) { "Audio player is already started" }
        val job = scope.launch(playbackDispatcher) {
            try {
                consumePackets(onDiagnostics)
            } catch (cancelled: CancellationException) {
                throw cancelled
            } catch (error: Exception) {
                runCatching { onError(error) }
            }
        }
        playbackJob = job
        return job
    }

    private suspend fun consumePackets(onDiagnostics: (PlaybackDiagnostics) -> Unit) {
        while (true) {
            val packet = withTimeoutOrNull(DIAGNOSTICS_INTERVAL_MS) {
                packetQueue.receive()
            }
            if (packet == null) {
                if (closed.get()) break
                reportDiagnostics(onDiagnostics, force = true)
                continue
            }
            try {
                ensureTrack(packet.sampleRate, packet.channels)
                writePacket(packet)
            } catch (error: Exception) {
                Log.e(
                    TAG,
                    "PLAYBACK_FAILURE stream=${packet.streamId} sequence=${packet.sequenceNumber} " +
                        "sampleRate=${packet.sampleRate} channels=${packet.channels} " +
                        "payloadBytes=${packet.payload.size}",
                    error,
                )
                throw error
            }
            reportDiagnostics(onDiagnostics)
        }
    }

    private fun reportDiagnostics(
        callback: (PlaybackDiagnostics) -> Unit,
        force: Boolean = false,
    ) {
        val now = SystemClock.elapsedRealtime()
        if (!force && now - lastDiagnosticsAt < DIAGNOSTICS_INTERVAL_MS) return
        lastDiagnosticsAt = now
        callback(currentDiagnostics())
    }

    private fun ensureTrack(sampleRate: Int, channels: Int) {
        require(channels == 2) { "Protocol v3 receiver supports stereo only" }
        if (activeFormat == (sampleRate to channels) && track?.state == AudioTrack.STATE_INITIALIZED) {
            return
        }

        releaseTrack()
        val channelConfig = AudioFormat.CHANNEL_OUT_STEREO
        val encoding = AudioFormat.ENCODING_PCM_FLOAT
        val minBufferBytes = AudioTrack.getMinBufferSize(sampleRate, channelConfig, encoding)
        require(minBufferBytes > 0) {
            "AudioTrack rejected $sampleRate Hz float stereo format ($minBufferBytes)"
        }

        val attributes = AudioAttributes.Builder()
            .setUsage(AudioAttributes.USAGE_MEDIA)
            .setContentType(AudioAttributes.CONTENT_TYPE_MUSIC)
            .build()
        val format = AudioFormat.Builder()
            .setEncoding(encoding)
            .setSampleRate(sampleRate)
            .setChannelMask(channelConfig)
            .build()
        val builder = AudioTrack.Builder()
            .setAudioAttributes(attributes)
            .setAudioFormat(format)
            .setTransferMode(AudioTrack.MODE_STREAM)
            .setBufferSizeInBytes(minBufferBytes)

        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.O) {
            builder.setPerformanceMode(AudioTrack.PERFORMANCE_MODE_LOW_LATENCY)
        }

        val newTrack = builder.build()
        track = newTrack
        try {
            check(newTrack.state == AudioTrack.STATE_INITIALIZED) {
                "AudioTrack initialization failed"
            }
            newTrack.play()
            activeFormat = sampleRate to channels
            framesWritten = 0L
        } catch (error: Exception) {
            releaseTrack()
            throw error
        }
    }

    private fun writePacket(packet: AudioPacket) {
        require(packet.sampleFormat == SampleFormat.FLOAT32_LE) {
            "AudioTrack requires FLOAT32_LE payloads"
        }
        val output = track ?: error("AudioTrack is not initialized")
        require(packet.payload.size % (packet.channels * Float.SIZE_BYTES) == 0) {
            "PCM payload is not aligned to complete stereo frames"
        }
        val sampleCount = packet.payload.size / Float.SIZE_BYTES
        require(sampleCount <= sampleScratch.size) { "PCM payload exceeds the supported datagram size" }
        val payload = packet.payload
        for (sampleIndex in 0 until sampleCount) {
            val offset = sampleIndex * Float.SIZE_BYTES
            val bits = (payload[offset].toInt() and 0xFF) or
                ((payload[offset + 1].toInt() and 0xFF) shl 8) or
                ((payload[offset + 2].toInt() and 0xFF) shl 16) or
                ((payload[offset + 3].toInt() and 0xFF) shl 24)
            sampleScratch[sampleIndex] = Float.fromBits(bits)
        }

        var sampleOffset = 0
        while (sampleOffset < sampleCount) {
            val written = try {
                output.write(
                    sampleScratch,
                    sampleOffset,
                    sampleCount - sampleOffset,
                    AudioTrack.WRITE_BLOCKING,
                )
            } catch (error: RuntimeException) {
                Log.e(
                    TAG,
                    "DROP reason=audiotrack_write_exception stream=${packet.streamId} " +
                        "sequence=${packet.sequenceNumber} sampleOffset=$sampleOffset " +
                        "sampleCount=$sampleCount",
                    error,
                )
                throw error
            }
            if (written <= 0) {
                Log.e(
                    TAG,
                    "DROP reason=audiotrack_write_failure stream=${packet.streamId} " +
                        "sequence=${packet.sequenceNumber} code=$written sampleOffset=$sampleOffset " +
                        "sampleCount=$sampleCount",
                )
                error("AudioTrack.write failed with code $written")
            }
            sampleOffset += written
            framesWritten += written / packet.channels
        }
    }

    private fun currentDiagnostics(): PlaybackDiagnostics {
        val currentTrack = track
        val capacityFrames = currentTrack?.bufferSizeInFrames ?: 0
        val playedFrames = currentTrack?.playbackHeadPosition?.toLong()?.and(0xFFFF_FFFFL) ?: 0L
        val writtenModulo = framesWritten and 0xFFFF_FFFFL
        val queuedFrames = ((writtenModulo - playedFrames) and 0xFFFF_FFFFL)
            .coerceAtMost(capacityFrames.toLong())
        return PlaybackDiagnostics(
            bufferCapacityFrames = capacityFrames,
            queuedFrames = queuedFrames,
            queuedPackets = packetQueue.queuedPackets(),
            queuedPacketDurationMs = packetQueue.queuedDurationMs(),
            targetQueueDurationMs = targetQueueDurationMs,
            droppedPackets = packetQueue.droppedPackets(),
        )
    }

    /** Stop accepting audio, join the writer, then release AudioTrack on its owner thread. */
    suspend fun closeAndJoin() {
        withContext(NonCancellable) {
            closeMutex.lock()
            try {
                val shouldClose = synchronized(this@AudioPlayer) {
                    closed.compareAndSet(false, true).also {
                        if (it) packetQueue.close()
                    }
                }
                if (shouldClose) {
                    playbackJob?.cancelAndJoin()
                    withContext(playbackDispatcher) { releaseTrack() }
                    if (ownsDispatcher) {
                        (playbackDispatcher as ExecutorCoroutineDispatcher).close()
                    }
                }
            } finally {
                closeMutex.unlock()
            }
        }
    }

    private fun releaseTrack() {
        val oldTrack = track
        track = null
        activeFormat = null
        if (oldTrack != null) {
            runCatching { oldTrack.pause() }
            runCatching { oldTrack.flush() }
            runCatching { oldTrack.stop() }
            runCatching { oldTrack.release() }
        }
    }

    private companion object {
        const val TAG = "AudioStreamerPlayback"
        const val DIAGNOSTICS_INTERVAL_MS = 250L
        const val MAX_SAMPLES_PER_DATAGRAM = ProtocolSpec.MAX_DATAGRAM_SIZE / Float.SIZE_BYTES
    }
}
