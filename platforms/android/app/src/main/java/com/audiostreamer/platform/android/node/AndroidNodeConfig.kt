package com.audiostreamer.platform.android.node

import android.content.Context
import android.os.Build
import android.util.Log
import com.audiostreamer.core.node.NodeSettings

internal class AndroidNodeConfigStore(context: Context) {
    private val preferences = context.getSharedPreferences("audiostreamer_node", Context.MODE_PRIVATE)

    fun load(): NodeSettings = NodeSettings(
        nodeName = preferences.getString(KEY_NODE_NAME, Build.MODEL)?.takeIf { it.isNotBlank() }
            ?: "Android Node",
        destinationIp = preferences.getString(KEY_DESTINATION_IP, "127.0.0.1") ?: "127.0.0.1",
        destinationPort = preferences.getInt(KEY_DESTINATION_PORT, DEFAULT_PORT),
        receivePort = preferences.getInt(KEY_RECEIVE_PORT, DEFAULT_PORT),
        logLevel = preferences.getString(KEY_LOG_LEVEL, "INFO") ?: "INFO",
    )

    fun save(settings: NodeSettings) {
        preferences.edit()
            .putString(KEY_NODE_NAME, settings.nodeName)
            .putString(KEY_DESTINATION_IP, settings.destinationIp)
            .putInt(KEY_DESTINATION_PORT, settings.destinationPort)
            .putInt(KEY_RECEIVE_PORT, settings.receivePort)
            .putString(KEY_LOG_LEVEL, settings.logLevel)
            .apply()
    }

    companion object {
        const val DEFAULT_PORT = 5005
        private const val KEY_NODE_NAME = "node_name"
        private const val KEY_DESTINATION_IP = "destination_ip"
        private const val KEY_DESTINATION_PORT = "destination_port"
        private const val KEY_RECEIVE_PORT = "receive_port"
        private const val KEY_LOG_LEVEL = "log_level"
    }
}

internal class AndroidNodeLogger(private val level: String) {
    fun info(message: String) { if (levelRank >= INFO) Log.i(TAG, message) }
    fun warning(message: String, error: Throwable? = null) {
        if (levelRank >= WARNING) Log.w(TAG, message, error)
    }
    fun error(message: String, error: Throwable? = null) {
        if (levelRank >= ERROR) Log.e(TAG, message, error)
    }

    private val levelRank: Int
        get() = when (level.uppercase()) {
            "DEBUG" -> 0
            "INFO" -> INFO
            "WARNING" -> WARNING
            "ERROR" -> ERROR
            else -> INFO
        }

    private companion object {
        const val TAG = "AudioStreamerNode"
        const val INFO = 1
        const val WARNING = 2
        const val ERROR = 3
    }
}
