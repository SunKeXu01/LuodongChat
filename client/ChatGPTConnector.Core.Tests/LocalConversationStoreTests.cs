using ChatGPTConnector.Core;
using Xunit;

namespace ChatGPTConnector.Core.Tests;

public sealed class LocalConversationStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"luodong-chat-tests-{Guid.NewGuid():N}");

    [Fact]
    public async Task SavesListsAndDeletesConversationsInsideTheConfiguredRoot()
    {
        var store = new LocalConversationStore(_root);
        var message = new SyncedChatMessage(Guid.NewGuid().ToString(), Guid.NewGuid().ToString(), "user", "你好", DateTimeOffset.UtcNow);
        var conversation = new LocalConversation(message.ConversationId, "第一段对话", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, [message]);
        await store.SaveAsync("account-a", conversation);

        var loaded = await store.LoadAsync("account-a");
        Assert.Single(loaded);
        Assert.Equal("你好", loaded[0].Messages[0].Content);
        Assert.Empty(await store.LoadAsync("account-b"));

        await store.DeleteAsync("account-a", conversation.Id);
        Assert.Empty(await store.LoadAsync("account-a"));
    }

    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
}
