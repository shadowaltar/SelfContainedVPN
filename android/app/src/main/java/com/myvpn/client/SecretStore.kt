package com.myvpn.client

import android.content.Context
import android.security.keystore.KeyGenParameterSpec
import android.security.keystore.KeyProperties
import android.util.Base64
import java.security.KeyStore
import javax.crypto.Cipher
import javax.crypto.KeyGenerator
import javax.crypto.SecretKey
import javax.crypto.spec.GCMParameterSpec

/**
 * Stores secrets (the imported WireGuard configuration and this device's WireGuard private key)
 * encrypted with an AES-256-GCM key held in the Android Keystore, instead of leaving them in
 * plaintext SharedPreferences.
 */
class SecretStore(private val context: Context) {

    fun saveConfig(text: String) = put(KEY_CONFIG, text)
    fun loadConfig(): String? = get(KEY_CONFIG)

    fun savePrivateKey(key: String) = put(KEY_PRIVATE, key)
    fun loadPrivateKey(): String? = get(KEY_PRIVATE)
    fun clearPrivateKey() = prefs().edit().remove(KEY_PRIVATE).apply()

    private fun put(name: String, plaintext: String) {
        val cipher = Cipher.getInstance(TRANSFORMATION)
        cipher.init(Cipher.ENCRYPT_MODE, secretKey())
        val iv = cipher.iv
        val ciphertext = cipher.doFinal(plaintext.toByteArray(Charsets.UTF_8))
        val blob = iv + ciphertext
        prefs().edit().putString(name, Base64.encodeToString(blob, Base64.NO_WRAP)).apply()
    }

    private fun get(name: String): String? {
        val stored = prefs().getString(name, null) ?: return null
        return try {
            val blob = Base64.decode(stored, Base64.NO_WRAP)
            if (blob.size <= IV_SIZE) return null
            val iv = blob.copyOfRange(0, IV_SIZE)
            val ciphertext = blob.copyOfRange(IV_SIZE, blob.size)
            val cipher = Cipher.getInstance(TRANSFORMATION)
            cipher.init(Cipher.DECRYPT_MODE, secretKey(), GCMParameterSpec(TAG_BITS, iv))
            String(cipher.doFinal(ciphertext), Charsets.UTF_8)
        } catch (e: Exception) {
            null
        }
    }

    private fun prefs() = context.getSharedPreferences(PREFS, Context.MODE_PRIVATE)

    private fun secretKey(): SecretKey {
        val keyStore = KeyStore.getInstance(ANDROID_KEYSTORE).apply { load(null) }
        (keyStore.getEntry(KEY_ALIAS, null) as? KeyStore.SecretKeyEntry)?.let { return it.secretKey }

        val generator = KeyGenerator.getInstance(KeyProperties.KEY_ALGORITHM_AES, ANDROID_KEYSTORE)
        generator.init(
            KeyGenParameterSpec.Builder(
                KEY_ALIAS,
                KeyProperties.PURPOSE_ENCRYPT or KeyProperties.PURPOSE_DECRYPT,
            )
                .setBlockModes(KeyProperties.BLOCK_MODE_GCM)
                .setEncryptionPaddings(KeyProperties.ENCRYPTION_PADDING_NONE)
                .setKeySize(256)
                .build(),
        )
        return generator.generateKey()
    }

    private companion object {
        const val ANDROID_KEYSTORE = "AndroidKeyStore"
        const val KEY_ALIAS = "myvpn_config_key"
        const val PREFS = "myvpn"
        const val KEY_CONFIG = "config_enc"
        const val KEY_PRIVATE = "privkey_enc"
        const val TRANSFORMATION = "AES/GCM/NoPadding"
        const val IV_SIZE = 12
        const val TAG_BITS = 128
    }
}
