using ChatGPTConnector.Core;
using Xunit;

namespace ChatGPTConnector.Core.Tests;

public sealed class ProjectContextBuilderTests
{
    [Fact]
    public async Task InspectAsync_ReportsReadableFilesAndIgnoresSecrets()
    {
        var root = Path.Combine(Path.GetTempPath(), $"luodong-project-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            await File.WriteAllTextAsync(Path.Combine(root, "README.md"), "hello");
            await File.WriteAllTextAsync(Path.Combine(root, "main.cs"), "class App {}");
            await File.WriteAllTextAsync(Path.Combine(root, ".env"), "SECRET=value");
            await File.WriteAllBytesAsync(Path.Combine(root, "image.png"), [1, 2, 3]);

            var result = await new ProjectContextBuilder().InspectAsync(root);

            Assert.NotNull(result);
            Assert.Equal(4, result.ScannedFileCount);
            Assert.Equal(2, result.IgnoredFileCount);
            Assert.Equal(["main.cs", "README.md"], result.ReadableFiles);
            Assert.False(result.Truncated);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }
}
