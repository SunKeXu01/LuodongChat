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
    val password: String = "",
    val confirmPassword: String = "",
    val authMode: Int = 0,
    val input: String = "",
    val messages: List<ChatMessage> = emptyList(),
    val conversationId: String? = null,
    val loading: Boolean = false,
    val availableUpdate: AndroidUpdate? = null,
    val notice: String = "",
    val error: String? = null,
)

class ConnectorViewModel(application: Application) : AndroidViewModel(application) {
    private val api = ConnectorApi()
    private val sessionStore = SecureSessionStore(application)
    private val updates = AndroidUpdateService()
    private val mainHandler = Handler(Looper.getMainLooper())
    var state by mutableStateOf(ConnectorUiState())
        private set

    init {
        checkForUpdates()
        sessionStore.load()?.let { saved ->
            state = state.copy(session = saved, loading = true)
            viewModelScope.launch {
                try {
                    val result = withContext(Dispatchers.IO) {
                        api.profile(saved.accessToken)
                    }
                    val session = saved.copy(profile = result)
                    sessionStore.save(session)
                    state = state.copy(session = session, loading = false, error = null)
                } catch (_: Exception) {
                    sessionStore.clear()
                    state = ConnectorUiState(error = "登录已过期，请重新登录")
                }
            }
        }
    }

    fun setEmail(value: String) { state = state.copy(email = value, error = null) }
    fun setCode(value: String) { state = state.copy(code = value.filter(Char::isDigit).take(6), error = null) }
    fun setPassword(value: String) { state = state.copy(password = value.take(128), error = null) }
    fun setConfirmPassword(value: String) { state = state.copy(confirmPassword = value.take(128), error = null) }
    fun setAuthMode(value: Int) { state = state.copy(authMode = value, error = null, notice = "") }
    fun setInput(value: String) { state = state.copy(input = value) }

    fun requestCode() = runBusy {
        val email = normalizedEmail()
        withContext(Dispatchers.IO) { api.requestCode(email) }
        state = state.copy(notice = "验证码已发送，请检查邮箱")
    }

    fun passwordLogin() = runBusy {
        val email = normalizedEmail()
        require(validPassword(state.password)) { "密码应为 8 至 128 个字符，并同时包含字母和数字" }
        finishLogin(withContext(Dispatchers.IO) { api.login(email, state.password) })
    }

    fun register() = runBusy {
        val email = normalizedEmail()
        require(validPassword(state.password)) { "密码应为 8 至 128 个字符，并同时包含字母和数字" }
        require(state.password == state.confirmPassword) { "两次输入的密码不一致" }
        require(state.code.length == 6) { "请输入邮件中的 6 位验证码" }
        finishLogin(withContext(Dispatchers.IO) { api.register(email, state.password, state.code) })
    }

    fun resetPassword() = runBusy {
        val email = normalizedEmail()
        require(validPassword(state.password)) { "密码应为 8 至 128 个字符，并同时包含字母和数字" }
        require(state.password == state.confirmPassword) { "两次输入的密码不一致" }
        require(state.code.length == 6) { "请输入邮件中的 6 位验证码" }
        finishLogin(withContext(Dispatchers.IO) { api.resetPassword(email, state.password, state.code) })
    }

    fun installUpdate() = runBusy {
        val update = state.availableUpdate ?: return@runBusy
        val apk = withContext(Dispatchers.IO) { updates.download(getApplication(), update) }
        updates.openInstaller(getApplication(), apk)
        state = state.copy(notice = "安装程序已打开，请确认覆盖安装；旧版本会由 Android 自动替换")
    }

    fun codeLogin() = runBusy {
        val email = normalizedEmail()
        require(state.code.length == 6) { "请输入邮件中的 6 位验证码" }
        finishLogin(withContext(Dispatchers.IO) { api.verify(email, state.code) })
    }

    private suspend fun finishLogin(session: AccountSession) {
        sessionStore.save(session)
        state = state.copy(session = session, conversationId = null, messages = emptyList(), loading = false, error = null)
    }

    private fun normalizedEmail(): String {
        val email = state.email.trim().lowercase()
        val format = Regex("""^[a-z0-9.!#${'$'}%&'*+/=?^_`{|}~-]{1,64}@[a-z0-9.-]{3,189}${'$'}""")
        val local = email.substringBeforeLast('@', "")
        val labels = email.substringAfterLast('@', "").split('.')
        require(email.length <= 254 && format.matches(email) && !local.startsWith('.') && !local.endsWith('.') && ".." !in local
            && labels.size > 1 && labels.all { it.isNotEmpty() && it.length <= 63 && !it.startsWith('-') && !it.endsWith('-') }) {
            "请输入完整、有效的邮箱地址，例如 name@example.com"
        }
        return email
    }

    private fun validPassword(value: String) = value.length in 8..128 && value.any(Char::isLetter) && value.any(Char::isDigit)

    private fun checkForUpdates() {
        viewModelScope.launch {
            runCatching { withContext(Dispatchers.IO) { updates.check(BuildConfig.VERSION_NAME) } }
                .onSuccess { update -> state = state.copy(availableUpdate = update) }
        }
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
                val result = withContext(Dispatchers.IO) {
                    api.streamResponse(session.accessToken, context) { delta ->
                        mainHandler.post {
                            state = state.copy(messages = state.messages.map {
                                if (it.id == assistantId) it.copy(content = it.content + delta) else it
                            })
                        }
                    }
                }
                val assistant = placeholder.copy(content = result.text.ifBlank { "暂时没有收到模型输出。" }, citations = result.citations)
                state = state.copy(
                    messages = state.messages.map { if (it.id == assistantId) assistant else it },
                    loading = false,
                    notice = if (result.webSearchUnavailable) "联网服务暂不可用，本次已自动使用普通回答。" else "",
                )
            } catch (error: Exception) {
                state = state.copy(messages = state.messages.filterNot { it.id == assistantId }, loading = false, error = error.message ?: "发送失败")
            }
        }
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
