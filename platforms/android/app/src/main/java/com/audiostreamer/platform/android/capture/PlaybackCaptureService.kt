package com.audiostreamer.platform.android.capture

import android.app.Notification
import android.app.NotificationChannel
import android.app.NotificationManager
import android.app.Service
import android.content.Intent
import android.content.pm.ServiceInfo
import android.media.AudioFormat
import android.media.AudioRecord
import android.media.AudioPlaybackCaptureConfiguration
import android.media.projection.MediaProjection
import android.media.projection.MediaProjectionManager
import android.os.Binder
import android.os.Build
import android.os.IBinder
import android.os.Process
import android.util.Log
import androidx.core.app.NotificationCompat
import java.util.concurrent.atomic.AtomicBoolean

/** Foreground owner for Android's user-authorized mixed-playback capture session. */
internal class PlaybackCaptureService : Service() {
    private val binder = LocalBinder()
    private val stopRequested = AtomicBoolean(false)
    @Volatile private var recorder: AudioRecord? = null
    @Volatile private var projection: MediaProjection? = null
    @Volatile private var worker: Thread? = null
    @Volatile private var captureCallback: CaptureCallback? = null
    private val stopNotified = AtomicBoolean(false)

    inner class LocalBinder : Binder() {
        fun beginCapture(callback: CaptureCallback) {
            this@PlaybackCaptureService.beginCapture(callback)
        }

        fun stopCapture() = this@PlaybackCaptureService.stopCapture()
    }

    override fun onBind(intent: Intent?): IBinder = binder

    override fun onStartCommand(intent: Intent?, flags: Int, startId: Int): Int {
        when (intent?.action) {
            ACTION_START -> initializeProjection(intent, startId)
        }
        return START_NOT_STICKY
    }

