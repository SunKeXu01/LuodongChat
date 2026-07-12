namespace ChatGPTConnector.Core;

public sealed record ConfigurationPlan(
    string UpdatedConfigToml,
    string UpdatedAuthJson,
    IReadOnlyList<string> ManagedPaths,
    IReadOnlyList<string> ChangeSummary);
