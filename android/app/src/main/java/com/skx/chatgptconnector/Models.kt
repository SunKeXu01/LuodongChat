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

data class ChatMessage(
    val id: String,
    val conversationId: String,
    val role: String,
    val content: String,
    val clientCreatedAt: String,
)

data class SyncedConversation(
    val id: String,
    val title: String,
    val deletedAt: String?,
)

data class SyncState(
    val conversations: List<SyncedConversation>,
    val messages: List<ChatMessage>,
    val serverTime: String,
)
