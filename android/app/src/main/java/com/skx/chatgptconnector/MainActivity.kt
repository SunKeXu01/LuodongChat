package com.skx.chatgptconnector

import android.os.Bundle
import androidx.activity.ComponentActivity
import androidx.activity.compose.setContent
import androidx.compose.foundation.background
import androidx.compose.foundation.layout.*
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.automirrored.filled.Send
import androidx.compose.material.icons.filled.Add
import androidx.compose.material3.*
import androidx.compose.runtime.Composable
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.input.KeyboardType
import androidx.compose.ui.text.input.PasswordVisualTransformation
import androidx.compose.foundation.text.KeyboardOptions
import androidx.compose.ui.unit.dp
import androidx.lifecycle.viewmodel.compose.viewModel

class MainActivity : ComponentActivity() {
    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        setContent {
            MaterialTheme(colorScheme = lightColorScheme(primary = Color(0xFF0F766E))) {
                val model: ConnectorViewModel = viewModel()
                Surface(Modifier.fillMaxSize()) {
                    if (model.state.session == null) LoginScreen(model) else ChatScreen(model)
                }
            }
        }
    }
}

@OptIn(ExperimentalMaterial3Api::class)
@Composable
private fun LoginScreen(model: ConnectorViewModel) {
    val state = model.state
    Column(Modifier.fillMaxSize().background(Color(0xFFF4F7FB)).padding(22.dp), verticalArrangement = Arrangement.Center) {
        Text("ChatGPT 连接器", style = MaterialTheme.typography.headlineMedium, fontWeight = FontWeight.Bold)
        Text("安全登录并同步 Windows 与 Android 数据", color = Color.Gray, modifier = Modifier.padding(top = 6.dp, bottom = 20.dp))
        Card(colors = CardDefaults.cardColors(containerColor = Color.White), elevation = CardDefaults.cardElevation(2.dp)) {
            Column(Modifier.padding(20.dp)) {
                PrimaryTabRow(selectedTabIndex = state.authMode) {
                    listOf("密码登录", "注册", "验证码").forEachIndexed { index, title ->
                        Tab(selected = state.authMode == index, onClick = { model.setAuthMode(index) }, text = { Text(title) })
                    }
                }
                OutlinedTextField(
                    state.email, model::setEmail, Modifier.fillMaxWidth().padding(top = 18.dp),
                    label = { Text("邮箱") }, enabled = !state.loading, singleLine = true,
                    keyboardOptions = KeyboardOptions(keyboardType = KeyboardType.Email),
                )
                if (state.authMode != 2) {
                    OutlinedTextField(
                        state.password, model::setPassword, Modifier.fillMaxWidth().padding(top = 12.dp),
                        label = { Text("密码") }, enabled = !state.loading, singleLine = true,
                        visualTransformation = PasswordVisualTransformation(), keyboardOptions = KeyboardOptions(keyboardType = KeyboardType.Password),
                    )
                }
                if (state.authMode == 1) {
                    OutlinedTextField(
                        state.confirmPassword, model::setConfirmPassword, Modifier.fillMaxWidth().padding(top = 12.dp),
                        label = { Text("确认密码") }, enabled = !state.loading, singleLine = true,
                        visualTransformation = PasswordVisualTransformation(), keyboardOptions = KeyboardOptions(keyboardType = KeyboardType.Password),
                    )
                }
                if (state.authMode != 0) {
                    OutlinedTextField(
                        state.code, model::setCode, Modifier.fillMaxWidth().padding(top = 12.dp),
                        label = { Text("6 位邮箱验证码") }, enabled = !state.loading, singleLine = true,
                        keyboardOptions = KeyboardOptions(keyboardType = KeyboardType.Number),
                    )
                    OutlinedButton(model::requestCode, Modifier.fillMaxWidth().padding(top = 12.dp), enabled = !state.loading) { Text("获取验证码") }
                }
                Button(
                    onClick = when (state.authMode) { 0 -> model::passwordLogin; 1 -> model::register; else -> model::codeLogin },
                    modifier = Modifier.fillMaxWidth().padding(top = 16.dp), enabled = !state.loading,
                ) { Text(when (state.authMode) { 0 -> "登录"; 1 -> "注册 / 设置密码"; else -> "使用验证码登录" }) }
            }
        }
        StatusText(state)
    }
}

@OptIn(ExperimentalMaterial3Api::class)
@Composable
private fun ChatScreen(model: ConnectorViewModel) {
    val state = model.state
    Scaffold(
        topBar = {
            TopAppBar(
                title = { Column { Text("GPT-5.6", fontWeight = FontWeight.SemiBold); Text(state.session?.profile?.email.orEmpty(), style = MaterialTheme.typography.labelSmall, color = Color.Gray) } },
                actions = {
                    IconButton(model::newConversation, enabled = !state.loading) { Icon(Icons.Default.Add, "新建会话") }
                    TextButton(model::logout, enabled = !state.loading) { Text("退出") }
                },
            )
        },
    ) { padding ->
        Column(Modifier.fillMaxSize().padding(padding)) {
            LazyColumn(
                modifier = Modifier.weight(1f).fillMaxWidth().padding(horizontal = 14.dp),
                contentPadding = PaddingValues(vertical = 12.dp),
                verticalArrangement = Arrangement.spacedBy(10.dp),
            ) {
                if (state.messages.isEmpty()) item { Text("开始一段新对话吧。", color = Color.Gray, modifier = Modifier.padding(16.dp)) }
                items(state.messages, key = { it.id }) { MessageBubble(it) }
            }
            StatusText(state)
            Row(Modifier.fillMaxWidth().padding(12.dp), verticalAlignment = Alignment.Bottom) {
                OutlinedTextField(
                    state.input, model::setInput, Modifier.weight(1f),
                    placeholder = { Text("输入消息") }, enabled = !state.loading, maxLines = 5,
                )
                IconButton(model::send, enabled = state.input.isNotBlank() && !state.loading, modifier = Modifier.padding(start = 6.dp)) {
                    Icon(Icons.AutoMirrored.Filled.Send, "发送")
                }
            }
        }
    }
}

@Composable
private fun MessageBubble(message: ChatMessage) {
    val user = message.role == "user"
    Row(Modifier.fillMaxWidth(), horizontalArrangement = if (user) Arrangement.End else Arrangement.Start) {
        Text(
            message.content.ifBlank { "正在思考…" },
            modifier = Modifier.widthIn(max = 320.dp).background(
                if (user) Color(0xFFDDF7F1) else Color(0xFFF1F3F5), RoundedCornerShape(14.dp),
            ).padding(horizontal = 14.dp, vertical = 10.dp),
            color = Color(0xFF1F2937),
        )
    }
}

@Composable
private fun StatusText(state: ConnectorUiState) {
    if (state.loading) LinearProgressIndicator(Modifier.fillMaxWidth().padding(top = 12.dp))
    state.error?.let { Text(it, color = MaterialTheme.colorScheme.error, modifier = Modifier.padding(14.dp)) }
    if (state.notice.isNotBlank()) Text(state.notice, color = Color(0xFF047857), modifier = Modifier.padding(14.dp))
}
