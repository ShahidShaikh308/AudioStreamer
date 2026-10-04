package com.audiostreamer.platform.android.capture

import android.Manifest
import android.app.Activity
import android.content.ComponentName
import android.content.Context
import android.content.Intent
import android.content.ServiceConnection
import android.content.pm.PackageManager
import android.media.projection.MediaProjectionManager
import android.os.Build
import android.os.IBinder
import androidx.activity.ComponentActivity
import androidx.activity.result.contract.ActivityResultContracts
import androidx.core.content.ContextCompat
import android.util.Log

/** Capture callback runs on the capture worker; payload storage is reused after the callback returns. */
internal interface CaptureCallback {
    fun onStarted(sampleRate: Int, channels: Int)
    fun onPcm(payload: ByteArray, length: Int, sampleRate: Int, channels: Int)
    fun onStopped()
    fun onError(error: Throwable)
}

/** Encapsulates consent, MediaProjection, and the foreground capture service from the node/UI. */
internal class AndroidPlaybackCapture(activity: ComponentActivity) {
    private val activity = activity
    private val applicationContext = activity.applicationContext
    private var callback: CaptureCallback? = null
    private var generation = 0L
    private var pendingGeneration: Long? = null
    private var captureService: PlaybackCaptureService.LocalBinder? = null
    private var bound = false
    private val recordPermission = activity.registerForActivityResult(
        ActivityResultContracts.RequestPermission(),
    ) { granted ->
        if (!isCurrentRequest()) return@registerForActivityResult
        if (granted) launchProjectionPrompt() else failCurrent(
            SecurityException("Microphone permission is required by Android playback capture"),
        )
    }

    private val projectionPermission = activity.registerForActivityResult(
        ActivityResultContracts.StartActivityForResult(),
    ) { result ->
        val request = pendingGeneration
        if (request == null || request != generation) return@registerForActivityResult
        val data = result.data
        if (result.resultCode != Activity.RESULT_OK || data == null) {
            failCurrent(SecurityException("System audio capture permission was denied"))
            return@registerForActivityResult
        }
        val serviceIntent = Intent(applicationContext, PlaybackCaptureService::class.java)
            .setAction(PlaybackCaptureService.ACTION_START)
            .putExtra(PlaybackCaptureService.EXTRA_RESULT_CODE, result.resultCode)
            .putExtra(PlaybackCaptureService.EXTRA_RESULT_DATA, data)
        try {
            if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.O) {
                applicationContext.startForegroundService(serviceIntent)
            } else {
                applicationContext.startService(serviceIntent)
            }
            bound = applicationContext.bindService(serviceIntent, connection, Context.BIND_AUTO_CREATE)
            check(bound) { "Could not bind to the playback capture service" }
        } catch (error: Exception) {
            failCurrent(error)
        }
    }

    private val connection = object : ServiceConnection {
        override fun onServiceConnected(name: ComponentName?, binder: IBinder?) {
            val serviceBinder = binder as? PlaybackCaptureService.LocalBinder
            if (serviceBinder == null || !isCurrentRequest()) {
                stop()
                return
            }
            captureService = serviceBinder
            try {
                serviceBinder.beginCapture(object : CaptureCallback {
                    override fun onStarted(sampleRate: Int, channels: Int) = callback?.onStarted(sampleRate, channels) ?: Unit
                    override fun onPcm(payload: ByteArray, length: Int, sampleRate: Int, channels: Int) =
                        callback?.onPcm(payload, length, sampleRate, channels) ?: Unit
                    override fun onStopped() {
                        val activeCallback = callback
                        callback = null
                        pendingGeneration = null
                        captureService = null
                        activeCallback?.onStopped()
                    }
                    override fun onError(error: Throwable) = callback?.onError(error) ?: Unit
                })
            } catch (error: Exception) {
                failCurrent(error)
            }
        }

        override fun onServiceDisconnected(name: ComponentName?) {
            captureService = null
            callback?.onError(IllegalStateException("Playback capture service disconnected"))
        }
    }

    fun start(listener: CaptureCallback) {
        check(callback == null) { "Android playback capture is already running" }
        if (Build.VERSION.SDK_INT < Build.VERSION_CODES.Q) {
            listener.onError(UnsupportedOperationException(
                "Mixed playback capture requires Android 10 (API 29) or newer",
            ))
            listener.onStopped()
            return
        }
        generation++
        pendingGeneration = generation
        callback = listener
        Log.i(TAG, "capture_start_requested")
        if (ContextCompat.checkSelfPermission(applicationContext, Manifest.permission.RECORD_AUDIO) ==
            PackageManager.PERMISSION_GRANTED
        ) {
            launchProjectionPrompt()
        } else {
            recordPermission.launch(Manifest.permission.RECORD_AUDIO)
        }
    }

    fun stop() {
        generation++
        pendingGeneration = null
        val hadService = captureService != null
        captureService?.stopCapture()
        captureService = null
        if (bound) {
            runCatching { applicationContext.unbindService(connection) }
            bound = false
        }
        if (hadService) {
            applicationContext.stopService(Intent(applicationContext, PlaybackCaptureService::class.java))
        } else {
            callback?.onStopped()
            callback = null
        }
        Log.i(TAG, "capture_stop_requested")
    }

    private fun launchProjectionPrompt() {
        if (!isCurrentRequest()) return
        val manager = activity.getSystemService(Context.MEDIA_PROJECTION_SERVICE) as MediaProjectionManager
        projectionPermission.launch(manager.createScreenCaptureIntent())
    }

    private fun isCurrentRequest(): Boolean = callback != null && pendingGeneration == generation

    private fun failCurrent(error: Throwable) {
        Log.e(TAG, "capture_error", error)
        pendingGeneration = null
        callback?.onError(error)
        callback = null
        captureService?.stopCapture()
        applicationContext.stopService(Intent(applicationContext, PlaybackCaptureService::class.java))
    }

    private companion object {
        const val TAG = "AudioStreamerCapture"
    }
}
