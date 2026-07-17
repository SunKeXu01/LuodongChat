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
        var projectPath = Path.Combine(_root, "project");
        var conversation = new LocalConversation(message.ConversationId, "第一段对话", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, [message], projectPath);
        await store.SaveAsync("account-a", conversation);

        var loaded = await store.LoadAsync("account-a");
        Assert.Single(loaded);
        Assert.Equal("你好", loaded[0].Messages[0].Content);
        Assert.Equal(projectPath, loaded[0].ProjectPath);
        Assert.Empty(await store.LoadAsync("account-b"));

        await store.DeleteAsync("account-a", conversation.Id);
        Assert.Empty(await store.LoadAsync("account-a"));
    }

    [Fact]
    public async Task StoresGeneratedImagesUnderTheAccountConversationDirectory()
    {
        var store = new LocalConversationStore(_root);
        var conversationId = Guid.NewGuid().ToString();
        var image = await store.SaveGeneratedImageAsync(
            "account-a", conversationId, Guid.NewGuid().ToString(),
            new GeneratedImageData(Convert.ToBase64String([1, 2, 3]), "image/png"));

        var path = store.GetImagePath("account-a", image.RelativePath);
        Assert.Equal([1, 2, 3], await File.ReadAllBytesAsync(path));
        await store.DeleteAsync("account-a", conversationId);
        Assert.False(File.Exists(path));
    }

    [Theory]
    [InlineData("请生成一张夜空图片")]
    [InlineData("Draw an image of a mountain")]
    public void DetectsExplicitImageRequests(string text) => Assert.True(ImageGenerationIntent.IsExplicit(text));

    [Fact]
    public void DoesNotTreatOrdinaryConversationAsImageGeneration() =>
        Assert.False(ImageGenerationIntent.IsExplicit("解释一下图片压缩算法"));

    [Fact]
    public async Task BuildsBoundedReadOnlyProjectContextAndExcludesSecretsAndDependencies()
    {
        var project = Path.Combine(_root, "示例项目");
        Directory.CreateDirectory(Path.Combine(project, "src"));
        Directory.CreateDirectory(Path.Combine(project, "node_modules", "package"));
        await File.WriteAllTextAsync(Path.Combine(project, "README.md"), "# 示例项目\n这是说明。");
        await File.WriteAllTextAsync(Path.Combine(project, "src", "App.cs"), "public sealed class App { }");
        await File.WriteAllTextAsync(Path.Combine(project, ".env"), "API_KEY=must-not-leak");
        await File.WriteAllTextAsync(Path.Combine(project, "appsettings.json"), "{\"ConnectionString\":\"must-not-leak-either\"}");
        await File.WriteAllTextAsync(Path.Combine(project, "client-secrets.txt"), "must-not-leak-by-name");
        await File.WriteAllTextAsync(Path.Combine(project, "node_modules", "package", "index.ts"), "must-not-index");

        var context = await new ProjectContextBuilder().BuildAsync(project, "解释 App 类");

        Assert.NotNull(context);
        Assert.Contains("src/App.cs", context.Content);
        Assert.Contains("public sealed class App", context.Content);
        Assert.DoesNotContain("must-not-leak", context.Content);
        Assert.DoesNotContain("must-not-leak-either", context.Content);
        Assert.DoesNotContain("must-not-leak-by-name", context.Content);
        Assert.DoesNotContain("must-not-index", context.Content);
        Assert.DoesNotContain(project, context.Content);
        Assert.InRange(context.Content.Length, 1, 65_000);
    }

    [Fact]
    public void RemembersRecentProjectsOnlyOnTheLocalMachine()
    {
        var first = Path.Combine(_root, "first");
        var second = Path.Combine(_root, "second");
        Directory.CreateDirectory(first); Directory.CreateDirectory(second);
        var store = new RecentProjectStore(Path.Combine(_root, "recent.json"));
        store.Remember(first); store.Remember(second); store.Remember(first);
        Assert.Equal([Path.GetFullPath(first), Path.GetFullPath(second)], store.Load());
    }

    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
}
