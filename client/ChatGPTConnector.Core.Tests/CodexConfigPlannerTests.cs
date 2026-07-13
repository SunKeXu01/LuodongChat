using System.Text.Json.Nodes;
using ChatGPTConnector.Core;
using Tomlyn;
using Xunit;

namespace ChatGPTConnector.Core.Tests;

public sealed class CodexConfigPlannerTests
{
    [Fact]
    public void PreservesUnrelatedConfigAndAuthFields()
    {
        const string config = """
            model = "existing-model"
            approval_policy = "on-request"

            [features]
            goals = true
            """;
        const string auth = """{"OTHER_TOKEN":"keep-me"}""";
        var settings = new ConnectorSettings(
            new Uri("https://520skx.com"),
            "gw_test_secret");

        var plan = new CodexConfigPlanner().CreatePlan(config, auth, settings);
        var model = TomlSerializer.Deserialize<Tomlyn.Model.TomlTable>(plan.UpdatedConfigToml)!;
        var authModel = JsonNode.Parse(plan.UpdatedAuthJson)!.AsObject();

        Assert.Equal("on-request", model["approval_policy"]);
        Assert.Equal("gpt-5.6-sol", model["model"]);
        Assert.Equal("ChatGPTConnector", model["model_provider"]);
        Assert.Equal("keep-me", authModel["OTHER_TOKEN"]!.GetValue<string>());
        Assert.Equal("gw_test_secret", authModel["OPENAI_API_KEY"]!.GetValue<string>());
    }

    [Fact]
    public void ProducesAResponsesProviderWithoutLeakingKeyInSummary()
    {
        var plan = new CodexConfigPlanner().CreatePlan(
            string.Empty,
            "{}",
            new ConnectorSettings(new Uri("https://520skx.com/"), "gw_sensitive"));
        var model = TomlSerializer.Deserialize<Tomlyn.Model.TomlTable>(plan.UpdatedConfigToml)!;
        var providers = Assert.IsType<Tomlyn.Model.TomlTable>(model["model_providers"]);
        var provider = Assert.IsType<Tomlyn.Model.TomlTable>(providers["ChatGPTConnector"]);

        Assert.Equal("https://520skx.com", provider["base_url"]);
        Assert.Equal("responses", provider["wire_api"]);
        Assert.True((bool)provider["requires_openai_auth"]!);
        Assert.DoesNotContain("gw_sensitive", string.Join("\n", plan.ChangeSummary));
        Assert.Contains("AI 模型：GPT-5.6", plan.ChangeSummary);
        Assert.DoesNotContain("gpt-5.6-sol", string.Join("\n", plan.ChangeSummary));
        Assert.DoesNotContain("ChatGPTConnector", string.Join("\n", plan.ChangeSummary));
    }

    [Fact]
    public void RejectsInvalidTomlBeforePlanningChanges()
    {
        Assert.Throws<InvalidDataException>(() => new CodexConfigPlanner().CreatePlan(
            "model = [",
            "{}",
            new ConnectorSettings(new Uri("https://520skx.com"), "gw_test")));
    }
}
