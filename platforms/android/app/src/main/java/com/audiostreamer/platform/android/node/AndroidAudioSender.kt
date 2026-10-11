package com.audiostreamer.platform.android.node

import android.util.Log
import com.audiostreamer.audio_plane.transport.SenderDiagnostics
import com.audiostreamer.audio_plane.transport.UdpSender
import java.util.concurrent.atomic.AtomicBoolean
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.Job
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.cancel
import kotlinx.coroutines.channels.Channel
import kotlinx.coroutines.launch

/** Bounded handoff between the MediaProjection callback and the Audio Plane UDP sender. */
internal class AndroidAudioSender(
    private val udpSender: UdpSender,
    private val onError: (Throwable) -> Unit,
) {
    private class PcmFrame(val bytes: ByteArray) {
        var length = 0
        var sampleRate = 0
        var channels = 2
    }

    private val scope = CoroutineScope(SupervisorJob() + Dispatchers.IO)
    private val queue = Channel<PcmFrame>(capacity = QUEUE_CAPACITY)
    private val freeFrames = Channel<PcmFrame>(capacity = POOL_SIZE)
    private val closed = AtomicBoolean(false)
    private val worker: Job

    init {
        repeat(POOL_SIZE) { freeFrames.trySend(PcmFrame(ByteArray(MAX_FRAME_BYTES))) }
        worker = scope.launch {
            try {
                for (frame in queue) {
                    try {
                        udpSender.sendPcm(frame.bytes, frame.length, frame.sampleRate, frame.channels)
                    } finally {
                        freeFrames.trySend(frame)
                    }
                }
            } catch (cancelled: CancellationException) {
                throw cancelled
            } catch (error: Exception) {
                Log.e(TAG, "audio_sender_failed", error)
                onError(error)
            } finally {
                queue.close()
                udpSender.close()
                scope.cancel()
            }
        }
    }

    fun enqueuePcm(payload: ByteArray, length: Int, sampleRate: Int, channels: Int) {
        check(!closed.get()) { "Audio sender is closed" }
        require(length in 1..MAX_FRAME_BYTES && length <= payload.size && sampleRate > 0 && channels == 2)
        var frame = freeFrames.tryReceive().getOrNull()
        if (frame == null) {
            // The pool is exhausted only when the queue is full and one frame is in flight.
            val oldest = queue.tryReceive().getOrNull()
            if (oldest != null) {
                freeFrames.trySend(oldest)
                frame = freeFrames.tryReceive().getOrNull()
            }
        }
        if (frame == null) return
        payload.copyInto(frame.bytes, 0, 0, length)
        frame.length = length
        frame.sampleRate = sampleRate
        frame.channels = channels
        if (queue.trySend(frame).isFailure) freeFrames.trySend(frame)
    }

    fun diagnostics(sampleRate: Int): SenderDiagnostics = udpSender.diagnostics(sampleRate)

    suspend fun closeAndJoin() {
        if (closed.compareAndSet(false, true)) queue.close()
        worker.join()
    }

    private companion object {
        const val TAG = "AudioStreamerSender"
        const val QUEUE_CAPACITY = 8
        const val POOL_SIZE = QUEUE_CAPACITY + 1
        const val MAX_FRAME_BYTES = 480 * 2 * Float.SIZE_BYTES
    }
}
