using System.Net;
using System.Text;
using ChatGPTConnector.Core;
using Xunit;

namespace ChatGPTConnector.Core.Tests;

public sealed class ClientUpdateServiceTests
{
    [Fact]
    public async Task SelectsANewerReleaseWithVerifiedAssets()
    {
        const string json = """{"version":"0.1.0-preview.3","executableUrl":"https://520skx.com/client/download/ChatGPTConnector.exe","checksumUrl":"https://520skx.com/client/download/ChatGPTConnector.exe.sha256"}""";
        var update = await new ClientUpdateService(new HttpClient(new StubHandler(json))).CheckAsync("0.1.0-preview.2");
        Assert.NotNull(update);
        Assert.Equal("0.1.0-preview.3", update.Version);
    }

    [Fact]
    public async Task IgnoresTheCurrentRelease()
    {
        const string json = """{"version":"0.1.0-preview.2","executableUrl":"https://520skx.com/client/download/ChatGPTConnector.exe","checksumUrl":"https://520skx.com/client/download/ChatGPTConnector.exe.sha256"}""";
        Assert.Null(await new ClientUpdateService(new HttpClient(new StubHandler(json))).CheckAsync("0.1.0-preview.2"));
    }

    private sealed class StubHandler(string json) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") });
    }
}
