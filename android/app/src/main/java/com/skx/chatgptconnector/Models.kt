package com.skx.chatgptconnector

data class AccountProfile(
    val id: String,
    val email: String,
    val nickname: String,
    val avatarMediaType: String? = null,
    val avatarBase64: String? = null,
    val balanceMicrounits: Long = 0,
)

data class AccountSession(val accessToken: String, val profile: AccountProfile)

data class ChatCitation(val title: String, val url: String)
data class ChatStreamResult(val text: String, val citations: List<ChatCitation>, val webSearchUnavailable: Boolean = false)

data class ChatMessage(
    val id: String,
    val conversationId: String,
    val role: String,
    val content: String,
    val clientCreatedAt: String,
    val citations: List<ChatCitation> = emptyList(),
)
