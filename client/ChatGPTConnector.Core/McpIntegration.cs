using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace ChatGPTConnector.Core;

public enum McpTransportKind { Stdio, Http }

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
    {
        await EnsureConnectedAsync(cancellationToken).ConfigureAwait(false);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            lock (_tools) return _tools.Values.Select(tool => (object)new
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
