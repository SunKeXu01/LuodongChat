package com.skx.chatgptconnector

import org.json.JSONArray
import org.json.JSONObject
import java.net.HttpURLConnection
import java.net.URI

class ConnectorApi(private val baseUrl: String = "https://luodongchat.com") {
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

    fun streamResponse(token: String, messages: List<ChatMessage>, onDelta: (String) -> Unit): ChatStreamResult = try {
        streamResponseOnce(token, messages, onDelta, true)
    } catch (error: ConnectorApiException) {
        if (error.code !in setOf("web_search_unavailable", "upstream_unavailable")) throw error
        streamResponseOnce(token, messages, onDelta, false).copy(webSearchUnavailable = true)
    }

    private fun streamResponseOnce(token: String, messages: List<ChatMessage>, onDelta: (String) -> Unit, enableWebSearch: Boolean): ChatStreamResult {
        val input = JSONArray()
        messages.forEach { input.put(JSONObject().put("role", it.role).put("content", it.content)) }
        val connection = open("POST", "/v1/responses", token).apply {
            setRequestProperty("Accept", "text/event-stream")
            doOutput = true
        }
        val request = JSONObject().put("model", "gpt-5.6-sol").put("input", input).put("stream", true)
        if (enableWebSearch) request.put("tools", JSONArray().put(JSONObject().put("type", "web_search").put("search_context_size", "low"))).put("tool_choice", "auto")
        connection.outputStream.use { it.write(request.toString().toByteArray()) }
        ensureSuccess(connection)
        val complete = StringBuilder()
        val citations = linkedMapOf<String, ChatCitation>()
        connection.inputStream.bufferedReader().useLines { lines ->
            lines.filter { it.startsWith("data:") }.forEach { line ->
                val data = line.removePrefix("data:").trim()
                if (data == "[DONE]" || data.isBlank()) return@forEach
                runCatching {
                    val event = JSONObject(data)
                    if (event.optString("type") == "response.output_text.delta") {
                        event.optString("delta").takeIf { it.isNotEmpty() }?.let { delta -> complete.append(delta); onDelta(delta) }
                    }
                    if (event.optString("type") == "response.completed") collectCitations(event, citations)
                }
            }
        }
        connection.disconnect()
        return ChatStreamResult(complete.toString(), citations.values.toList())
    }

    private fun collectCitations(event: JSONObject, citations: MutableMap<String, ChatCitation>) {
        val output = event.optJSONObject("response")?.optJSONArray("output") ?: return
        for (i in 0 until output.length()) {
            val content = output.optJSONObject(i)?.optJSONArray("content") ?: continue
            for (j in 0 until content.length()) {
                val annotations = content.optJSONObject(j)?.optJSONArray("annotations") ?: continue
                for (k in 0 until annotations.length()) {
                    val annotation = annotations.optJSONObject(k) ?: continue
                    if (annotation.optString("type") != "url_citation") continue
                    val url = annotation.optString("url").takeIf { it.startsWith("https://") || it.startsWith("http://") } ?: continue
                    citations[url] = ChatCitation(annotation.optString("title").ifBlank { URI.create(url).host ?: url }, url)
                }
            }
        }
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
