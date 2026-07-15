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

    fun profile(token: String): AccountProfile = request("GET", "/account/profile", token).toProfile()

    fun logout(token: String) { request("POST", "/account/logout", token) }

    fun sync(token: String, since: String? = null): SyncState {
        val suffix = since?.let { "?since=${java.net.URLEncoder.encode(it, "UTF-8")}" } ?: ""
        val root = request("GET", "/sync/state$suffix", token)
        return SyncState(
            root.getJSONArray("conversations").objects().map {
                SyncedConversation(it.getString("id"), it.getString("title"), it.optString("deletedAt").takeIf { value -> value.isNotBlank() && value != "null" })
            },
            root.getJSONArray("messages").objects().map { it.toMessage() },
            root.getString("serverTime"),
        )
    }

    fun saveConversation(token: String, id: String, title: String) {
        request("POST", "/sync/conversations", token, JSONObject().put("id", id).put("title", title))
    }

    fun saveMessage(token: String, message: ChatMessage) {
        request("POST", "/sync/messages", token, JSONObject()
            .put("id", message.id).put("conversationId", message.conversationId)
            .put("role", message.role).put("content", message.content).put("clientCreatedAt", message.clientCreatedAt))
    }

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
        val message = runCatching { JSONObject(text).getJSONObject("error").optString("message") }.getOrNull()
        connection.disconnect()
        throw IllegalStateException(message?.takeIf { it.isNotBlank() } ?: "服务器返回错误 $status")
    }

    private fun session(root: JSONObject) = AccountSession(root.getString("accessToken"), root.getJSONObject("profile").toProfile())
}

private fun JSONArray.objects() = (0 until length()).map { getJSONObject(it) }
private fun JSONObject.toMessage() = ChatMessage(
    getString("id"), getString("conversationId"), getString("role"), getString("content"), getString("clientCreatedAt"),
)
