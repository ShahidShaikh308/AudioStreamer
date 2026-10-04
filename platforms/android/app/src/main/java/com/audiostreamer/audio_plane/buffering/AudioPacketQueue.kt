package com.audiostreamer.audio_plane.buffering

import android.util.Log
import com.audiostreamer.audio_plane.protocol.AudioPacket
import java.util.ArrayDeque
import kotlinx.coroutines.channels.Channel

/** A low-latency FIFO bounded by actual queued PCM duration, rather than packet count. */
internal class AudioPacketQueue(targetBufferMs: Long) {
    private val targetDurationNs = targetBufferMs * NANOS_PER_MILLI
    private val lock = Any()
    private val packets = ArrayDeque<AudioPacket>()
    private val available = Channel<Unit>(Channel.CONFLATED)
    private var queuedDurationNs = 0L
    private var droppedPacketCount = 0L
    private var isClosed = false

    init {
        require(targetBufferMs > 0) { "target playback buffer must be positive" }
    }

    /** Enqueue the packet, evicting oldest audio until the time budget is satisfied. */
    fun offer(packet: AudioPacket): Boolean {
        val packetDurationNs = durationNs(packet)
        synchronized(lock) {
            if (isClosed) {
                logDrop("playback_queue_closed", packet)
                return false
            }
            if (packetDurationNs > targetDurationNs) {
                droppedPacketCount++
                logDrop("packet_exceeds_duration_budget", packet)
                return false
            }

            while (packets.isNotEmpty() && queuedDurationNs + packetDurationNs > targetDurationNs) {
                val evicted = packets.removeFirst()
                queuedDurationNs -= durationNs(evicted)
                droppedPacketCount++
                logDrop("playback_queue_duration_limit", evicted)
            }
            packets.addLast(packet)
            queuedDurationNs += packetDurationNs
            available.trySend(Unit)
            return true
        }
    }

    suspend fun receive(): AudioPacket? {
        while (true) {
            val packet = synchronized(lock) {
                when {
                    packets.isNotEmpty() -> {
                        val next = packets.removeFirst()
                        queuedDurationNs -= durationNs(next)
                        next
                    }
                    isClosed -> return null
                    else -> null
                }
            }
            if (packet != null) return packet
            if (available.receiveCatching().isFailure) return null
        }
    }

    fun queuedPackets(): Int = synchronized(lock) { packets.size }

    fun queuedDurationMs(): Long = synchronized(lock) {
        queuedDurationNs / NANOS_PER_MILLI
    }

    fun droppedPackets(): Long = synchronized(lock) { droppedPacketCount }

    /** Clear pending audio and wake the consumer during orderly shutdown. */
    fun close() {
        synchronized(lock) {
            if (isClosed) return
            isClosed = true
            while (packets.isNotEmpty()) {
                logDrop("playback_queue_shutdown", packets.removeFirst())
            }
            queuedDurationNs = 0L
            available.close()
        }
    }

    private fun durationNs(packet: AudioPacket): Long {
        val frames = packet.payload.size / (packet.channels * Float.SIZE_BYTES)
        return frames * NANOS_PER_SECOND / packet.sampleRate
    }

    private fun logDrop(reason: String, packet: AudioPacket) {
        Log.w(
            TAG,
            "DROP reason=$reason stream=${packet.streamId} sequence=${packet.sequenceNumber} " +
                "targetBufferMs=${targetDurationNs / NANOS_PER_MILLI}",
        )
    }

    private companion object {
        const val TAG = "AudioStreamerPlayback"
        const val NANOS_PER_MILLI = 1_000_000L
        const val NANOS_PER_SECOND = 1_000_000_000L
    }
}
