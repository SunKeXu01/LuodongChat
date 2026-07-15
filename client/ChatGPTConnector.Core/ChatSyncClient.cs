using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

namespace ChatGPTConnector.Core;

public sealed record SyncedChatMessage(
    string Id, string ConversationId, string Role, string Content, DateTimeOffset ClientCreatedAt,
    DateTimeOffset? UpdatedAt = null, IReadOnlyList<ChatCitation>? Citations = null);
public sealed record ChatCitation(string Title, string Url);
public sealed record ChatStreamResult(string Text, IReadOnlyList<ChatCitation> Citations, bool WebSearchUnavailable = false);

public sealed class ChatSyncClient(HttpClient http)
{
    public async Task<ChatStreamResult> StreamResponseAsync(
        Uri gateway, string token, IReadOnlyList<SyncedChatMessage> messages,
        IProgress<string>? progress = null, CancellationToken cancellationToken = default,
        bool enableWebSearch = false)
    {
        try
        {
            return await StreamOnceAsync(gateway, token, messages, progress, cancellationToken, enableWebSearch);
        }
        catch (ResponseRequestException error) when (enableWebSearch && error.StatusCode is
            HttpStatusCode.BadRequest or HttpStatusCode.NotFound or HttpStatusCode.UnprocessableEntity or
            HttpStatusCode.BadGateway or HttpStatusCode.ServiceUnavailable or HttpStatusCode.GatewayTimeout)
        {
            var fallback = await StreamOnceAsync(gateway, token, messages, progress, cancellationToken, false);
            return fallback with { WebSearchUnavailable = true };
        }
    }

    private async Task<ChatStreamResult> StreamOnceAsync(
        Uri gateway, string token, IReadOnlyList<SyncedChatMessage> messages,
        IProgress<string>? progress, CancellationToken cancellationToken, bool enableWebSearch)
    {
        using var request = Authorized(HttpMethod.Post, new Uri(gateway, "/v1/responses"), token);
        request.Headers.Accept.ParseAdd("text/event-stream");
        var payload = new Dictionary<string, object?>
        {
            ["model"] = "gpt-5.6-sol",
            ["stream"] = true,
            ["input"] = messages.Select(message => new { role = message.Role, content = message.Content }),
        };
        if (enableWebSearch)
        {
            payload["tools"] = new[] { new { type = "web_search", search_context_size = "low" } };
            payload["tool_choice"] = "auto";
        }
        request.Content = JsonContent.Create(payload);
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        var result = new StringBuilder();
        var citations = new Dictionary<string, ChatCitation>(StringComparer.OrdinalIgnoreCase);
        while (await reader.ReadLineAsync(cancellationToken) is { } line)
        {
            if (!line.StartsWith("data:", StringComparison.Ordinal)) continue;
            var data = line[5..].Trim();
            if (data.Length == 0 || data == "[DONE]") continue;
            try
            {
                using var eventDocument = JsonDocument.Parse(data);
                var root = eventDocument.RootElement;
                if (root.TryGetProperty("type", out var type) && type.GetString() == "response.output_text.delta"
                    && root.TryGetProperty("delta", out var deltaElement) && deltaElement.GetString() is { Length: > 0 } delta)
                {
                    result.Append(delta);
                    progress?.Report(delta);
                }
                if (type.GetString() == "response.completed") ExtractCitations(root, citations);
            }
            catch (JsonException) { }
        }
        return new ChatStreamResult(result.ToString(), citations.Values.ToArray());
    }

    private static void ExtractCitations(JsonElement root, IDictionary<string, ChatCitation> citations)
    {
        if (!root.TryGetProperty("response", out var response)
            || !response.TryGetProperty("output", out var output) || output.ValueKind != JsonValueKind.Array) return;
        foreach (var item in output.EnumerateArray())
        {
            if (!item.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array) continue;
            foreach (var part in content.EnumerateArray())
            {
                if (!part.TryGetProperty("annotations", out var annotations) || annotations.ValueKind != JsonValueKind.Array) continue;
                foreach (var annotation in annotations.EnumerateArray())
                {
                    if (!annotation.TryGetProperty("type", out var type) || type.GetString() != "url_citation"
                        || !annotation.TryGetProperty("url", out var urlElement) || urlElement.GetString() is not { Length: > 0 } url
                        || !Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https")) continue;
                    var title = annotation.TryGetProperty("title", out var titleElement) ? titleElement.GetString() : null;
                    citations[url] = new ChatCitation(string.IsNullOrWhiteSpace(title) ? uri.Host : title, url);
                }
            }
        }
    }

    private static HttpRequestMessage Authorized(HttpMethod method, Uri uri, string token) =>
        new(method, uri) { Headers = { Authorization = new AuthenticationHeaderValue("Bearer", token) } };

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode) return;
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        var message = "服务器返回错误 " + (int)response.StatusCode;
        try { message = JsonDocument.Parse(body).RootElement.GetProperty("error").GetProperty("message").GetString() ?? message; }
        catch (Exception) when (body.Length < 1_000_000) { }
        throw new ResponseRequestException(response.StatusCode, message);
    }

    private sealed class ResponseRequestException(HttpStatusCode statusCode, string message) : InvalidOperationException(message)
    {
        public HttpStatusCode StatusCode { get; } = statusCode;
    }
}
