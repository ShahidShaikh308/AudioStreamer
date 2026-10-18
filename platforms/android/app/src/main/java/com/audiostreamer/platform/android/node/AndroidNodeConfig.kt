package com.audiostreamer.platform.android.node

import android.content.Context
import android.os.Build
import android.util.Log
import android.security.keystore.KeyGenParameterSpec
import android.security.keystore.KeyProperties
import com.audiostreamer.core.node.NodeSettings
import java.security.SecureRandom
import java.security.KeyStore
import javax.crypto.Cipher
import javax.crypto.KeyGenerator
import javax.crypto.SecretKey
import javax.crypto.spec.GCMParameterSpec
import android.util.Base64

internal class AndroidNodeConfigStore(context: Context) {
    private val preferences = context.getSharedPreferences("audiostreamer_node", Context.MODE_PRIVATE)

    fun load(): NodeSettings {
        val storedNodeId = if (preferences.contains(KEY_NODE_ID)) preferences.getLong(KEY_NODE_ID, 0L) else 0L
        val generatedNodeId = SecureRandom().nextLong().let { if (it == 0L) 1L else it }
        val nodeId = storedNodeId.takeIf { it != 0L } ?: generatedNodeId.also {
            preferences.edit().putLong(KEY_NODE_ID, it).apply()
        }
        return NodeSettings(
            nodeName = preferences.getString(KEY_NODE_NAME, Build.MODEL)?.takeIf { it.isNotBlank() }
                ?: "Android Node",
            destinationIp = preferences.getString(KEY_DESTINATION_IP, "127.0.0.1") ?: "127.0.0.1",
            destinationPort = preferences.getInt(KEY_DESTINATION_PORT, DEFAULT_PORT),
            receivePort = preferences.getInt(KEY_RECEIVE_PORT, DEFAULT_PORT),
            logLevel = preferences.getString(KEY_LOG_LEVEL, "INFO") ?: "INFO",
            nodeId = nodeId,
            controlPort = preferences.getInt(KEY_CONTROL_PORT, DEFAULT_CONTROL_PORT),
            controllerSecretHex = loadControllerSecret(),
        )
    }

    fun save(settings: NodeSettings) {
        preferences.edit()
            .putString(KEY_NODE_NAME, settings.nodeName)
            .putLong(KEY_NODE_ID, settings.nodeId)
            .putString(KEY_DESTINATION_IP, settings.destinationIp)
            .putInt(KEY_DESTINATION_PORT, settings.destinationPort)
            .putInt(KEY_RECEIVE_PORT, settings.receivePort)
            .putString(KEY_LOG_LEVEL, settings.logLevel)
            .putInt(KEY_CONTROL_PORT, settings.controlPort)
            .putString(KEY_CONTROLLER_SECRET, protectControllerSecret(settings.controllerSecretHex))
            .apply()
    }

    companion object {
        const val DEFAULT_PORT = 5005
        const val DEFAULT_CONTROL_PORT = 55050
        private const val KEY_NODE_NAME = "node_name"
        private const val KEY_NODE_ID = "node_id"
        private const val KEY_DESTINATION_IP = "destination_ip"
        private const val KEY_DESTINATION_PORT = "destination_port"
        private const val KEY_RECEIVE_PORT = "receive_port"
        private const val KEY_LOG_LEVEL = "log_level"
        private const val KEY_CONTROL_PORT = "control_port"
        private const val KEY_CONTROLLER_SECRET = "controller_secret_hex"
        // The controller credential is shipped with the app so pairing does not require manual entry.
        // It is still encrypted at rest with Android Keystore; embedded credentials can be extracted
        // from a client package and must not be treated as a server-side secret.
        private const val EMBEDDED_CONTROLLER_SECRET_HEX = "C979AACE57EE4913956D06AC0379E1F9B784558FFDCBE2F9D884001AD0A0AAF8"
        private const val CONTROLLER_KEY_ALIAS = "audiostreamer_controller_secret_key"
        private const val GCM_IV_BYTES = 12
        private const val GCM_TAG_BITS = 128
    }

    private fun loadControllerSecret(): String {
        val stored = preferences.getString(KEY_CONTROLLER_SECRET, null)
        val isCurrent = try {
            stored != null && decryptControllerSecret(stored) == EMBEDDED_CONTROLLER_SECRET_HEX
        } catch (error: Exception) {
            Log.w("AudioStreamerNode", "Stored controller credential could not be read; restoring built-in credential")
            false
        }
        if (!isCurrent) {
            runCatching {
                preferences.edit()
                    .putString(KEY_CONTROLLER_SECRET, protectControllerSecret(EMBEDDED_CONTROLLER_SECRET_HEX))
                    .apply()
            }.onFailure { error ->
                Log.e("AudioStreamerNode", "Could not persist controller credential", error)
            }
        }
        return EMBEDDED_CONTROLLER_SECRET_HEX
    }

    private fun decryptControllerSecret(encrypted: String): String {
        val blob = Base64.decode(encrypted, Base64.NO_WRAP)
        require(blob.size > GCM_IV_BYTES) { "Stored controller credential is invalid" }
        val cipher = Cipher.getInstance("AES/GCM/NoPadding")
        cipher.init(Cipher.DECRYPT_MODE, getControllerKey(), GCMParameterSpec(GCM_TAG_BITS, blob, 0, GCM_IV_BYTES))
        return String(cipher.doFinal(blob, GCM_IV_BYTES, blob.size - GCM_IV_BYTES), Charsets.UTF_8)
    }

    private fun protectControllerSecret(secret: String): String {
        if (secret.isEmpty()) return ""
        val cipher = Cipher.getInstance("AES/GCM/NoPadding")
        cipher.init(Cipher.ENCRYPT_MODE, getControllerKey())
        val encrypted = cipher.doFinal(secret.toByteArray(Charsets.UTF_8))
        return Base64.encodeToString(cipher.iv + encrypted, Base64.NO_WRAP)
    }

    private fun getControllerKey(): SecretKey {
        val keyStore = KeyStore.getInstance("AndroidKeyStore").apply { load(null) }
        (keyStore.getKey(CONTROLLER_KEY_ALIAS, null) as? SecretKey)?.let { return it }
        return KeyGenerator.getInstance(KeyProperties.KEY_ALGORITHM_AES, "AndroidKeyStore").run {
            init(KeyGenParameterSpec.Builder(CONTROLLER_KEY_ALIAS,
                KeyProperties.PURPOSE_ENCRYPT or KeyProperties.PURPOSE_DECRYPT)
                .setBlockModes(KeyProperties.BLOCK_MODE_GCM)
                .setEncryptionPaddings(KeyProperties.ENCRYPTION_PADDING_NONE)
                .setKeySize(256)
                .setRandomizedEncryptionRequired(true)
                .build())
            generateKey()
        }
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
