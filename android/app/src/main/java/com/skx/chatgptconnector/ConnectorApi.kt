package com.skx.chatgptconnector

import org.json.JSONArray
import org.json.JSONObject
import java.net.HttpURLConnection
import java.net.URI

class ConnectorApi(private val baseUrl: String = "https://520skx.com") {
    fun requestCode(email: String) {
        request("POST", "/account/code", body = JSONObject().put("email", email))
    }

    fun verify(email: String, code: String): AccountSession {
        val root = request("POST", "/account/verify", body = JSONObject().put("email", email).put("code", code))
        return AccountSession(root.getString("accessToken"), root.getJSONObject("profile").toProfile())
    }

    fun login(email: String, password: String): AccountSession = session(
        request("POST", "/account/login", body = JSONObject().put("email", email).put("password", password)),
    )

    fun register(email: String, password: String, code: String): AccountSession = session(
        request("POST", "/account/register", body = JSONObject().put("email", email).put("password", password).put("code", code)),
    )

    fun resetPassword(email: String, password: String, code: String): AccountSession = session(
        request("POST", "/account/password/reset", body = JSONObject().put("email", email).put("password", password).put("code", code)),
    )

    fun profile(token: String): AccountProfile = request("GET", "/account/profile", token).toProfile()

    fun logout(token: String) { request("POST", "/account/logout", token) }

    fun streamResponse(token: String, messages: List<ChatMessage>, onDelta: (String) -> Unit): String {
        val input = JSONArray()
        messages.forEach { input.put(JSONObject().put("role", it.role).put("content", it.content)) }
        val connection = open("POST", "/v1/responses", token).apply {
            setRequestProperty("Accept", "text/event-stream")
            doOutput = true
        }
        connection.outputStream.use { it.write(JSONObject().put("model", "gpt-5.6-sol").put("input", input).put("stream", true).toString().toByteArray()) }
        ensureSuccess(connection)
        val complete = StringBuilder()
        connection.inputStream.bufferedReader().useLines { lines ->
            lines.filter { it.startsWith("data:") }.forEach { line ->
                val data = line.removePrefix("data:").trim()
                if (data == "[DONE]" || data.isBlank()) return@forEach
                runCatching {
                    val event = JSONObject(data)
                    if (event.optString("type") == "response.output_text.delta") {
                        event.optString("delta").takeIf { it.isNotEmpty() }?.let { delta -> complete.append(delta); onDelta(delta) }
                    }
                }
            }
        }
        connection.disconnect()
        return complete.toString()
    }

    private fun request(method: String, path: String, token: String? = null, body: JSONObject? = null): JSONObject {
        val connection = open(method, path, token)
        if (body != null) {
            connection.doOutput = true
            connection.outputStream.use { it.write(body.toString().toByteArray(Charsets.UTF_8)) }
        }
        ensureSuccess(connection)
        val text = connection.inputStream.bufferedReader().use { it.readText() }
        connection.disconnect()
        return if (text.isBlank()) JSONObject() else JSONObject(text)
    }

    private fun open(method: String, path: String, token: String?): HttpURLConnection =
        (URI.create("$baseUrl$path").toURL().openConnection() as HttpURLConnection).apply {
            requestMethod = method
            connectTimeout = 15_000
            readTimeout = 120_000
            setRequestProperty("Content-Type", "application/json; charset=utf-8")
            setRequestProperty("X-Client-Platform", "android")
            token?.let { setRequestProperty("Authorization", "Bearer $it") }
        }

    private fun ensureSuccess(connection: HttpURLConnection) {
        val status = connection.responseCode
        if (status in 200..299) return
        val text = connection.errorStream?.bufferedReader()?.use { it.readText() }.orEmpty()
        val error = runCatching { JSONObject(text).getJSONObject("error") }.getOrNull()
        val message = error?.optString("message")
        val code = error?.optString("code")
        connection.disconnect()
        throw ConnectorApiException(code?.takeIf { it.isNotBlank() } ?: "server_error", message?.takeIf { it.isNotBlank() } ?: "服务器返回错误 $status")
    }

    private fun session(root: JSONObject) = AccountSession(root.getString("accessToken"), root.getJSONObject("profile").toProfile())
}

class ConnectorApiException(val code: String, message: String) : IllegalStateException(message)
