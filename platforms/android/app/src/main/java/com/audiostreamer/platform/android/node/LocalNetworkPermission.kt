package com.audiostreamer.platform.android.node

import android.content.pm.PackageManager
import android.os.Build
import androidx.activity.ComponentActivity
import androidx.activity.result.contract.ActivityResultContracts
import java.util.ArrayDeque

/** Keeps the Android-version-specific local-network permission flow out of the Activity UI. */
internal class LocalNetworkPermission(private val activity: ComponentActivity) {
    private val waiting = ArrayDeque<(Boolean) -> Unit>()
    private var requestInFlight = false

    private val permissionLauncher = activity.registerForActivityResult(
        ActivityResultContracts.RequestPermission(),
    ) { isGranted ->
        synchronized(this) {
            requestInFlight = false
            val pending = waiting.toList()
            waiting.clear()
            pending.forEach { action -> action(isGranted) }
        }
    }

    fun runWhenAllowed(action: () -> Unit, onDenied: () -> Unit) {
        if (Build.VERSION.SDK_INT < ANDROID_LOCAL_NETWORK_PERMISSION_API ||
            activity.checkSelfPermission(LOCAL_NETWORK_PERMISSION) == PackageManager.PERMISSION_GRANTED
        ) {
            action()
            return
        }
        synchronized(this) {
            waiting.addLast { isGranted -> if (isGranted) action() else onDenied() }
            if (!requestInFlight) {
                requestInFlight = true
                permissionLauncher.launch(LOCAL_NETWORK_PERMISSION)
            }
        }
    }

    companion object {
        private const val LOCAL_NETWORK_PERMISSION = "android.permission.ACCESS_LOCAL_NETWORK"
        private const val ANDROID_LOCAL_NETWORK_PERMISSION_API = 37
    }
}
