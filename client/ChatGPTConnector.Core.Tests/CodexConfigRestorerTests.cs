using System.Text.Json.Nodes;
using ChatGPTConnector.Core;
using Tomlyn;
using Tomlyn.Model;
using Xunit;

namespace ChatGPTConnector.Core.Tests;

public sealed class CodexConfigRestorerTests : IDisposable
{
    private readonly string _temporaryHome = Path.Combine(Path.GetTempPath(), $"connector-restore-{Guid.NewGuid():N}");

    [Fact]
    public async Task RestoresManagedFieldsAndPreservesUnrelatedChanges()
    {
        var paths = new CodexPaths(_temporaryHome);
        Directory.CreateDirectory(paths.CodexDirectory);
        const string beforeConfig = "model = \"old\"\ntheme = \"dark\"\n";
        const string beforeAuth = "{\"OTHER\":\"keep\"}\n";
        await File.WriteAllTextAsync(paths.ConfigPath, beforeConfig);
        await File.WriteAllTextAsync(paths.AuthPath, beforeAuth);
        var settings = new ConnectorSettings(new Uri("https://520skx.com"), "gw_test");
        var applied = new CodexConfigPlanner().CreatePlan(beforeConfig, beforeAuth, settings);
        var install = await new CodexConfigInstaller("test").ApplyAsync(paths, applied);

        var currentModel = TomlSerializer.Deserialize<TomlTable>(applied.UpdatedConfigToml)!;
        currentModel["user_setting"] = 42L;
        var currentConfig = TomlSerializer.Serialize(currentModel);
        var plan = new CodexConfigRestorer().CreatePlan(install.Manifest, currentConfig, applied.UpdatedAuthJson);
        var restoredConfig = TomlSerializer.Deserialize<TomlTable>(plan.UpdatedConfigToml)!;
        var restoredAuth = JsonNode.Parse(plan.UpdatedAuthJson)!.AsObject();

        Assert.Equal("old", restoredConfig["model"]);
        Assert.Equal("dark", restoredConfig["theme"]);
        Assert.Equal(42L, restoredConfig["user_setting"]);
        Assert.False(restoredConfig.ContainsKey("model_provider"));
        Assert.Equal("keep", restoredAuth["OTHER"]!.GetValue<string>());
        Assert.False(restoredAuth.ContainsKey("OPENAI_API_KEY"));
        Assert.Empty(plan.Conflicts);
    }

    [Fact]
    public async Task ReportsExternalChangesToManagedFieldsAsConflicts()
    {
        var paths = new CodexPaths(_temporaryHome);
        Directory.CreateDirectory(paths.CodexDirectory);
        await File.WriteAllTextAsync(paths.ConfigPath, "model = \"old\"\n");
        await File.WriteAllTextAsync(paths.AuthPath, "{}\n");
        var applied = new CodexConfigPlanner().CreatePlan(
            "model = \"old\"\n",
            "{}",
            new ConnectorSettings(new Uri("https://520skx.com"), "gw_test"));
        var install = await new CodexConfigInstaller("test").ApplyAsync(paths, applied);

        var plan = new CodexConfigRestorer().CreatePlan(
            install.Manifest,
            applied.UpdatedConfigToml.Replace("model = \"gpt-5.5\"", "model = \"user-choice\""),
            applied.UpdatedAuthJson);

        Assert.Contains("model", plan.Conflicts);
        Assert.Contains("model = \"user-choice\"", plan.UpdatedConfigToml);
    }

    public void Dispose()
    {
        if (Directory.Exists(_temporaryHome)) Directory.Delete(_temporaryHome, true);
        GC.SuppressFinalize(this);
    }
}
