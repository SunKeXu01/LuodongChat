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

    public void Dispose() { try { Directory.Delete(_root, true); } catch { } }
}
