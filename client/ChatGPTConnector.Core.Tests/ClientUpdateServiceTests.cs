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
        const string json = """{"version":"0.1.0-preview.4","executableUrl":"https://luodongchat-app.oss-cn-beijing.aliyuncs.com/latest/LuodongChat.exe","checksumUrl":"https://luodongchat-app.oss-cn-beijing.aliyuncs.com/latest/LuodongChat.exe.sha256"}""";
        var update = await new ClientUpdateService(new HttpClient(new StubHandler(json))).CheckAsync("0.1.0-preview.3");
        Assert.NotNull(update);
        Assert.Equal("0.1.0-preview.4", update.Version);
    }

    [Fact]
    public async Task IgnoresTheCurrentRelease()
    {
        const string json = """{"version":"0.1.0-preview.2","executableUrl":"https://luodongchat-app.oss-cn-beijing.aliyuncs.com/latest/LuodongChat.exe","checksumUrl":"https://luodongchat-app.oss-cn-beijing.aliyuncs.com/latest/LuodongChat.exe.sha256"}""";
        Assert.Null(await new ClientUpdateService(new HttpClient(new StubHandler(json))).CheckAsync("0.1.0-preview.2"));
    }

    [Fact]
    public async Task StableV1IsNewerThanPreviewRelease()
    {
        const string json = """{"version":"1.0","executableUrl":"https://luodongchat-app.oss-cn-beijing.aliyuncs.com/latest/LuodongChat.exe","checksumUrl":"https://luodongchat-app.oss-cn-beijing.aliyuncs.com/latest/LuodongChat.exe.sha256","installerUrl":"https://luodongchat-app.oss-cn-beijing.aliyuncs.com/latest/LuodongChat-Setup.exe","installerChecksumUrl":"https://luodongchat-app.oss-cn-beijing.aliyuncs.com/latest/LuodongChat-Setup.exe.sha256"}""";
        var update = await new ClientUpdateService(new HttpClient(new StubHandler(json))).CheckAsync("0.1.0-preview.18");
        Assert.NotNull(update);
        Assert.Equal("1.0", update.Version);
        Assert.Equal("LuodongChat-Setup.exe", update.InstallerUri!.Segments[^1]);
    }

    [Fact]
    public void PortableUpdaterUsesUnicodeSafePowerShellAndDeletesThePreviousExecutable()
    {
        var script = ClientUpdateService.BuildPortableInstallScript(@"D:\中文 目录\泺栋\data\updates\new.exe", @"D:\中文 目录\泺栋\LuodongChat.exe", 4321);
        Assert.Contains("Wait-Process -Id 4321", script);
        Assert.Contains(@"D:\中文 目录\泺栋\data\updates\new.exe", script);
        Assert.Contains("Move-Item -LiteralPath $target -Destination $backup", script);
        Assert.Contains("Remove-Item -LiteralPath $backup", script);
        Assert.Contains("Start-Process -FilePath $target -WorkingDirectory $root", script);
        Assert.Contains("for ($attempt = 0; $attempt -lt 20; $attempt++)", script);
    }

    [Fact]
    public void InstalledUpdaterRunsTheInstallerInTheExistingDirectory()
    {
        var script = ClientUpdateService.BuildInstallerScript(@"D:\中文 目录\泺栋\data\updates\setup.exe", @"D:\中文 目录\泺栋\", 4321);
        Assert.Contains("Wait-Process -Id 4321", script);
        Assert.Contains(@"D:\中文 目录\泺栋\data\updates\setup.exe", script);
        Assert.Contains("& $installer /S \"/D=$root\"", script);
        Assert.Contains("$target = Join-Path $root 'LuodongChat.exe'", script);
        Assert.Contains("Start-Process -FilePath $target -WorkingDirectory $root", script);
        Assert.Contains("for ($attempt = 0; $attempt -lt 20; $attempt++)", script);
    }

    private sealed class StubHandler(string json) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") });
    }
}
