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
        const string json = """{"version":"1.0","executableUrl":"https://520skx.com/client/download/LuodongChat.exe","checksumUrl":"https://520skx.com/client/download/LuodongChat.exe.sha256","installerUrl":"https://520skx.com/client/download/LuodongChat-Setup.exe","installerChecksumUrl":"https://520skx.com/client/download/LuodongChat-Setup.exe.sha256"}""";
        var update = await new ClientUpdateService(new HttpClient(new StubHandler(json))).CheckAsync("0.1.0-preview.18");
        Assert.NotNull(update);
        Assert.Equal("1.0", update.Version);
        Assert.Equal("LuodongChat-Setup.exe", update.InstallerUri!.Segments[^1]);
    }

    [Fact]
    public void PortableUpdaterWaitsForExitAndDeletesThePreviousExecutable()
    {
        var script = ClientUpdateService.BuildPortableInstallScript(@"C:\Apps\LuodongChat\data\updates\new.exe", @"C:\Apps\LuodongChat\LuodongChat.exe", 4321);
        Assert.Contains("tasklist /FI \"PID eq 4321\"", script);
        Assert.Contains("move /y \"%target%\" \"%backup%\"", script);
        Assert.Contains("del /f /q \"%backup%\"", script);
        Assert.Contains("start \"\" \"%target%\"", script);
    }

    [Fact]
    public void InstalledUpdaterRunsTheInstallerInTheExistingDirectory()
    {
        var script = ClientUpdateService.BuildInstallerScript(@"C:\Apps\LuodongChat\data\updates\setup.exe", @"C:\Apps\LuodongChat\", 4321);
        Assert.Contains("tasklist /FI \"PID eq 4321\"", script);
        Assert.Contains("/S", script);
        Assert.Contains("/D=%root%", script);
        Assert.Contains("%root%\\LuodongChat.exe", script);
    }

    private sealed class StubHandler(string json) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") });
    }
}
