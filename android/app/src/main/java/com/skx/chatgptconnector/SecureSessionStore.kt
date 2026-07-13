package com.skx.chatgptconnector

import android.content.Context
import android.security.keystore.KeyGenParameterSpec
import android.security.keystore.KeyProperties
import android.util.Base64
import org.json.JSONObject
import java.security.KeyStore
import javax.crypto.Cipher
import javax.crypto.KeyGenerator
import javax.crypto.SecretKey
import javax.crypto.spec.GCMParameterSpec

class SecureSessionStore(context: Context) {
    private val preferences = context.getSharedPreferences("secure_account", Context.MODE_PRIVATE)

    fun save(session: AccountSession) {
        val profile = JSONObject()
            .put("id", session.profile.id)
            .put("email", session.profile.email)
            .put("nickname", session.profile.nickname)
            .put("avatarMediaType", session.profile.avatarMediaType)
            .put("avatarBase64", session.profile.avatarBase64)
            .put("balanceMicrounits", session.profile.balanceMicrounits)
        val payload = JSONObject().put("accessToken", session.accessToken).put("profile", profile).toString()
        preferences.edit().putString("session", encrypt(payload)).apply()
    }

    fun load(): AccountSession? = runCatching {
        val encoded = preferences.getString("session", null) ?: return null
        val root = JSONObject(decrypt(encoded))
        AccountSession(root.getString("accessToken"), root.getJSONObject("profile").toProfile())
    }.getOrElse { clear(); null }

    fun clear() = preferences.edit().clear().apply()

    private fun key(): SecretKey {
        val store = KeyStore.getInstance("AndroidKeyStore").apply { load(null) }
        (store.getKey(KEY_ALIAS, null) as? SecretKey)?.let { return it }
        return KeyGenerator.getInstance(KeyProperties.KEY_ALGORITHM_AES, "AndroidKeyStore").run {
            init(KeyGenParameterSpec.Builder(KEY_ALIAS, KeyProperties.PURPOSE_ENCRYPT or KeyProperties.PURPOSE_DECRYPT)
                .setBlockModes(KeyProperties.BLOCK_MODE_GCM)
                .setEncryptionPaddings(KeyProperties.ENCRYPTION_PADDING_NONE)
                .build())
            generateKey()
        }
    }

    private fun encrypt(value: String): String {
        val cipher = Cipher.getInstance("AES/GCM/NoPadding").apply { init(Cipher.ENCRYPT_MODE, key()) }
        val encrypted = cipher.doFinal(value.toByteArray(Charsets.UTF_8))
        return Base64.encodeToString(cipher.iv + encrypted, Base64.NO_WRAP)
    }

    private fun decrypt(value: String): String {
        val bytes = Base64.decode(value, Base64.NO_WRAP)
        require(bytes.size > 12)
        val cipher = Cipher.getInstance("AES/GCM/NoPadding").apply {
            init(Cipher.DECRYPT_MODE, key(), GCMParameterSpec(128, bytes.copyOfRange(0, 12)))
        }
        return cipher.doFinal(bytes.copyOfRange(12, bytes.size)).toString(Charsets.UTF_8)
    }

    companion object { private const val KEY_ALIAS = "chatgpt_connector_account_v1" }
}

fun JSONObject.toProfile() = AccountProfile(
    id = getString("id"), email = getString("email"), nickname = getString("nickname"),
    avatarMediaType = optString("avatarMediaType").takeIf { it.isNotBlank() && it != "null" },
    avatarBase64 = optString("avatarBase64").takeIf { it.isNotBlank() && it != "null" },
    balanceMicrounits = optLong("balanceMicrounits"),
)
