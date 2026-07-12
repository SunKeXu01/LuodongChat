using System.Text.Json;
using ChatGPTConnector.Core;
using Xunit;

namespace ChatGPTConnector.Core.Tests;

public sealed class CodexConfigInstallerTests : IDisposable
{
    private readonly string _temporaryHome = Path.Combine(Path.GetTempPath(), $"connector-{Guid.NewGuid():N}");

    [Fact]
    public async Task BacksUpAndAtomicallyWritesBothCodexFiles()
    {
        var paths = new CodexPaths(_temporaryHome);
        Directory.CreateDirectory(paths.CodexDirectory);
        await File.WriteAllTextAsync(paths.ConfigPath, "model = \"old\"\n");
        await File.WriteAllTextAsync(paths.AuthPath, "{\"OLD\":true}\n");
        var plan = new ConfigurationPlan(
            "model = \"new\"\n",
            "{\"OPENAI_API_KEY\":\"gw_test\"}\n",
            ["model", "auth.OPENAI_API_KEY"],
            []);

        var result = await new CodexConfigInstaller("0.1.0-test").ApplyAsync(paths, plan);

        Assert.Equal("model = \"new\"\n", await File.ReadAllTextAsync(paths.ConfigPath));
        Assert.Contains("gw_test", await File.ReadAllTextAsync(paths.AuthPath));
        Assert.Equal("model = \"old\"\n", await File.ReadAllTextAsync(result.Manifest.Config.BackupPath!));
        Assert.True(result.Manifest.Config.Existed);
        Assert.Equal(64, result.Manifest.Config.OriginalSha256!.Length);
        var stored = JsonSerializer.Deserialize<BackupManifest>(
            await File.ReadAllTextAsync(Path.Combine(result.BackupDirectory, "manifest.json")));
        Assert.Equal(result.Manifest.Id, stored!.Id);
    }

    [Fact]
    public async Task RefusesConcurrentConfigurationModification()
    {
        var paths = new CodexPaths(_temporaryHome);
        var state = Path.Combine(paths.CodexDirectory, ".chatgpt-connector");
        Directory.CreateDirectory(state);
        await File.WriteAllTextAsync(Path.Combine(state, "config.lock"), "held");
        var plan = new ConfigurationPlan(string.Empty, "{}", [], []);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new CodexConfigInstaller("test").ApplyAsync(paths, plan));
    }

    public void Dispose()
    {
        if (Directory.Exists(_temporaryHome)) Directory.Delete(_temporaryHome, true);
        GC.SuppressFinalize(this);
    }
}
