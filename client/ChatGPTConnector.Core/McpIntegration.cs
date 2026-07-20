using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace ChatGPTConnector.Core;

public enum McpTransportKind { Stdio, Http }
public enum McpToolMode { Off, Smart, Specified }
public enum McpToolRisk { PublicRead, SensitiveRead, Write, ExternalAction, Dangerous }

public sealed record McpRuntimeSettings(
    McpToolMode ToolMode = McpToolMode.Smart,
    IReadOnlyList<string>? SelectedToolNames = null);

public sealed class McpRuntimeSettingsStore(string path)
{
    public static McpRuntimeSettingsStore ForApplicationDirectory() =>
        new(Path.Combine(ApplicationDirectories.Data, "mcp-runtime.json"));

    public McpRuntimeSettings Load()
    {
        try
        {
            return File.Exists(path)
                ? JsonSerializer.Deserialize<McpRuntimeSettings>(File.ReadAllBytes(path), JsonOptions) ?? new()
                : new();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        {
            return new();
        }
    }

    public void Save(McpRuntimeSettings settings)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + ".tmp";
        File.WriteAllBytes(temp, JsonSerializer.SerializeToUtf8Bytes(settings, JsonOptions));
        File.Move(temp, path, true);
    }

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };
}

public static class McpToolRiskClassifier
{
    private static readonly string[] DangerousWords = ["delete", "remove", "drop", "destroy", "erase", "format", "payment", "purchase", "transfer", "删除", "销毁", "付款", "转账"];
    private static readonly string[] ExternalActionWords = ["send", "publish", "post", "submit", "create_issue", "merge", "deploy", "message", "email", "发送", "发布", "提交", "部署"];
    private static readonly string[] WriteWords = ["write", "update", "edit", "modify", "create", "insert", "upload", "save", "set_", "写入", "修改", "创建", "上传", "保存"];
    private static readonly string[] SensitiveWords = ["private", "secret", "credential", "token", "account", "profile", "contact", "mail", "个人", "私有", "凭据", "密钥", "账户", "联系人"];
    private static readonly string[] PublicReadWords = ["get", "list", "search", "fetch", "query", "lookup", "read", "weather", "time", "status", "搜索", "查询", "获取", "天气", "时间", "状态"];

    public static McpToolRisk Classify(McpToolDescriptor descriptor)
    {
        var text = $"{descriptor.OriginalName} {descriptor.Description}".ToLowerInvariant();
        if (ContainsAny(text, DangerousWords)) return McpToolRisk.Dangerous;
        if (ContainsAny(text, ExternalActionWords)) return McpToolRisk.ExternalAction;
        if (ContainsAny(text, WriteWords)) return McpToolRisk.Write;
        if (ContainsAny(text, SensitiveWords)) return McpToolRisk.SensitiveRead;
        return ContainsAny(text, PublicReadWords) ? McpToolRisk.PublicRead : McpToolRisk.SensitiveRead;
    }

    public static string Label(McpToolRisk risk) => risk switch
    {
        McpToolRisk.PublicRead => "公开只读查询",
        McpToolRisk.SensitiveRead => "可能读取敏感数据",
        McpToolRisk.Write => "可能修改数据",
        McpToolRisk.ExternalAction => "可能执行外部操作",
        _ => "高风险操作",
    };

    private static bool ContainsAny(string text, IEnumerable<string> values) =>
        values.Any(value => text.Contains(value, StringComparison.OrdinalIgnoreCase));
}

public sealed record McpDiscoverySource(string Name, string Url, string Kind, string Description);

public static class McpDiscoveryCatalog
{
    public static IReadOnlyList<McpDiscoverySource> Sources { get; } =
    [
        new("MCP Registry", "https://registry.modelcontextprotocol.io/", "官方 Registry", "优先查找已发布的 MCP 服务器与版本元数据。"),
        new("MCP Reference Servers", "https://github.com/modelcontextprotocol/servers", "官方参考实现", "用于学习协议和常见能力，不等同于生产级安全审计。"),
        new("MCPMarket", "https://mcpmarket.com/zh/search", "第三方目录", "按分类和关键词发现社区 MCP。安装前需要核对发布者、源码和权限。"),
        new("MCP Server Hub", "https://mcpserverhub.com/servers", "第三方目录", "发现社区 MCP Servers 与工具。收录不代表泺栋 Chat 或 MCP 官方审核。"),
    ];
}

