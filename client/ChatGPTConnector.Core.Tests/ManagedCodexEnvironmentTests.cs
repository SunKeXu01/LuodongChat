using ChatGPTConnector.Core;
using Xunit;

namespace ChatGPTConnector.Core.Tests;

public sealed class ManagedCodexEnvironmentTests
{
    [Fact]
    public void CreatesAndRemovesAConfigurationLinkWithoutDeletingItsTarget()
    {
        var root = Path.Combine(Path.GetTempPath(), $"connector-link-{Guid.NewGuid():N}");
        var target = Path.Combine(root, "managed");
        var link = Path.Combine(root, ".codex");
        Directory.CreateDirectory(target);
        File.WriteAllText(Path.Combine(target, "config.toml"), "model = \"gpt-5.6-sol\"");
        try
        {
            ManagedCodexEnvironment.CreateManagedLink(link, target);
            Assert.True(ManagedCodexEnvironment.IsLinkTo(link, target));
            Assert.True(File.Exists(Path.Combine(link, "config.toml")));
            Directory.Delete(link);
            Assert.True(File.Exists(Path.Combine(target, "config.toml")));
        }
        finally { Directory.Delete(root, true); }
    }
}
