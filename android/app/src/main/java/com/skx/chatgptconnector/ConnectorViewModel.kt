package com.skx.chatgptconnector

import android.app.Application
import android.os.Handler
import android.os.Looper
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.setValue
import androidx.lifecycle.AndroidViewModel
import androidx.lifecycle.viewModelScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext
import java.time.Instant
import java.util.UUID

data class ConnectorUiState(
    val session: AccountSession? = null,
    val email: String = "",
    val code: String = "",
    val input: String = "",
    val messages: List<ChatMessage> = emptyList(),
    val conversationId: String? = null,
    val loading: Boolean = false,
    val notice: String = "",
    val error: String? = null,
)

class ConnectorViewModel(application: Application) : AndroidViewModel(application) {
    private val api = ConnectorApi()
    private val sessionStore = SecureSessionStore(application)
    private val mainHandler = Handler(Looper.getMainLooper())
    var state by mutableStateOf(ConnectorUiState())
        private set

    init {
        sessionStore.load()?.let { saved ->
            state = state.copy(session = saved, loading = true)
            viewModelScope.launch {
                try {
                    val result = withContext(Dispatchers.IO) {
                        val profile = api.profile(saved.accessToken)
                        profile to api.sync(saved.accessToken)
                    }
                    val session = saved.copy(profile = result.first)
                    sessionStore.save(session)
                    applySync(session, result.second)
                } catch (_: Exception) {
                    sessionStore.clear()
                    state = ConnectorUiState(error = "登录已过期，请重新登录")
                }
            }
        }
    }

    fun setEmail(value: String) { state = state.copy(email = value, error = null) }
    fun setCode(value: String) { state = state.copy(code = value.filter(Char::isDigit).take(6), error = null) }
    fun setInput(value: String) { state = state.copy(input = value) }

    fun requestCode() = runBusy {
        require(state.email.isNotBlank()) { "请输入邮箱地址" }
        withContext(Dispatchers.IO) { api.requestCode(state.email.trim()) }
        state = state.copy(notice = "验证码已发送，请检查邮箱")
    }

    fun login() = runBusy {
        require(state.email.isNotBlank() && state.code.length == 6) { "请输入邮箱和 6 位验证码" }
        val session = withContext(Dispatchers.IO) { api.verify(state.email.trim(), state.code) }
        sessionStore.save(session)
        val sync = withContext(Dispatchers.IO) { api.sync(session.accessToken) }
        applySync(session, sync)
    }

    fun logout() = runBusy {
        state.session?.let { session -> runCatching { withContext(Dispatchers.IO) { api.logout(session.accessToken) } } }
        sessionStore.clear()
        state = ConnectorUiState()
    }

    fun newConversation() {
        if (state.loading) return
        state = state.copy(conversationId = null, messages = emptyList(), notice = "已新建会话")
    }

    fun send() {
        val session = state.session ?: return
        val text = state.input.trim()
        if (text.isBlank() || state.loading) return
        val conversationId = state.conversationId ?: UUID.randomUUID().toString()
        val userMessage = ChatMessage(UUID.randomUUID().toString(), conversationId, "user", text, Instant.now().toString())
        val assistantId = UUID.randomUUID().toString()
        val assistantTime = Instant.now().toString()
        val placeholder = ChatMessage(assistantId, conversationId, "assistant", "", assistantTime)
        val context = state.messages + userMessage
        state = state.copy(conversationId = conversationId, input = "", messages = context + placeholder, loading = true, error = null)
        viewModelScope.launch {
            try {
                val answer = withContext(Dispatchers.IO) {
                    if (state.conversationId == conversationId && context.count { it.conversationId == conversationId } == 1)
                        api.saveConversation(session.accessToken, conversationId, text.take(40))
                    api.saveMessage(session.accessToken, userMessage)
                    api.streamResponse(session.accessToken, context) { delta ->
                        mainHandler.post {
                            state = state.copy(messages = state.messages.map {
                                if (it.id == assistantId) it.copy(content = it.content + delta) else it
                            })
                        }
                    }
                }
                val assistant = placeholder.copy(content = answer.ifBlank { "暂时没有收到模型输出。" })
                withContext(Dispatchers.IO) { api.saveMessage(session.accessToken, assistant) }
                state = state.copy(messages = state.messages.map { if (it.id == assistantId) assistant else it }, loading = false)
            } catch (error: Exception) {
                state = state.copy(messages = state.messages.filterNot { it.id == assistantId }, loading = false, error = error.message ?: "发送失败")
            }
        }
    }

    private fun applySync(session: AccountSession, sync: SyncState) {
        val conversation = sync.conversations.lastOrNull { it.deletedAt == null }
        val messages = conversation?.let { selected ->
            sync.messages.filter { it.conversationId == selected.id }.sortedBy { it.clientCreatedAt }
        }.orEmpty()
        state = state.copy(session = session, conversationId = conversation?.id, messages = messages, loading = false, error = null)
    }

    private fun runBusy(block: suspend () -> Unit) {
        if (state.loading) return
        state = state.copy(loading = true, error = null, notice = "")
        viewModelScope.launch {
            try { block() }
            catch (error: Exception) { state = state.copy(error = error.message ?: "操作失败") }
            finally { state = state.copy(loading = false) }
        }
    }
}