public static class McpConfigurationProposalParser
{
    private static readonly Regex AddIntent = new(@"(?:添加|安装|连接|配置)\s*(?:这个|以下)?\s*MCP", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex HttpUrl = new("https?://[^\\s\\\"'<>]+", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static bool TryParse(string text, out McpServerConfiguration? configuration)
    {
        configuration = null;
        if (string.IsNullOrWhiteSpace(text) || !AddIntent.IsMatch(text)) return false;
        var jsonStart = text.IndexOf('{');
        var jsonEnd = text.LastIndexOf('}');
        if (jsonStart >= 0 && jsonEnd > jsonStart)
        {
            try
            {
                using var document = JsonDocument.Parse(text[jsonStart..(jsonEnd + 1)]);
                var root = document.RootElement;
                var name = "MCP 服务";
                if (root.TryGetProperty("mcpServers", out var servers) && servers.ValueKind == JsonValueKind.Object)
                {
                    var first = servers.EnumerateObject().FirstOrDefault();
                    if (first.Value.ValueKind == JsonValueKind.Undefined) return false;
                    name = first.Name;
                    root = first.Value;
                }
                configuration = ParseObject(root, name);
                if (configuration is not null) return true;
            }
            catch (JsonException) { }
        }
        var match = HttpUrl.Match(text);
        if (!match.Success || !Uri.TryCreate(match.Value.TrimEnd('.', '。', ',', '，'), UriKind.Absolute, out var uri)) return false;
        configuration = new(Guid.NewGuid().ToString("N")[..12], uri.Host, McpTransportKind.Http, Url: uri.AbsoluteUri);
        return true;
    }

    private static McpServerConfiguration? ParseObject(JsonElement root, string name)
    {
        if (root.ValueKind != JsonValueKind.Object) return null;
        if (root.TryGetProperty("url", out var url) && url.GetString() is { Length: > 0 } address
            && Uri.TryCreate(address, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https")
            return new(Guid.NewGuid().ToString("N")[..12], name, McpTransportKind.Http, Url: uri.AbsoluteUri,
                Headers: ReadStringMap(root, "headers"));
        if (!root.TryGetProperty("command", out var commandElement) || commandElement.GetString() is not { Length: > 0 } command) return null;
        var arguments = root.TryGetProperty("args", out var args) && args.ValueKind == JsonValueKind.Array
            ? args.EnumerateArray().Select(item => item.GetString()).Where(item => item is not null).Cast<string>().ToArray() : [];
        if (OperatingSystem.IsWindows() && command.Equals("npx", StringComparison.OrdinalIgnoreCase))
        {
            arguments = ["/c", "npx", .. arguments];
            command = "cmd.exe";
        }
        return new(Guid.NewGuid().ToString("N")[..12], name, McpTransportKind.Stdio, Command: command,
            Arguments: arguments, Environment: ReadStringMap(root, "env"));
    }

    private static IReadOnlyDictionary<string, string>? ReadStringMap(JsonElement root, string property)
    {
        if (!root.TryGetProperty(property, out var map) || map.ValueKind != JsonValueKind.Object) return null;
        return map.EnumerateObject().Where(item => item.Value.ValueKind == JsonValueKind.String)
            .ToDictionary(item => item.Name, item => item.Value.GetString() ?? "", StringComparer.OrdinalIgnoreCase);
    }
}

public sealed record McpServerConfiguration(
    string Id,
    string Name,
    McpTransportKind Transport,
    bool Enabled = true,
    string? Command = null,
    IReadOnlyList<string>? Arguments = null,
    string? Url = null,
    IReadOnlyDictionary<string, string>? Environment = null,
    IReadOnlyDictionary<string, string>? Headers = null);

public sealed record McpConfiguration(IReadOnlyList<McpServerConfiguration> Servers)
{
    public static McpConfiguration Empty { get; } = new([]);
}

public sealed class McpConfigurationStore(string path)
{
    private static readonly byte[] EncryptedHeader = "LDMCP1\n"u8.ToArray();
    public static McpConfigurationStore ForApplicationDirectory() =>
        new(Path.Combine(ApplicationDirectories.Data, "mcp-servers.dat"));

    public McpConfiguration Load()
    {
        try
        {
            if (!File.Exists(path)) return McpConfiguration.Empty;
            var bytes = File.ReadAllBytes(path);
            if (bytes.AsSpan().StartsWith(EncryptedHeader))
                bytes = SecureSessionStore.UnprotectForCurrentUser(bytes[EncryptedHeader.Length..]);
            return JsonSerializer.Deserialize<McpConfiguration>(bytes, JsonOptions)
                ?? McpConfiguration.Empty;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException)
        {
            return McpConfiguration.Empty;
        }
    }

    public void Save(McpConfiguration configuration)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + ".tmp";
        var plaintext = JsonSerializer.SerializeToUtf8Bytes(configuration, JsonOptions);
        byte[] output;
        if (OperatingSystem.IsWindows())
        {
            var encrypted = SecureSessionStore.ProtectForCurrentUser(plaintext);
            output = [.. EncryptedHeader, .. encrypted];
        }
        else output = plaintext;
        File.WriteAllBytes(temp, output);
        Array.Clear(plaintext);
        File.Move(temp, path, true);
    }

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };
}

public sealed record McpToolDescriptor(
    string ModelName,
    string ServerId,
    string ServerName,
    string OriginalName,
    string Description,
    JsonElement InputSchema);

public sealed record McpServerStatus(
    string Id,
    string Name,
    bool Enabled,
    bool Connected,
    int ToolCount,
    string? Error = null);

/// <summary>
/// Owns MCP sessions for the desktop client. Tool names are namespaced before they
/// are exposed to the model, preventing one server from shadowing local tools or
/// tools from another server.
/// </summary>
public sealed class McpClientManager : IAsyncDisposable
{
    private static readonly string[] ProtocolToolNames = ["mcp_list_resources", "mcp_read_resource", "mcp_list_prompts", "mcp_get_prompt"];
    private sealed record ConnectedServer(McpServerConfiguration Configuration, McpClient Client);

    private readonly McpConfigurationStore _store;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<string, ConnectedServer> _clients = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, McpToolDescriptor> _tools = new(StringComparer.Ordinal);
    private IReadOnlyList<McpServerStatus> _statuses = [];

    public McpClientManager(McpConfigurationStore store) => _store = store;

    public IReadOnlyList<McpServerStatus> Statuses => _statuses;

    public static IReadOnlyList<object> ProtocolToolDefinitions { get; } =
    [
        Function("mcp_list_resources", "列出已连接 MCP 服务器提供的 Resources 和资源模板。", new
        {
            type = "object", properties = new { server = new { type = "string", description = "可选的 MCP 服务器 ID；留空列出全部服务器。" } }, additionalProperties = false,
        }),
        Function("mcp_read_resource", "读取 MCP Resource。只能读取 mcp_list_resources 返回的 URI。", new
        {
            type = "object", properties = new { server = new { type = "string" }, uri = new { type = "string" } },
            required = new[] { "server", "uri" }, additionalProperties = false,
        }),
        Function("mcp_list_prompts", "列出已连接 MCP 服务器提供的 Prompts 及其参数。", new
        {
            type = "object", properties = new { server = new { type = "string", description = "可选的 MCP 服务器 ID。" } }, additionalProperties = false,
        }),
        Function("mcp_get_prompt", "获取指定 MCP Prompt 生成的消息。", new
        {
            type = "object", properties = new
            {
                server = new { type = "string" }, name = new { type = "string" },
                arguments = new { type = "object", additionalProperties = true },
            }, required = new[] { "server", "name" }, additionalProperties = false,
        }),
    ];

    public async Task<IReadOnlyList<object>> GetToolDefinitionsAsync(CancellationToken cancellationToken = default)
        => await GetToolDefinitionsAsync(null, cancellationToken).ConfigureAwait(false);

    public async Task<IReadOnlyList<object>> GetToolDefinitionsAsync(
        IReadOnlySet<string>? allowedToolNames,
        CancellationToken cancellationToken = default)
    {
        await EnsureConnectedAsync(cancellationToken).ConfigureAwait(false);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            lock (_tools) return _tools.Values
                .Where(tool => allowedToolNames is null || allowedToolNames.Contains(tool.ModelName))
                .Select(tool => (object)new
            {
                type = "function",
                name = tool.ModelName,
                description = $"[MCP：{tool.ServerName}] {tool.Description}",
                parameters = tool.InputSchema,
                strict = false,
            }).ToArray();
        }
        finally { _gate.Release(); }
    }

    public async Task<IReadOnlyList<McpToolDescriptor>> GetToolDescriptorsAsync(
        CancellationToken cancellationToken = default)
    {
        await EnsureConnectedAsync(cancellationToken).ConfigureAwait(false);
        lock (_tools) return _tools.Values
            .OrderBy(tool => tool.ServerName, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(tool => tool.OriginalName, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();
    }

    public bool IsMcpTool(string modelName)
    {
        lock (_tools) return _tools.ContainsKey(modelName);
    }

    public bool IsProtocolTool(string name) => ProtocolToolNames.Contains(name, StringComparer.Ordinal);

    public async Task<string> ExecuteProtocolToolAsync(LocalToolCall call, CancellationToken cancellationToken = default)
    {
        await EnsureConnectedAsync(cancellationToken).ConfigureAwait(false);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var document = JsonDocument.Parse(string.IsNullOrWhiteSpace(call.Arguments) ? "{}" : call.Arguments);
            var root = document.RootElement;
            var serverId = OptionalString(root, "server");
            return call.Name switch
            {
                "mcp_list_resources" => await ListResourcesAsync(serverId, cancellationToken).ConfigureAwait(false),
                "mcp_read_resource" => await ReadResourceAsync(RequiredString(root, "server"), RequiredString(root, "uri"), cancellationToken).ConfigureAwait(false),
                "mcp_list_prompts" => await ListPromptsAsync(serverId, cancellationToken).ConfigureAwait(false),
                "mcp_get_prompt" => await GetPromptAsync(RequiredString(root, "server"), RequiredString(root, "name"),
                    root.TryGetProperty("arguments", out var arguments) ? arguments : default, cancellationToken).ConfigureAwait(false),
                _ => JsonSerializer.Serialize(new { ok = false, error = "未知 MCP 协议工具。" }),
            };
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception error)
        {
            return JsonSerializer.Serialize(new { ok = false, error = $"MCP 请求失败：{error.Message}" });
        }
        finally { _gate.Release(); }
    }

    public McpToolDescriptor? DescribeTool(string modelName)
    {
        lock (_tools) return _tools.GetValueOrDefault(modelName);
    }

    public async Task<string> CallToolAsync(LocalToolCall call, CancellationToken cancellationToken = default)
    {
        await EnsureConnectedAsync(cancellationToken).ConfigureAwait(false);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            McpToolDescriptor descriptor;
            ConnectedServer server;
            lock (_tools)
            {
                if (!_tools.TryGetValue(call.Name, out descriptor!))
                    return JsonSerializer.Serialize(new { ok = false, error = "MCP 工具不存在或服务器已断开。" });
            }
            if (!_clients.TryGetValue(descriptor.ServerId, out server!))
                return JsonSerializer.Serialize(new { ok = false, error = "MCP 服务器当前未连接。" });
            using var argumentsDocument = JsonDocument.Parse(string.IsNullOrWhiteSpace(call.Arguments) ? "{}" : call.Arguments);
            if (argumentsDocument.RootElement.ValueKind != JsonValueKind.Object)
                throw new JsonException("工具参数必须是 JSON 对象。");
            var arguments = argumentsDocument.RootElement.EnumerateObject()
                .ToDictionary(property => property.Name, property => ToObject(property.Value), StringComparer.Ordinal);
            var result = await server.Client.CallToolAsync(
                descriptor.OriginalName, arguments, cancellationToken: cancellationToken).ConfigureAwait(false);
            return SerializeResult(result);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception error)
        {
            return JsonSerializer.Serialize(new { ok = false, error = $"MCP 调用失败：{error.Message}" });
        }
        finally { _gate.Release(); }
    }

    public async Task ReloadAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await DisconnectAllAsync().ConfigureAwait(false);
            await ConnectConfiguredServersAsync(cancellationToken).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    private async Task EnsureConnectedAsync(CancellationToken cancellationToken)
    {
        if (_clients.Count > 0 || _store.Load().Servers.All(server => !server.Enabled)) return;
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_clients.Count == 0) await ConnectConfiguredServersAsync(cancellationToken).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    private async Task ConnectConfiguredServersAsync(CancellationToken cancellationToken)
    {
        var statuses = new List<McpServerStatus>();
        foreach (var configuration in _store.Load().Servers)
        {
            if (!configuration.Enabled)
            {
                statuses.Add(new(configuration.Id, configuration.Name, false, false, 0));
                continue;
            }
            try
            {
                var transport = CreateTransport(configuration);
                var client = await McpClient.CreateAsync(transport, cancellationToken: cancellationToken).ConfigureAwait(false);
                var tools = await client.ListToolsAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
                _clients[configuration.Id] = new(configuration, client);
                lock (_tools)
                {
                    foreach (var tool in tools)
                    {
                        var modelName = CreateUniqueModelName(configuration.Id, tool.Name, _tools.Keys);
                        _tools[modelName] = new(modelName, configuration.Id, configuration.Name, tool.Name,
                            tool.Description ?? tool.Name, tool.JsonSchema.Clone());
                    }
                }
                statuses.Add(new(configuration.Id, configuration.Name, true, true, tools.Count));
            }
            catch (Exception error)
            {
                statuses.Add(new(configuration.Id, configuration.Name, true, false, 0, error.Message));
            }
        }
        _statuses = statuses;
    }

    private static IClientTransport CreateTransport(McpServerConfiguration configuration)
    {
        if (configuration.Transport == McpTransportKind.Http)
        {
            if (!Uri.TryCreate(configuration.Url, UriKind.Absolute, out var endpoint)
                || endpoint.Scheme is not ("http" or "https"))
                throw new InvalidOperationException("MCP HTTP 地址无效。");
            return new HttpClientTransport(new HttpClientTransportOptions
            {
                Name = configuration.Name,
                Endpoint = endpoint,
                TransportMode = HttpTransportMode.AutoDetect,
                ConnectionTimeout = TimeSpan.FromSeconds(30),
                AdditionalHeaders = configuration.Headers is null
                    ? null : new Dictionary<string, string>(configuration.Headers, StringComparer.OrdinalIgnoreCase),
            });
        }

        if (string.IsNullOrWhiteSpace(configuration.Command))
            throw new InvalidOperationException("MCP stdio 启动命令不能为空。");
        var environment = StdioClientTransportOptions.GetDefaultEnvironmentVariables();
        if (configuration.Environment is not null)
            foreach (var pair in configuration.Environment) environment[pair.Key] = pair.Value;
        return new StdioClientTransport(new StdioClientTransportOptions
        {
            Name = configuration.Name,
            Command = configuration.Command,
            Arguments = configuration.Arguments?.ToArray() ?? [],
            InheritEnvironmentVariables = false,
            EnvironmentVariables = environment,
        });
    }

    internal static string CreateUniqueModelName(string serverId, string toolName, IEnumerable<string> existing)
    {
        var prefix = SanitizeName(serverId);
        var suffix = SanitizeName(toolName);
        var baseName = $"mcp__{prefix}__{suffix}";
        if (baseName.Length > 64) baseName = baseName[..64].TrimEnd('_');
        var used = existing.ToHashSet(StringComparer.Ordinal);
        if (!used.Contains(baseName)) return baseName;
        for (var index = 2; ; index++)
        {
            var number = $"_{index}";
            var candidate = baseName[..Math.Min(baseName.Length, 64 - number.Length)] + number;
            if (!used.Contains(candidate)) return candidate;
        }
    }

    private static string SanitizeName(string value)
    {
        var result = Regex.Replace(value.Trim().ToLowerInvariant(), "[^a-z0-9_-]+", "_").Trim('_');
        return string.IsNullOrWhiteSpace(result) ? "server" : result;
    }

    private static object? ToObject(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.Object => element.EnumerateObject().ToDictionary(p => p.Name, p => ToObject(p.Value)),
        JsonValueKind.Array => element.EnumerateArray().Select(ToObject).ToArray(),
        JsonValueKind.String => element.GetString(),
        JsonValueKind.Number when element.TryGetInt64(out var integer) => integer,
        JsonValueKind.Number => element.GetDouble(),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        _ => null,
    };

    private async Task<string> ListResourcesAsync(string? serverId, CancellationToken cancellationToken)
    {
        var resources = new List<object>();
        var templates = new List<object>();
        foreach (var server in SelectServers(serverId))
        {
            foreach (var resource in await server.Client.ListResourcesAsync(cancellationToken: cancellationToken).ConfigureAwait(false))
                resources.Add(new { server = server.Configuration.Id, serverName = server.Configuration.Name, resource.Uri, resource.Name, resource.Description, resource.MimeType });
            foreach (var template in await server.Client.ListResourceTemplatesAsync(cancellationToken: cancellationToken).ConfigureAwait(false))
                templates.Add(new { server = server.Configuration.Id, serverName = server.Configuration.Name, template.UriTemplate, template.Name, template.Description, template.MimeType });
        }
        return JsonSerializer.Serialize(new { ok = true, resources, templates });
    }

    private async Task<string> ReadResourceAsync(string serverId, string uri, CancellationToken cancellationToken)
    {
        var server = SelectServer(serverId);
        var result = await server.Client.ReadResourceAsync(uri, cancellationToken: cancellationToken).ConfigureAwait(false);
        var contents = result.Contents.Select(content => content switch
        {
            TextResourceContents text => (object)new { type = "text", text.Uri, text.MimeType, text = text.Text },
            BlobResourceContents blob => new { type = "blob", blob.Uri, blob.MimeType, data = Convert.ToBase64String(blob.Blob.Span) },
            _ => new { type = "unknown", content.Uri, content.MimeType },
        }).ToArray();
        return JsonSerializer.Serialize(new { ok = true, server = serverId, uri, contents });
    }

    private async Task<string> ListPromptsAsync(string? serverId, CancellationToken cancellationToken)
    {
        var prompts = new List<object>();
        foreach (var server in SelectServers(serverId))
            foreach (var prompt in await server.Client.ListPromptsAsync(cancellationToken: cancellationToken).ConfigureAwait(false))
                prompts.Add(new
                {
                    server = server.Configuration.Id, serverName = server.Configuration.Name, prompt.Name, prompt.Description,
                    arguments = prompt.ProtocolPrompt.Arguments?.Select(argument => new { argument.Name, argument.Description, required = argument.Required is true }),
                });
        return JsonSerializer.Serialize(new { ok = true, prompts });
    }

    private async Task<string> GetPromptAsync(string serverId, string name, JsonElement argumentsElement, CancellationToken cancellationToken)
    {
        var server = SelectServer(serverId);
        Dictionary<string, object?> arguments = argumentsElement.ValueKind == JsonValueKind.Object
            ? argumentsElement.EnumerateObject().ToDictionary(property => property.Name, property => ToObject(property.Value), StringComparer.Ordinal)
            : [];
        var result = await server.Client.GetPromptAsync(name, arguments, cancellationToken: cancellationToken).ConfigureAwait(false);
        var messages = result.Messages.Select(message => new
        {
            role = message.Role.ToString().ToLowerInvariant(),
            content = message.Content switch
            {
                TextContentBlock text => (object)new { type = "text", text = text.Text },
                ImageContentBlock image => new { type = "image", image.MimeType, data = Convert.ToBase64String(image.Data.Span) },
                EmbeddedResourceBlock resource => new { type = "resource", resource = resource.Resource },
                _ => new { type = message.Content.Type },
            },
        }).ToArray();
        return JsonSerializer.Serialize(new { ok = true, server = serverId, name, result.Description, messages });
    }

    private IEnumerable<ConnectedServer> SelectServers(string? serverId) => string.IsNullOrWhiteSpace(serverId)
        ? _clients.Values : [SelectServer(serverId)];

    private ConnectedServer SelectServer(string serverId) => _clients.TryGetValue(serverId, out var server)
        ? server : throw new InvalidOperationException($"MCP 服务器未连接：{serverId}");

    private static string? OptionalString(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static string RequiredString(JsonElement root, string name) => OptionalString(root, name) is { Length: > 0 } value
        ? value : throw new InvalidDataException($"缺少参数：{name}");

    private static string SerializeResult(CallToolResult result)
    {
        var content = result.Content.Select(block => block switch
        {
            TextContentBlock text => (object)new { type = "text", text = text.Text },
            ImageContentBlock image => new { type = "image", mimeType = image.MimeType, data = Convert.ToBase64String(image.Data.Span) },
            AudioContentBlock audio => new { type = "audio", mimeType = audio.MimeType, data = Convert.ToBase64String(audio.Data.Span) },
            EmbeddedResourceBlock resource => new { type = "resource", resource = resource.Resource },
            ResourceLinkBlock link => new { type = "resource_link", uri = link.Uri, name = link.Name, description = link.Description },
            _ => new { type = block.Type },
        }).ToArray();
        return JsonSerializer.Serialize(new
        {
            ok = result.IsError is not true,
            isError = result.IsError is true,
            content,
            structuredContent = result.StructuredContent,
        });
    }

    private async Task DisconnectAllAsync()
    {
        foreach (var server in _clients.Values)
            try { await server.Client.DisposeAsync().ConfigureAwait(false); } catch { }
        _clients.Clear();
        lock (_tools) _tools.Clear();
        _statuses = [];
    }

    public async ValueTask DisposeAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try { await DisconnectAllAsync().ConfigureAwait(false); }
        finally { _gate.Release(); _gate.Dispose(); }
    }

    private static object Function(string name, string description, object parameters) => new { type = "function", name, description, parameters, strict = false };
}
