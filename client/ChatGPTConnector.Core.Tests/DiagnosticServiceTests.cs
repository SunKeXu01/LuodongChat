using System.IO.Compression;
using ChatGPTConnector.Core;
using Xunit;

namespace ChatGPTConnector.Core.Tests;

public sealed class DiagnosticServiceTests
{
    [Fact]
    public void SanitizerRemovesCredentialsIdentityAndPaths()
    {
        var result = DiagnosticLog.Sanitize("token=usr_abcdefghijk user@example.com D:\\Users\\Alice\\secret.txt 192.168.1.5");
        Assert.DoesNotContain("usr_", result.Text);
        Assert.DoesNotContain("user@example.com", result.Text);
        Assert.DoesNotContain("Alice", result.Text);
        Assert.DoesNotContain("192.168.1.5", result.Text);
        Assert.True(result.Count >= 3);
    }

    [Fact]
    public void PackageContainsOnlyDocumentedDiagnosticFiles()
    {
        var package = DiagnosticPackageBuilder.Create("TEST_ERROR", DiagnosticRange.Related, "2.1.0");
        using var archive = new ZipArchive(new MemoryStream(package.Data), ZipArchiveMode.Read);
        Assert.Equal(["app.log", "environment.json", "error.json", "manifest.json", "requests.json", "tools.json"],
            archive.Entries.Select(item => item.FullName).Order().ToArray());
        using var reader = new StreamReader(archive.GetEntry("manifest.json")!.Open());
        var manifest = reader.ReadToEnd();
        Assert.Contains("conversationContent", manifest);
        Assert.Contains("false", manifest);
    }
}
