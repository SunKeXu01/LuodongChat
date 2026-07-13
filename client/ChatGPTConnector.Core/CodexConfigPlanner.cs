using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using Tomlyn;
using Tomlyn.Model;

namespace ChatGPTConnector.Core;

public sealed class CodexConfigPlanner
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        WriteIndented = true,
    };

    public ConfigurationPlan CreatePlan(
        string existingConfigToml,
        string existingAuthJson,
        ConnectorSettings settings)
    {
        settings.Validate();
        TomlTable config;
        try
        {
            config = TomlSerializer.Deserialize<TomlTable>(existingConfigToml) ?? new TomlTable();
        }
        catch (TomlException error)
        {
            throw new InvalidDataException("Codex config.toml is invalid.", error);
        }
        var providerPath = $"model_providers.{settings.ProviderId}";
        config["model_provider"] = settings.ProviderId;
        config["model"] = settings.Model;
        config["review_model"] = settings.Model;
        config["model_reasoning_effort"] = settings.ReasoningEffort;
        config["disable_response_storage"] = true;

        var providers = GetOrCreateTable(config, "model_providers");
        var provider = GetOrCreateTable(providers, settings.ProviderId);
        provider["name"] = "ChatGPT Connector";
        provider["base_url"] = settings.GatewayBaseUri.ToString().TrimEnd('/');
        provider["wire_api"] = "responses";
        provider["requires_openai_auth"] = true;

        var auth = ParseAuth(existingAuthJson);
        auth["OPENAI_API_KEY"] = settings.GatewayKey;

        return new ConfigurationPlan(
            TomlSerializer.Serialize(config),
            auth.ToJsonString(JsonOptions) + Environment.NewLine,
            [
                "model_provider",
                "model",
                "review_model",
                "model_reasoning_effort",
                "disable_response_storage",
                providerPath,
                "auth.OPENAI_API_KEY",
            ],
            [
                $"AI 模型：{ConnectorSettings.DisplayModel}",
                $"网关地址：{settings.GatewayBaseUri.ToString().TrimEnd('/')}",
                "凭证：将写入用户网关密钥（界面不显示完整值）",
            ]);
    }

    private static TomlTable GetOrCreateTable(TomlTable parent, string key)
    {
        if (parent.TryGetValue(key, out var value))
        {
            if (value is TomlTable table) return table;
            throw new InvalidDataException($"Codex config field '{key}' must be a table.");
        }
        var created = new TomlTable();
        parent[key] = created;
        return created;
    }

    private static JsonObject ParseAuth(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new JsonObject();
        try
        {
            return JsonNode.Parse(json) as JsonObject
                ?? throw new InvalidDataException("Codex auth.json must contain a JSON object.");
        }
        catch (JsonException error)
        {
            throw new InvalidDataException("Codex auth.json is invalid JSON.", error);
        }
    }
}