    private fun initializeProjection(intent: Intent, startId: Int) {
        try {
            createNotificationChannel()
            val notification = NotificationCompat.Builder(this, CHANNEL_ID)
                .setSmallIcon(android.R.drawable.ic_btn_speak_now)
                .setContentTitle("AudioStreamer is capturing playback")
                .setContentText("System audio is being sent to the configured node")
                .setOngoing(true)
                .build()
            if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.Q) {
                startForeground(
                    NOTIFICATION_ID,
                    notification,
                    ServiceInfo.FOREGROUND_SERVICE_TYPE_MEDIA_PROJECTION,
                )
            } else {
                startForeground(NOTIFICATION_ID, notification)
            }
            val resultCode = intent.getIntExtra(EXTRA_RESULT_CODE, 0)
            @Suppress("DEPRECATION")
            val resultData = if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.TIRAMISU) {
                intent.getParcelableExtra(EXTRA_RESULT_DATA, Intent::class.java)
            } else {
                intent.getParcelableExtra(EXTRA_RESULT_DATA)
            } ?: error("Missing MediaProjection permission result")
            val manager = getSystemService(MEDIA_PROJECTION_SERVICE) as MediaProjectionManager
            projection = manager.getMediaProjection(resultCode, resultData)
            projection?.registerCallback(object : MediaProjection.Callback() {
                override fun onStop() {
                    Log.i(TAG, "capture_projection_revoked")
                    stopCapture()
                    stopSelf()
                }
            }, null)
        } catch (error: Exception) {
            Log.e(TAG, "capture_service_start_error", error)
            captureCallback?.onError(error)
            stopForeground(STOP_FOREGROUND_REMOVE)
            stopSelf(startId)
        }
    }

    @Synchronized
    private fun beginCapture(callback: CaptureCallback) {
        check(worker == null) { "Capture service already has an active recorder" }
        val activeProjection = projection ?: error("MediaProjection is unavailable")
        captureCallback = callback
        stopRequested.set(false)
        stopNotified.set(false)
        val thread = Thread({ captureLoop(activeProjection) }, "AudioStreamerCapture").apply { isDaemon = true }
        worker = thread
        thread.start()
    }

    private fun captureLoop(activeProjection: MediaProjection) {
        runCatching { Process.setThreadPriority(Process.THREAD_PRIORITY_AUDIO) }
        var localRecorder: AudioRecord? = null
        try {
            val sampleRate = CAPTURE_SAMPLE_RATE
            val minimumBytes = AudioRecord.getMinBufferSize(
                sampleRate,
                AudioFormat.CHANNEL_IN_STEREO,
                AudioFormat.ENCODING_PCM_FLOAT,
            )
            check(minimumBytes > 0) { "Device rejected 48 kHz float stereo capture ($minimumBytes)" }
            val config = AudioPlaybackCaptureConfiguration.Builder(activeProjection)
                .addMatchingUsage(android.media.AudioAttributes.USAGE_MEDIA)
                .addMatchingUsage(android.media.AudioAttributes.USAGE_GAME)
                .addMatchingUsage(android.media.AudioAttributes.USAGE_UNKNOWN)
                .excludeUid(Process.myUid())
                .build()
            val audioFormat = AudioFormat.Builder()
                .setEncoding(AudioFormat.ENCODING_PCM_FLOAT)
                .setSampleRate(sampleRate)
                .setChannelMask(AudioFormat.CHANNEL_IN_STEREO)
                .build()
            val audioRecord = AudioRecord.Builder()
                .setAudioFormat(audioFormat)
                .setAudioPlaybackCaptureConfig(config)
                .setBufferSizeInBytes(maxOf(minimumBytes * 2, FRAMES_PER_READ * CHANNELS * Float.SIZE_BYTES * 4))
                .build()
            check(audioRecord.state == AudioRecord.STATE_INITIALIZED) { "AudioRecord failed to initialize" }
            localRecorder = audioRecord
            recorder = audioRecord
            audioRecord.startRecording()
            check(audioRecord.recordingState == AudioRecord.RECORDSTATE_RECORDING) {
                "AudioRecord did not enter recording state"
            }
            Log.i(TAG, "capture_started sampleRate=$sampleRate channels=$CHANNELS encoding=float32")
            captureCallback?.onStarted(sampleRate, CHANNELS)

            val samples = FloatArray(FRAMES_PER_READ * CHANNELS)
            val pcm = ByteArray(samples.size * Float.SIZE_BYTES)
            while (!stopRequested.get()) {
                val sampleCount = audioRecord.read(samples, 0, samples.size, AudioRecord.READ_BLOCKING)
                if (sampleCount == AudioRecord.ERROR_DEAD_OBJECT) error("AudioRecord device was lost")
                if (sampleCount < 0) error("AudioRecord read failed ($sampleCount)")
                val alignedSamples = sampleCount - sampleCount % CHANNELS
                if (alignedSamples == 0) continue
                val length = alignedSamples * Float.SIZE_BYTES
                for (sampleIndex in 0 until alignedSamples) {
                    val bits = samples[sampleIndex].toBits()
                    val offset = sampleIndex * Float.SIZE_BYTES
                    pcm[offset] = bits.toByte()
                    pcm[offset + 1] = (bits ushr 8).toByte()
                    pcm[offset + 2] = (bits ushr 16).toByte()
                    pcm[offset + 3] = (bits ushr 24).toByte()
                }
                captureCallback?.onPcm(pcm, length, sampleRate, CHANNELS)
            }
        } catch (error: Exception) {
            if (!stopRequested.get()) {
                Log.e(TAG, "capture_error", error)
                captureCallback?.onError(error)
            }
        } finally {
            localRecorder?.let { audioRecord ->
                runCatching { if (audioRecord.recordingState == AudioRecord.RECORDSTATE_RECORDING) audioRecord.stop() }
                audioRecord.release()
            }
            recorder = null
            worker = null
            Log.i(TAG, "capture_stopped")
            notifyCaptureStopped()
        }
    }

    private fun stopCapture() {
        stopRequested.set(true)
        runCatching { recorder?.stop() }
        if (worker == null) notifyCaptureStopped()
    }

    private fun notifyCaptureStopped() {
        if (stopNotified.compareAndSet(false, true)) captureCallback?.onStopped()
    }

    private fun createNotificationChannel() {
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.O) {
            val channel = NotificationChannel(
                CHANNEL_ID,
                "Audio capture",
                NotificationManager.IMPORTANCE_LOW,
            )
            getSystemService(NotificationManager::class.java).createNotificationChannel(channel)
        }
    }

    override fun onDestroy() {
        stopCapture()
        recorder = null
        projection?.let { runCatching { it.stop() } }
        projection = null
        super.onDestroy()
    }

    companion object {
        const val ACTION_START = "com.audiostreamer.capture.START"
        const val EXTRA_RESULT_CODE = "projection_result_code"
        const val EXTRA_RESULT_DATA = "projection_result_data"
        private const val TAG = "AudioStreamerCapture"
        private const val CHANNEL_ID = "audio_capture"
        private const val NOTIFICATION_ID = 4201
        private const val CAPTURE_SAMPLE_RATE = 48_000
        private const val CHANNELS = 2
        private const val FRAMES_PER_READ = 480
    }
}
