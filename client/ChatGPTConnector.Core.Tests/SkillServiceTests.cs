using System.Text.Json;
using ChatGPTConnector.Core;
using Xunit;

namespace ChatGPTConnector.Core.Tests;

public sealed class SkillServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "luodong-skill-tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public void DiscoversEnablesAndLoadsCodexCompatibleSkill()
    {
        var source = Path.Combine(_root, "source", "review-code");
        Directory.CreateDirectory(Path.Combine(source, "references"));
        File.WriteAllText(Path.Combine(source, "SKILL.md"), """
            ---
            name: review-code
            description: Review source code when the user asks for a code review.
            ---
            # Review workflow
            Read the relevant files and report actionable findings.
            """);
        File.WriteAllText(Path.Combine(source, "references", "rules.md"), "Prefer precise line references.");
        var service = CreateService();

        var installed = service.InstallFromDirectory(source);
        service.SetEnabled(installed, true);
        var result = service.Discover();

        var skill = Assert.Single(result.Skills);
        Assert.Equal("review-code", skill.Name);
        Assert.True(skill.Enabled);
        Assert.Contains("references/rules.md", skill.LinkedFiles);
        var prompt = service.BuildPrompt("Please help me");
        Assert.Contains("Review workflow", prompt);
    }

    [Fact]
    public async Task ExplicitMentionLoadsSkillAndReferenceToolCannotEscapeRoot()
    {
        var skills = Path.Combine(_root, "skills", "write-docs");
        Directory.CreateDirectory(skills);
        File.WriteAllText(Path.Combine(skills, "SKILL.md"), """
            ---
            name: write-docs
            description: Write product documentation.
            ---
            Follow the documentation workflow.
            """);
        var service = CreateService();

        Assert.Contains("Follow the documentation workflow", service.BuildPrompt("$write-docs create a guide"));
        var output = await service.ExecuteToolAsync(new LocalToolCall("1", "read_skill_file", "{\"name\":\"write-docs\",\"path\":\"../secret.txt\"}"));

        using var json = JsonDocument.Parse(output);
        Assert.False(json.RootElement.GetProperty("ok").GetBoolean());
        Assert.Contains("超出技能目录", json.RootElement.GetProperty("error").GetString());
    }

    [Fact]
    public void IgnoresSymlinkedSkillDirectories()
    {
        if (OperatingSystem.IsWindows()) return;
        var external = Path.Combine(_root, "external", "hidden");
        Directory.CreateDirectory(external);
        File.WriteAllText(Path.Combine(external, "SKILL.md"), "---\nname: hidden\ndescription: hidden\n---\nDo hidden things.");
        var skillsRoot = Path.Combine(_root, "skills");
        Directory.CreateDirectory(skillsRoot);
        Directory.CreateSymbolicLink(Path.Combine(skillsRoot, "linked"), external);

        Assert.Empty(CreateService().Discover().Skills);
    }

    private SkillService CreateService() => new(Path.Combine(_root, "skills"), Path.Combine(_root, "settings.json"));
    public void Dispose() { try { Directory.Delete(_root, true); } catch { } }
}
