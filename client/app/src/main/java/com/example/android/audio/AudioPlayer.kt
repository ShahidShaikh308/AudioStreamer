package com.example.android.audio

import android.media.AudioAttributes
import android.media.AudioFormat
import android.media.AudioTrack
import android.os.Build
import android.os.Process
import com.example.android.network.AudioPacket
import com.example.android.network.PacketType
import com.example.android.network.SampleFormat
import java.nio.ByteBuffer
import java.nio.ByteOrder
import java.util.concurrent.atomic.AtomicInteger
import java.util.concurrent.atomic.AtomicLong
import java.util.concurrent.Executors
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.CoroutineDispatcher
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.ExecutorCoroutineDispatcher
import kotlinx.coroutines.Job
import kotlinx.coroutines.asCoroutineDispatcher
import kotlinx.coroutines.channels.BufferOverflow
import kotlinx.coroutines.channels.Channel
import kotlinx.coroutines.launch

data class PlaybackDiagnostics(
    val bufferCapacityFrames: Int = 0,
    val queuedFrames: Long = 0,
    val queuedPackets: Int = 0,
    val droppedPackets: Long = 0,
)

/** Streams protocol PCM through AudioTrack on a dedicated background dispatcher. */
class AudioPlayer(
    private val dispatcher: CoroutineDispatcher? = null,
    private val packetQueueCapacity: Int = 8,
    private val onQueueDrop: () -> Unit = {},
) {
    private val playbackDispatcher: CoroutineDispatcher = dispatcher ?: Executors
        .newSingleThreadExecutor { runnable ->
            Thread({
                runCatching { Process.setThreadPriority(Process.THREAD_PRIORITY_AUDIO) }
                runnable.run()
            }, "AudioTrackWriter").apply { isDaemon = true }
        }
        .asCoroutineDispatcher()
    private val queuedPackets = AtomicInteger(0)
    private val droppedPackets = AtomicLong(0)
    private val packets = Channel<AudioPacket>(
        capacity = packetQueueCapacity,
        onBufferOverflow = BufferOverflow.DROP_OLDEST,
        onUndeliveredElement = {
            queuedPackets.decrementAndGet()
            droppedPackets.incrementAndGet()
            onQueueDrop()
        },
    )

    @Volatile private var track: AudioTrack? = null
    private var activeFormat: Pair<Int, Int>? = null
    private var framesWritten = 0L
    private var lastDiagnosticsAt = 0L

    init {
        require(packetQueueCapacity > 0) { "packet queue capacity must be positive" }
    }

    /** Adds a packet without blocking the network receive loop. Old audio is dropped on overflow. */
    fun enqueue(packet: AudioPacket): Boolean {
        queuedPackets.incrementAndGet()
        val result = packets.trySend(packet)
        if (result.isFailure) queuedPackets.decrementAndGet()
        return result.isSuccess
    }

    fun start(
        scope: CoroutineScope,
        onDiagnostics: (PlaybackDiagnostics) -> Unit,
        onError: (Throwable) -> Unit,
    ): Job = scope.launch(playbackDispatcher) {
        try {
            for (packet in packets) {
                queuedPackets.decrementAndGet()
                if (packet.packetType != PacketType.AUDIO || packet.sampleFormat != SampleFormat.FLOAT32_LE) {
                    continue
                }
                ensureTrack(packet.sampleRate, packet.channels)
                writePacket(packet)

                val now = android.os.SystemClock.elapsedRealtime()
                if (now - lastDiagnosticsAt >= 250) {
                    onDiagnostics(currentDiagnostics())
                    lastDiagnosticsAt = now
                }
            }
        } catch (cancelled: CancellationException) {
            throw cancelled
        } catch (error: Throwable) {
            onError(error)
        }
    }

    private fun ensureTrack(sampleRate: Int, channels: Int) {
        require(channels == 2) { "Android POC supports stereo audio only" }
        if (activeFormat == (sampleRate to channels) && track?.state == AudioTrack.STATE_INITIALIZED) {
            return
        }

        releaseTrack()
        val channelConfig = AudioFormat.CHANNEL_OUT_STEREO
        val encoding = AudioFormat.ENCODING_PCM_FLOAT
        val minBufferBytes = AudioTrack.getMinBufferSize(sampleRate, channelConfig, encoding)
        require(minBufferBytes > 0) { "AudioTrack rejected $sampleRate Hz float stereo format ($minBufferBytes)" }

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
        check(newTrack.state == AudioTrack.STATE_INITIALIZED) { "AudioTrack initialization failed" }
        newTrack.play()
        track = newTrack
        activeFormat = sampleRate to channels
        framesWritten = 0L
    }

    private fun writePacket(packet: AudioPacket) {
        val output = track ?: error("AudioTrack is not initialized")
        require(packet.payload.size % (packet.channels * Float.SIZE_BYTES) == 0) {
            "PCM payload is not aligned to complete stereo frames"
        }
        val sampleCount = packet.payload.size / Float.SIZE_BYTES
        val samples = FloatArray(sampleCount)
        ByteBuffer.wrap(packet.payload)
            .order(ByteOrder.LITTLE_ENDIAN)
            .asFloatBuffer()
            .get(samples)

        var sampleOffset = 0
        while (sampleOffset < samples.size) {
            val written = output.write(
                samples,
                sampleOffset,
                samples.size - sampleOffset,
                AudioTrack.WRITE_BLOCKING,
            )
            check(written > 0) { "AudioTrack.write failed with code $written" }
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
            queuedPackets = queuedPackets.get(),
            droppedPackets = droppedPackets.get(),
        )
    }

    fun close() {
        packets.close()
        releaseTrack()
        if (dispatcher == null) {
            (playbackDispatcher as ExecutorCoroutineDispatcher).close()
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
            oldTrack.release()
        }
    }
}
