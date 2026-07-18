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
        var containment = document.RootElement.GetProperty("result").GetProperty("process_containment").GetString();
        if (OperatingSystem.IsWindows()) Assert.StartsWith("windows_job_", containment);
        else Assert.Equal("process_tree", containment);
    }

    [Theory]
    [InlineData("shutdown /s /t 0")]
    [InlineData("powershell -EncodedCommand ZQBjAGgAbwAgAHgA")]
    [InlineData("schtasks /create /tn test /tr calc.exe /sc once /st 23:59")]
    public async Task AlwaysBlocksPrivilegeAndSystemCommands(string command)
    {
        var result = await new WorkspaceFileTools(_root).ExecuteAsync(Call("run_command", new
        {
            command, working_directory = "", shell = "cmd", timeout_seconds = 20,
        }));
        using var document = JsonDocument.Parse(result);
        Assert.False(document.RootElement.GetProperty("ok").GetBoolean());
        Assert.Contains("任何访问模式", document.RootElement.GetProperty("error").GetString());
    }

    [Fact]
    public void ClassifiesInternetDownloadAndAppliesSelectedApprovalMode()
    {
        var tools = new WorkspaceFileTools(_root);
        var call = Call("run_command", new
        {
            command = "curl.exe -L https://example.com/image.jpg -o image.jpg",
            working_directory = "",
            shell = "cmd",
            timeout_seconds = 20,
        });

        var plan = tools.Describe(call);

        Assert.Equal(WorkspaceOperationRisk.Network, plan.Risk);
        Assert.True(WorkspaceAccessPolicy.RequiresApproval(WorkspaceAccessMode.RequestApproval, plan));
        Assert.True(WorkspaceAccessPolicy.RequiresApproval(WorkspaceAccessMode.RiskBased, plan));
        Assert.False(WorkspaceAccessPolicy.RequiresApproval(WorkspaceAccessMode.FullAccess, plan));
    }

    [Theory]
    [InlineData("git clone https://github.com/example/repo.git")]
    [InlineData("npm install react")]
    [InlineData("dotnet restore")]
    public void ClassifiesDependencyAndRepositoryDownloadsAsNetwork(string command)
    {
        var plan = new WorkspaceFileTools(_root).Describe(Call("run_command", new
        {
            command, working_directory = "", shell = "cmd", timeout_seconds = 120,
        }));

        Assert.Equal(WorkspaceOperationRisk.Network, plan.Risk);
    }

    [Fact]
    public void KeepsDestructiveCommandsApprovalGatedExceptInFullAccess()
    {
        var plan = new WorkspaceFileTools(_root).Describe(Call("run_command", new
        {
            command = "git push origin main",
            working_directory = "",
            shell = "cmd",
            timeout_seconds = 20,
        }));

        Assert.Equal(WorkspaceOperationRisk.Destructive, plan.Risk);
        Assert.True(WorkspaceAccessPolicy.RequiresApproval(WorkspaceAccessMode.RiskBased, plan));
        Assert.False(WorkspaceAccessPolicy.RequiresApproval(WorkspaceAccessMode.FullAccess, plan));
    }

    [Fact]
    public void RiskBasedModeAsksBeforeEveryLocalCommand()
    {
        var plan = new WorkspaceFileTools(_root).Describe(Call("run_command", new
        {
            command = "dotnet test",
            working_directory = "",
            shell = "cmd",
            timeout_seconds = 120,
        }));

        Assert.Equal(WorkspaceOperationRisk.Write, plan.Risk);
        Assert.True(WorkspaceAccessPolicy.RequiresApproval(WorkspaceAccessMode.RiskBased, plan));
        Assert.False(WorkspaceAccessPolicy.RequiresApproval(WorkspaceAccessMode.FullAccess, plan));
    }

    [Fact]
    public void CommandApprovalContextBindsShellAndTimeout()
    {
        var tools = new WorkspaceFileTools(_root);
        var cmd = tools.Describe(Call("run_command", new
        {
            command = "echo hello", working_directory = "", shell = "cmd", timeout_seconds = 20,
        }));
        var powershell = tools.Describe(Call("run_command", new
        {
            command = "echo hello", working_directory = "", shell = "powershell", timeout_seconds = 20,
        }));
        var longer = tools.Describe(Call("run_command", new
        {
            command = "echo hello", working_directory = "", shell = "cmd", timeout_seconds = 120,
        }));

        Assert.NotEqual(cmd.Summary, powershell.Summary);
        Assert.NotEqual(cmd.Summary, longer.Summary);
    }

    [Fact]
    public async Task ExposesControlledApplicationLifecycleTools()
    {
        var definitions = JsonSerializer.Serialize(WorkspaceFileTools.ToolDefinitions);
        Assert.Contains("launch_application", definitions);
        Assert.Contains("list_launched_applications", definitions);
        Assert.Contains("stop_launched_application", definitions);

        Directory.CreateDirectory(Path.Combine(_root, "bin"));
        await File.WriteAllBytesAsync(Path.Combine(_root, "bin", "Demo.exe"), [0x4d, 0x5a, 0x00]);
        var tools = new WorkspaceFileTools(_root);
        var launch = tools.Describe(Call("launch_application", new
        {
            path = "bin/Demo.exe",
            arguments = new[] { "--project", "sample path" },
            working_directory = "bin",
        }));
        Assert.Equal(WorkspaceOperationRisk.Write, launch.Risk);
        Assert.True(WorkspaceAccessPolicy.RequiresApproval(WorkspaceAccessMode.RiskBased, launch));
        Assert.False(WorkspaceAccessPolicy.RequiresApproval(WorkspaceAccessMode.FullAccess, launch));
        Assert.Contains("Demo.exe", launch.Summary);
        Assert.Contains("sample path", launch.Summary);
        await File.WriteAllBytesAsync(Path.Combine(_root, "bin", "Demo.exe"), [0x4d, 0x5a, 0x01]);
        var changedExecutable = tools.Describe(Call("launch_application", new
        {
            path = "bin/Demo.exe", arguments = new[] { "--project", "sample path" }, working_directory = "bin",
        }));
        Assert.NotEqual(launch.ApprovalBinding, changedExecutable.ApprovalBinding);

        var listed = await tools.ExecuteAsync(Call("list_launched_applications", new { }));
        Assert.True(JsonDocument.Parse(listed).RootElement.GetProperty("ok").GetBoolean());
        Assert.Empty(JsonDocument.Parse(listed).RootElement.GetProperty("result").EnumerateArray());
    }

    [Theory]
    [InlineData("python -c \"print('hello')\"")]
    [InlineData("node -e \"console.log('hello')\"")]
    [InlineData("powershell.exe -Command Get-ChildItem")]
    [InlineData("cmd.exe /c echo hello")]
    public void InlineInterpreterCommandsCannotBePersistentlyApproved(string command)
    {
        var plan = new WorkspaceFileTools(_root).Describe(Call("run_command", new
        {
            command, working_directory = "", shell = "cmd", timeout_seconds = 30,
        }));

        Assert.False(plan.AllowsPersistentApproval);
    }

    [Fact]
    public async Task ScriptChangesInvalidateAnApprovedPlan()
    {
        var script = Path.Combine(_root, "build.py");
        await File.WriteAllTextAsync(script, "print('first')");
        var tools = new WorkspaceFileTools(_root);
        var call = Call("run_command", new
        {
            command = "python build.py", working_directory = "", shell = "cmd", timeout_seconds = 30,
        });
        var approved = tools.Describe(call);
        Assert.NotNull(approved.ApprovalBinding);

        await File.WriteAllTextAsync(script, "print('changed')");
        var result = await tools.ExecuteAsync(call, approvedPlan: approved);
        using var document = JsonDocument.Parse(result);
        Assert.False(document.RootElement.GetProperty("ok").GetBoolean());
        Assert.Contains("批准后执行内容发生变化", document.RootElement.GetProperty("error").GetString());
    }

    [Fact]
    public async Task CannotStopApplicationOutsideCurrentProjectRegistry()
    {
        var result = await new WorkspaceFileTools(_root).ExecuteAsync(Call(
            "stop_launched_application", new { process_id = int.MaxValue }));
        using var document = JsonDocument.Parse(result);
        Assert.False(document.RootElement.GetProperty("ok").GetBoolean());
        Assert.Contains("没有找到当前项目", document.RootElement.GetProperty("error").GetString());
    }

    [Fact]
    public void CustomPermissionsEvaluateEachRiskCategoryIndependently()
    {
        var settings = new WorkspaceCustomPermissions(
            Read: WorkspacePermissionDecision.Deny,
            Write: WorkspacePermissionDecision.Allow,
            Network: WorkspacePermissionDecision.Ask,
            Destructive: WorkspacePermissionDecision.Deny);

        Assert.Equal(WorkspacePermissionDecision.Deny, WorkspaceAccessPolicy.Decide(
            WorkspaceAccessMode.Custom, new WorkspaceToolPlan("read", "read", false, []), settings));
        Assert.Equal(WorkspacePermissionDecision.Allow, WorkspaceAccessPolicy.Decide(
            WorkspaceAccessMode.Custom, new WorkspaceToolPlan("write", "write", true, [], WorkspaceOperationRisk.Write), settings));
        Assert.Equal(WorkspacePermissionDecision.Ask, WorkspaceAccessPolicy.Decide(
            WorkspaceAccessMode.Custom, new WorkspaceToolPlan("web", "web", true, [], WorkspaceOperationRisk.Network), settings));
        Assert.Equal(WorkspacePermissionDecision.Deny, WorkspaceAccessPolicy.Decide(
            WorkspaceAccessMode.Custom, new WorkspaceToolPlan("delete", "delete", true, [], WorkspaceOperationRisk.Destructive), settings));
    }

    [Fact]
    public void MoveAndWriteToolsAreClassifiedAsWriteOperations()
    {
        var tools = new WorkspaceFileTools(_root);
        var write = tools.Describe(Call("write_text_file", new { path = "a.txt", content = "a" }));
        var move = tools.Describe(Call("move_path", new { source = "a.txt", destination = "b.txt" }));

        Assert.Equal(WorkspaceOperationRisk.Write, write.Risk);
        Assert.Equal(WorkspaceOperationRisk.Write, move.Risk);
    }

    [Fact]
    public void PersistsWorkspaceAccessSettings()
    {
        var path = Path.Combine(_root, "settings", "workspace-access.json");
        var store = new WorkspaceAccessSettingsStore(path);
        var expected = new WorkspaceAccessSettings(
            WorkspaceAccessMode.Custom,
            new WorkspaceCustomPermissions(Network: WorkspacePermissionDecision.Deny));

        store.Save(_root, expected);
        var actual = store.Load(_root);

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void KeepsWorkspacePermissionsScopedToTheirProject()
    {
        var path = Path.Combine(_root, "settings", "workspace-access.json");
        var first = Path.Combine(_root, "first");
        var second = Path.Combine(_root, "second");
        Directory.CreateDirectory(first);
        Directory.CreateDirectory(second);
        var store = new WorkspaceAccessSettingsStore(path);
        store.Save(first, new WorkspaceAccessSettings(
            WorkspaceAccessMode.FullAccess, new WorkspaceCustomPermissions()));

        Assert.Equal(WorkspaceAccessMode.FullAccess, store.Load(first).Mode);
        Assert.Equal(WorkspaceAccessMode.RequestApproval, store.Load(second).Mode);
    }

    [Fact]
    public void PersistsCommandAllowlistHashesOnlyForTheirProject()
    {
        var path = Path.Combine(_root, "settings", "workspace-access.json");
        var first = Path.Combine(_root, "first");
        var second = Path.Combine(_root, "second");
        Directory.CreateDirectory(first);
        Directory.CreateDirectory(second);
        var store = new WorkspaceAccessSettingsStore(path);
        store.Save(first, new WorkspaceAccessSettings(
            WorkspaceAccessMode.RiskBased,
            new WorkspaceCustomPermissions(),
            ["ABC123"]));

        Assert.Equal(["ABC123"], store.Load(first).AlwaysAllowedCommandHashes);
        Assert.Null(store.Load(second).AlwaysAllowedCommandHashes);
        Assert.DoesNotContain("dotnet test", File.ReadAllText(path));
    }

    [Fact]
    public void WorkspaceAuditIsProjectScopedAndRedactsSecrets()
    {
        var audit = new WorkspaceAuditStore(Path.Combine(_root, "logs", "audit.jsonl"));
        var other = Path.Combine(_root, "other");
        Directory.CreateDirectory(other);
        audit.Append(new WorkspaceAuditEntry(DateTimeOffset.UtcNow, _root, "run_command",
            "curl -H Authorization=sk-testsecret123456789 https://example.com", WorkspaceOperationRisk.Network,
            "本次允许", "执行成功"));
        audit.Append(new WorkspaceAuditEntry(DateTimeOffset.UtcNow, other, "read_text_file",
            "读取 other.txt", WorkspaceOperationRisk.Read, "自动允许", "执行成功"));

        var entries = audit.Load(_root);

        Assert.Single(entries);
        Assert.DoesNotContain("sk-testsecret123456789", entries[0].Summary);
        Assert.Contains("已遮盖", entries[0].Summary);
        audit.Clear(_root);
        Assert.Empty(audit.Load(_root));
        Assert.Single(audit.Load(other));
    }

    private static LocalToolCall Call(string name, object arguments) =>
        new("call_test", name, JsonSerializer.Serialize(arguments));

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { }
    }
}
