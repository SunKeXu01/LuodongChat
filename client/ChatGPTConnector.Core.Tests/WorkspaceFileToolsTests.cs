using System.Text.Json;
using ChatGPTConnector.Core;
using Xunit;

namespace ChatGPTConnector.Core.Tests;

public sealed class WorkspaceFileToolsTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "luodongchat-workspace-tools-" + Guid.NewGuid().ToString("N"));

    public WorkspaceFileToolsTests() => Directory.CreateDirectory(_root);

    [Fact]
    public async Task ReadsAndSearchesOnlyInsideSelectedWorkspace()
    {
        Directory.CreateDirectory(Path.Combine(_root, "src"));
        await File.WriteAllTextAsync(Path.Combine(_root, "src", "Program.cs"), "line one\nline two\nline three");
        var tools = new WorkspaceFileTools(_root);

        var search = await tools.ExecuteAsync(Call("search_files", new { path = "", query = "program" }));
        var read = await tools.ExecuteAsync(Call("read_text_file", new { path = "src/Program.cs", start_line = 2, line_count = 1 }));

        Assert.Contains("src/Program.cs", search);
        Assert.Contains("line two", read);
        Assert.DoesNotContain("line one", read);
    }

    [Fact]
    public async Task RejectsTraversalAndSensitiveFiles()
    {
        await File.WriteAllTextAsync(Path.Combine(_root, ".env"), "OPENAI_API_KEY=secret");
        var tools = new WorkspaceFileTools(_root);

        var traversal = await tools.ExecuteAsync(Call("read_text_file", new { path = "../outside.txt", start_line = 1, line_count = 20 }));
        var sensitive = await tools.ExecuteAsync(Call("read_text_file", new { path = ".env", start_line = 1, line_count = 20 }));

        Assert.Contains("超出了", JsonDocument.Parse(traversal).RootElement.GetProperty("error").GetString());
        Assert.DoesNotContain("secret", sensitive);
        Assert.Contains("不允许读取", JsonDocument.Parse(sensitive).RootElement.GetProperty("error").GetString());
    }

    [Fact]
    public async Task WritesAndReplacesTextAfterCallerApproval()
    {
        var tools = new WorkspaceFileTools(_root);
        var writeCall = Call("write_text_file", new { path = "notes/todo.md", content = "old value" });
        Assert.True(tools.Describe(writeCall).RequiresApproval);

        var written = await tools.ExecuteAsync(writeCall);
        var replaced = await tools.ExecuteAsync(Call("replace_in_file", new
        {
            path = "notes/todo.md", old_text = "old", new_text = "new", replace_all = false,
        }));

        Assert.Contains("\"ok\":true", written);
        Assert.Contains("\"replacements\":1", replaced);
        Assert.Equal("new value", await File.ReadAllTextAsync(Path.Combine(_root, "notes", "todo.md")));
    }

    [Fact]
    public async Task RefusesDeletingNonEmptyDirectory()
    {
        Directory.CreateDirectory(Path.Combine(_root, "data"));
        await File.WriteAllTextAsync(Path.Combine(_root, "data", "keep.txt"), "keep");
        var result = await new WorkspaceFileTools(_root).ExecuteAsync(Call("delete_path", new { path = "data" }));
        Assert.Contains("只允许删除空目录", JsonDocument.Parse(result).RootElement.GetProperty("error").GetString());
        Assert.True(File.Exists(Path.Combine(_root, "data", "keep.txt")));
    }

    [Fact]
    public async Task RejectsSymbolicLinkEscapeWhenPlatformSupportsLinks()
    {
        var outside = Path.Combine(Path.GetTempPath(), "luodongchat-outside-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(outside);
        await File.WriteAllTextAsync(Path.Combine(outside, "outside.txt"), "must stay private");
        try
        {
            try { Directory.CreateSymbolicLink(Path.Combine(_root, "linked"), outside); }
            catch (Exception error) when (error is UnauthorizedAccessException or IOException or PlatformNotSupportedException) { return; }
            var result = await new WorkspaceFileTools(_root).ExecuteAsync(
                Call("read_text_file", new { path = "linked/outside.txt", start_line = 1, line_count = 20 }));
            var parsed = JsonDocument.Parse(result).RootElement;
            Assert.False(parsed.GetProperty("ok").GetBoolean());
            Assert.DoesNotContain("must stay private", result);
        }
        finally { try { Directory.Delete(outside, true); } catch { } }
    }

    [Fact]
    public async Task RunsApprovedDevelopmentCommandInWorkspace()
    {
        var tools = new WorkspaceFileTools(_root);
        var call = Call("run_command", new { command = "echo luodong", working_directory = "", shell = "cmd", timeout_seconds = 20 });
        var plan = tools.Describe(call);

        var result = await tools.ExecuteAsync(call);
        using var document = JsonDocument.Parse(result);

        Assert.True(plan.RequiresApproval);
        Assert.Equal(0, document.RootElement.GetProperty("result").GetProperty("exit_code").GetInt32());
        Assert.Contains("luodong", document.RootElement.GetProperty("result").GetProperty("stdout").GetString(), StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("shutdown /s /t 0")]
    [InlineData("git push origin main")]
    [InlineData("rm -rf .")]
    [InlineData("curl https://example.com")]
    public async Task BlocksHighRiskCommandsEvenAfterToolCallIsReceived(string command)
    {
        var result = await new WorkspaceFileTools(_root).ExecuteAsync(Call("run_command", new
        {
            command, working_directory = "", shell = "cmd", timeout_seconds = 20,
        }));
        using var document = JsonDocument.Parse(result);
        Assert.False(document.RootElement.GetProperty("ok").GetBoolean());
        Assert.Contains("安全策略阻止", document.RootElement.GetProperty("error").GetString());
    }

    private static LocalToolCall Call(string name, object arguments) =>
        new("call_test", name, JsonSerializer.Serialize(arguments));

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { }
    }
}
