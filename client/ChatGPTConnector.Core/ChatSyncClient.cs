using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

namespace ChatGPTConnector.Core;

public sealed record SyncedChatMessage(string Id, string ConversationId, string Role, string Content, DateTimeOffset ClientCreatedAt, DateTimeOffset? UpdatedAt = null);

public sealed class ChatSyncClient(HttpClient http)
{
    public async Task<string> StreamResponseAsync(
        Uri gateway, string token, IReadOnlyList<SyncedChatMessage> messages,
        IProgress<string>? progress = null, CancellationToken cancellationToken = default)
    {
        using var request = Authorized(HttpMethod.Post, new Uri(gateway, "/v1/responses"), token);
        request.Headers.Accept.ParseAdd("text/event-stream");
        request.Content = JsonContent.Create(new {
            model = "gpt-5.6-sol", stream = true,
            input = messages.Select(message => new { role = message.Role, content = message.Content }),
        });
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        var result = new StringBuilder();
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
            }
            catch (JsonException) { }
        }
        return result.ToString();
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
        throw new InvalidOperationException(message);
    }
}
