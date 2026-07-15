package com.skx.chatgptconnector

import android.content.Context
import android.content.Intent
import androidx.core.content.FileProvider
import org.json.JSONObject
import java.io.File
import java.net.HttpURLConnection
import java.net.URI
import java.security.MessageDigest

data class AndroidUpdate(val version: String, val apkUrl: String, val checksumUrl: String)

class AndroidUpdateService(private val baseUrl: String = "https://luodongchat.com") {
    fun check(currentVersion: String): AndroidUpdate? {
        val root = JSONObject(read("$baseUrl/client/update.json"))
        val version = root.getString("version").trimStart('v')
        if (!isNewer(version, currentVersion)) return null
        val apkUrl = root.optString("androidApkUrl")
        val checksumUrl = root.optString("androidChecksumUrl")
        requireTrusted(apkUrl)
        requireTrusted(checksumUrl)
        return AndroidUpdate(version, apkUrl, checksumUrl)
    }

    fun download(context: Context, update: AndroidUpdate): File {
        val bytes = readBytes(update.apkUrl)
        val expected = read(update.checksumUrl).trim().split(Regex("\\s+"))[0]
        val actual = MessageDigest.getInstance("SHA-256").digest(bytes).joinToString("") { "%02x".format(it) }
        require(expected.equals(actual, ignoreCase = true)) { "更新文件完整性校验失败，已取消安装" }
        val directory = File(context.cacheDir, "updates").apply { mkdirs() }
        return File(directory, "LuodongChat-${update.version}.apk").apply { writeBytes(bytes) }
    }

    fun openInstaller(context: Context, apk: File) {
        val uri = FileProvider.getUriForFile(context, "${context.packageName}.files", apk)
        context.startActivity(Intent(Intent.ACTION_VIEW).apply {
            setDataAndType(uri, "application/vnd.android.package-archive")
            addFlags(Intent.FLAG_GRANT_READ_URI_PERMISSION or Intent.FLAG_ACTIVITY_NEW_TASK)
        })
    }

    private fun read(url: String) = readBytes(url).toString(Charsets.UTF_8)

    private fun readBytes(url: String): ByteArray {
        val connection = URI.create(url).toURL().openConnection() as HttpURLConnection
        connection.connectTimeout = 15_000
        connection.readTimeout = 120_000
        connection.setRequestProperty("X-Client-Platform", "android")
        try {
            require(connection.responseCode in 200..299) { "更新服务暂时不可用" }
            return connection.inputStream.use { it.readBytes() }
        } finally { connection.disconnect() }
    }

    private fun requireTrusted(value: String) {
        val uri = URI.create(value)
        require(uri.scheme == "https" && uri.host in setOf("luodongchat.com", "luodongchat-app.oss-cn-beijing.aliyuncs.com", "github.com")) { "更新清单包含不受信任的下载地址" }
    }

    companion object {
        internal fun isNewer(candidate: String, current: String): Boolean {
            val candidateCore = core(candidate)
            val currentCore = core(current)
            for (index in 0..2) if (candidateCore[index] != currentCore[index]) return candidateCore[index] > currentCore[index]
            return preview(candidate) > preview(current)
        }

        private fun core(value: String) = value.trim().trimStart('v').substringBefore('-').substringBefore('+')
            .split('.').map { it.toIntOrNull() ?: 0 }.let { parts -> List(3) { parts.getOrElse(it) { 0 } } }
        private fun preview(value: String) = value.substringAfter("preview.", "2147483647").substringBefore('-').substringBefore('+').toIntOrNull() ?: 0
    }
}
