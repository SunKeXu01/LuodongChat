using ChatGPTConnector.Core;
using Xunit;

namespace ChatGPTConnector.Core.Tests;

public sealed class McpIntegrationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "luodong-mcp-tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public void ConfigurationRoundTripsWithoutLosingTransportSettings()
    {
        var path = Path.Combine(_root, "mcp.json");
        var store = new McpConfigurationStore(path);
        var expected = new McpConfiguration([
            new("files", "本地文件", McpTransportKind.Stdio, Command: "npx", Arguments: ["-y", "server"], Environment: new Dictionary<string, string> { ["ROOT"] = "D:\\Project" }),
            new("remote", "远程服务", McpTransportKind.Http, Url: "https://example.com/mcp", Headers: new Dictionary<string, string> { ["Authorization"] = "Bearer test" }),
        ]);

        store.Save(expected);
        var actual = store.Load();

        Assert.Equal(2, actual.Servers.Count);
        Assert.Equal("npx", actual.Servers[0].Command);
        Assert.Equal("https://example.com/mcp", actual.Servers[1].Url);
        Assert.Equal("Bearer test", actual.Servers[1].Headers!["Authorization"]);
    }

    [Fact]
    public void ModelToolNamesAreSafeBoundedAndCollisionFree()
    {
        var first = McpClientManager.CreateUniqueModelName("My Server!", "Read / file with a very very very very very very very long name", []);
        var second = McpClientManager.CreateUniqueModelName("My Server!", "Read / file with a very very very very very very very long name", [first]);

        Assert.Matches("^[a-z0-9_-]+$", first);
        Assert.True(first.Length <= 64);
        Assert.NotEqual(first, second);
        Assert.True(second.Length <= 64);
    }

    [Fact]
    public void RuntimeModeDefaultsToAutoAndRoundTrips()
    {
        var store = new McpRuntimeSettingsStore(Path.Combine(_root, "runtime.json"));
        Assert.Equal(McpToolMode.Auto, store.Load().ToolMode);
        store.Save(new(McpToolMode.Off));
        Assert.Equal(McpToolMode.Off, store.Load().ToolMode);
    }

    [Theory]
    [InlineData("get_weather", "Get public weather", McpToolRisk.PublicRead)]
    [InlineData("read_profile", "Read private account profile", McpToolRisk.SensitiveRead)]
    [InlineData("update_file", "Modify a document", McpToolRisk.Write)]
    [InlineData("send_email", "Send an email", McpToolRisk.ExternalAction)]
    [InlineData("delete_database", "Delete data", McpToolRisk.Dangerous)]
    public void RiskClassifierKeepsMutatingAndSensitiveToolsBehindApproval(string name, string description, McpToolRisk expected)
    {
        using var schema = System.Text.Json.JsonDocument.Parse("{\"type\":\"object\"}");
        var tool = new McpToolDescriptor("mcp__test__tool", "test", "测试", name, description, schema.RootElement.Clone());
        Assert.Equal(expected, McpToolRiskClassifier.Classify(tool));
    }

    [Fact]
    public void DiscoveryCatalogKeepsOfficialAndThirdPartySourcesClearlySeparated()
    {
        Assert.Contains(McpDiscoveryCatalog.Sources, source => source.Name == "MCP Registry" && source.Kind.Contains("官方"));
        Assert.Contains(McpDiscoveryCatalog.Sources, source => source.Name == "MCPMarket" && source.Kind.Contains("第三方"));
        Assert.Contains(McpDiscoveryCatalog.Sources, source => source.Name == "MCP Server Hub" && source.Url == "https://mcpserverhub.com/servers");
    }

    [Fact]
    public void ConversationProposalParsesHttpAndJsonConfigurationsOnlyWithExplicitIntent()
    {
        Assert.True(McpConfigurationProposalParser.TryParse(
            "帮我添加这个 MCP：https://mcp.example.com/service", out var http));
        Assert.Equal(McpTransportKind.Http, http!.Transport);
        Assert.Equal("https://mcp.example.com/service", http.Url);

        Assert.True(McpConfigurationProposalParser.TryParse(
            "添加 MCP：{\"mcpServers\":{\"memory\":{\"command\":\"node\",\"args\":[\"server.js\"]}}}", out var stdio));
        Assert.Equal("memory", stdio!.Name);
        Assert.Equal("node", stdio.Command);
        Assert.False(McpConfigurationProposalParser.TryParse("参考链接 https://mcp.example.com", out _));
    }

    public void Dispose() { try { Directory.Delete(_root, true); } catch { } }
}
