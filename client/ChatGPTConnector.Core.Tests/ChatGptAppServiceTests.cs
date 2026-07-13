using ChatGPTConnector.Core;
using Xunit;

namespace ChatGPTConnector.Core.Tests;

public sealed class ChatGptAppServiceTests
{
    [Fact]
    public void FindsAUserInstalledChatGptExecutable()
    {
        var root = Path.Combine(Path.GetTempPath(), $"connector-app-{Guid.NewGuid():N}");
        var path = Path.Combine(root, "Programs", "ChatGPT", "ChatGPT.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, []);
        try { Assert.Equal(path, ChatGptAppService.FindExecutable(root, Path.Combine(root, "ProgramFiles"))); }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void UsesTheOfficialDownloadAddress() =>
        Assert.Equal("https://chatgpt.com/download/", ChatGptAppService.DownloadUri.ToString());
}
