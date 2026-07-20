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
        var conversation = new LocalConversation(message.ConversationId, "第一段对话", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow,
            [message], projectPath, McpToolMode.Specified, ["mcp__files__read_file"]);
        await store.SaveAsync("account-a", conversation);

        var loaded = await store.LoadAsync("account-a");
        Assert.Single(loaded);
        Assert.Equal("你好", loaded[0].Messages[0].Content);
        Assert.Equal(projectPath, loaded[0].ProjectPath);
        Assert.Equal(McpToolMode.Specified, loaded[0].ToolMode);
        Assert.Equal(["mcp__files__read_file"], loaded[0].SelectedMcpToolNames);
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

    [Fact]
    public async Task StoresMultipleMessageAttachmentsWithUniqueLocalPathsAndDeletesThemWithTheConversation()
    {
        var store = new LocalConversationStore(_root);
        var conversationId = Guid.NewGuid().ToString();
        var messageId = Guid.NewGuid().ToString();
        var firstSource = Path.Combine(_root, "first.txt");
        var secondSource = Path.Combine(_root, "second.txt");
        Directory.CreateDirectory(_root);
        await File.WriteAllTextAsync(firstSource, "first");
        await File.WriteAllTextAsync(secondSource, "second");

        var first = await store.SaveAttachmentAsync("account-a", conversationId, messageId, firstSource, "first.txt", "text/plain", 5, "document");
        var second = await store.SaveAttachmentAsync("account-a", conversationId, messageId, secondSource, "second.txt", "text/plain", 6, "document");

        Assert.NotEqual(first.RelativePath, second.RelativePath);
        var firstPath = store.GetAttachmentPath("account-a", first.RelativePath);
        var secondPath = store.GetAttachmentPath("account-a", second.RelativePath);
        Assert.Equal("first", await File.ReadAllTextAsync(firstPath));
        Assert.Equal("second", await File.ReadAllTextAsync(secondPath));
        await store.DeleteAsync("account-a", conversationId);
        Assert.False(File.Exists(firstPath));
        Assert.False(File.Exists(secondPath));
    }

    [Theory]
    [InlineData("请生成一张夜空图片")]
    [InlineData("你能生成一张日落雪山图片吗？")]
    [InlineData("帮我生成一张猫的图片")]
    [InlineData("Draw an image of a mountain")]
    [InlineData("Can you generate an image of a mountain at sunset?")]
    public void DetectsExplicitImageRequests(string text) => Assert.True(ImageGenerationIntent.IsExplicit(text));

    [Theory]
    [InlineData("解释一下图片压缩算法")]
    [InlineData("你好，能生成图片吗？")]
    [InlineData("你好，能更生成图片吗？")]
    [InlineData("你支持生图吗？")]
    [InlineData("Can you generate images?")]
    [InlineData("Do you support image generation?")]
    public void DoesNotTreatConversationOrCapabilityQuestionsAsImageGeneration(string text) =>
        Assert.False(ImageGenerationIntent.IsExplicit(text));

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
