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
        const string json = """{"version":"0.1.0-preview.4","executableUrl":"https://520skx.com/client/download/LuodongChat.exe","checksumUrl":"https://520skx.com/client/download/LuodongChat.exe.sha256"}""";
        var update = await new ClientUpdateService(new HttpClient(new StubHandler(json))).CheckAsync("0.1.0-preview.3");
        Assert.NotNull(update);
        Assert.Equal("0.1.0-preview.4", update.Version);
    }

    [Fact]
    public async Task IgnoresTheCurrentRelease()
    {
        const string json = """{"version":"0.1.0-preview.2","executableUrl":"https://520skx.com/client/download/LuodongChat.exe","checksumUrl":"https://520skx.com/client/download/LuodongChat.exe.sha256"}""";
        Assert.Null(await new ClientUpdateService(new HttpClient(new StubHandler(json))).CheckAsync("0.1.0-preview.2"));
    }

    [Fact]
    public async Task StableV1IsNewerThanPreviewRelease()
    {
        const string json = """{"version":"1.0","executableUrl":"https://520skx.com/client/download/LuodongChat.exe","checksumUrl":"https://520skx.com/client/download/LuodongChat.exe.sha256"}""";
        var update = await new ClientUpdateService(new HttpClient(new StubHandler(json))).CheckAsync("0.1.0-preview.18");
        Assert.NotNull(update);
        Assert.Equal("1.0", update.Version);
    }

    [Fact]
    public void InstallerWaitsForExitAndDeletesThePreviousExecutable()
    {
        var script = ClientUpdateService.BuildInstallScript(@"C:\Temp\new.exe", @"C:\Apps\LuodongChat.exe", 4321);
        Assert.Contains("tasklist /FI \"PID eq 4321\"", script);
        Assert.Contains("move /y \"%target%\" \"%backup%\"", script);
        Assert.Contains("del /f /q \"%backup%\"", script);
        Assert.Contains("start \"\" \"%target%\"", script);
    }

    private sealed class StubHandler(string json) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") });
    }
}
