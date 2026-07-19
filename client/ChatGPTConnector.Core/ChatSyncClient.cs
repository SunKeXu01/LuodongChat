using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

namespace ChatGPTConnector.Core;

public sealed record SyncedChatMessage(
    string Id, string ConversationId, string Role, string Content, DateTimeOffset ClientCreatedAt,
    DateTimeOffset? UpdatedAt = null, IReadOnlyList<ChatCitation>? Citations = null,
    IReadOnlyList<GeneratedChatImage>? Images = null, IReadOnlyList<LocalChatAttachment>? Attachments = null);
public sealed record LocalChatAttachment(string RelativePath, string Name, string MimeType, long Size, string Category);
public sealed record ChatCitation(string Title, string Url);
public sealed record GeneratedChatImage(string RelativePath, string MediaType);
public sealed record GeneratedImageData(string Base64, string MediaType);
public sealed record ChatStreamResult(
    string Text, IReadOnlyList<ChatCitation> Citations, IReadOnlyList<GeneratedImageData> Images,
    bool WebSearchUnavailable = false, bool WebSearchPerformed = false);

internal sealed record ChatStreamPassResult(
    string Text, IReadOnlyList<ChatCitation> Citations, IReadOnlyList<GeneratedImageData> Images,
    bool WebSearchPerformed, IReadOnlyList<LocalToolCall> ToolCalls, IReadOnlyList<JsonElement> OutputItems);

public sealed class ChatSyncClient(HttpClient http)
{
    public async Task<ChatStreamResult> StreamResponseAsync(
        Uri gateway, string token, IReadOnlyList<SyncedChatMessage> messages,
        IProgress<string>? progress = null, CancellationToken cancellationToken = default,
        bool enableWebSearch = false, bool enableImageGeneration = false, IReadOnlyList<string>? attachmentIds = null,
        bool hasReferenceImages = false, IReadOnlyList<object>? localTools = null,
        Func<LocalToolCall, CancellationToken, Task<string>>? executeLocalTool = null)
    {
        try
        {
            return await StreamWithToolsAsync(gateway, token, messages, progress, cancellationToken, enableWebSearch,
                enableImageGeneration, attachmentIds, hasReferenceImages, localTools, executeLocalTool);
        }
        catch (ResponseRequestException error) when (enableWebSearch && error.StatusCode is
            HttpStatusCode.BadRequest or HttpStatusCode.NotFound or HttpStatusCode.UnprocessableEntity or
            HttpStatusCode.BadGateway or HttpStatusCode.ServiceUnavailable or HttpStatusCode.GatewayTimeout)
        {
            var fallback = await StreamWithToolsAsync(gateway, token, messages, progress, cancellationToken, false,
                enableImageGeneration, attachmentIds, hasReferenceImages, localTools, executeLocalTool);
            return fallback with { WebSearchUnavailable = true };
        }
    }

    private async Task<ChatStreamResult> StreamWithToolsAsync(
        Uri gateway, string token, IReadOnlyList<SyncedChatMessage> messages,
        IProgress<string>? progress, CancellationToken cancellationToken, bool enableWebSearch, bool enableImageGeneration,
        IReadOnlyList<string>? attachmentIds, bool hasReferenceImages, IReadOnlyList<object>? localTools,
        Func<LocalToolCall, CancellationToken, Task<string>>? executeLocalTool)
    {
        var input = new List<object>(messages.Select(message => (object)new { role = message.Role, content = message.Content }));
        var text = new StringBuilder();
        var citations = new Dictionary<string, ChatCitation>(StringComparer.OrdinalIgnoreCase);
        var images = new List<GeneratedImageData>();
        var webSearchPerformed = false;
        // Command sessions may need an initial exec plus several output polls. Keep the loop
        // bounded, but allow enough turns for a normal build/test workflow to finish.
        for (var round = 0; round < 12; round++)
        {
            var pass = await StreamOnceAsync(gateway, token, input, progress, cancellationToken, enableWebSearch,
                enableImageGeneration, round == 0 ? attachmentIds : null, hasReferenceImages, localTools);
            text.Append(pass.Text);
            foreach (var citation in pass.Citations) citations[citation.Url] = citation;
            images.AddRange(pass.Images);
            webSearchPerformed |= pass.WebSearchPerformed;
            if (pass.ToolCalls.Count == 0)
                return new ChatStreamResult(text.ToString(), citations.Values.ToArray(), images, WebSearchPerformed: webSearchPerformed);
            if (executeLocalTool is null) throw new InvalidOperationException("模型请求了本地工具，但客户端没有配置工具执行器。");
            foreach (var outputItem in pass.OutputItems) input.Add(outputItem);
            foreach (var call in pass.ToolCalls)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var output = await executeLocalTool(call, cancellationToken);
                input.Add(new { type = "function_call_output", call_id = call.CallId, output });
            }
        }
        throw new InvalidOperationException("本次文件操作步骤过多，已为安全起见停止。请缩小任务范围后重试。");
    }

    private async Task<ChatStreamPassResult> StreamOnceAsync(
        Uri gateway, string token, IReadOnlyList<object> input,
        IProgress<string>? progress, CancellationToken cancellationToken, bool enableWebSearch, bool enableImageGeneration,
        IReadOnlyList<string>? attachmentIds, bool hasReferenceImages, IReadOnlyList<object>? localTools)
    {
        using var request = Authorized(HttpMethod.Post, new Uri(gateway, "/v1/responses"), token);
        request.Headers.Accept.ParseAdd("text/event-stream");
        var payload = new Dictionary<string, object?>
        {
            ["model"] = enableWebSearch ? "gpt-5.6" : "gpt-5.6-sol",
            ["stream"] = true,
            ["input"] = input,
        };
        if (attachmentIds is { Count: > 0 }) payload["attachment_ids"] = attachmentIds;
        var tools = new List<object>();
        if (enableWebSearch)
            tools.Add(new { type = "web_search", search_context_size = "medium" });
        if (enableImageGeneration)
            tools.Add(new { type = "image_generation", action = hasReferenceImages ? "edit" : "generate", size = "auto", quality = "auto" });
        if (localTools is { Count: > 0 }) tools.AddRange(localTools);
        if (tools.Count > 0)
        {
            payload["tools"] = tools;
            payload["tool_choice"] = "auto";
        }
        request.Content = JsonContent.Create(payload);
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        var result = new StringBuilder();
        var citations = new Dictionary<string, ChatCitation>(StringComparer.OrdinalIgnoreCase);
        var images = new List<GeneratedImageData>();
        var toolCalls = new List<LocalToolCall>();
        var outputItems = new List<JsonElement>();
        var webSearchPerformed = false;
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
                var eventType = type.GetString();
                if (eventType?.StartsWith("response.web_search_call.", StringComparison.Ordinal) == true)
                    webSearchPerformed = true;
                if (root.TryGetProperty("item", out var item)
                    && item.TryGetProperty("type", out var itemType) && itemType.GetString() == "web_search_call")
                    webSearchPerformed = true;
                if (eventType == "response.completed")
                    webSearchPerformed |= ExtractCompletedOutput(root, citations, images, toolCalls, outputItems);
            }
            catch (JsonException) { }
        }
        return new ChatStreamPassResult(result.ToString(), citations.Values.ToArray(), images, webSearchPerformed, toolCalls, outputItems);
    }

    private static bool ExtractCompletedOutput(
        JsonElement root, IDictionary<string, ChatCitation> citations, ICollection<GeneratedImageData> images,
        ICollection<LocalToolCall> toolCalls, ICollection<JsonElement> outputItems)
    {
        if (!root.TryGetProperty("response", out var response)
            || !response.TryGetProperty("output", out var output) || output.ValueKind != JsonValueKind.Array) return false;
        var webSearchPerformed = false;
        foreach (var item in output.EnumerateArray())
        {
            outputItems.Add(item.Clone());
            if (item.TryGetProperty("type", out var functionType) && functionType.GetString() == "function_call"
                && item.TryGetProperty("call_id", out var callIdElement) && callIdElement.GetString() is { Length: > 0 } callId
                && item.TryGetProperty("name", out var nameElement) && nameElement.GetString() is { Length: > 0 } name)
            {
                var arguments = item.TryGetProperty("arguments", out var argumentsElement) ? argumentsElement.GetString() ?? "{}" : "{}";
                toolCalls.Add(new LocalToolCall(callId, name, arguments));
                continue;
            }
            if (item.TryGetProperty("type", out var itemType) && itemType.GetString() == "web_search_call")
            {
                webSearchPerformed = true;
                continue;
            }
            if (item.TryGetProperty("type", out itemType) && itemType.GetString() == "image_generation_call"
                && item.TryGetProperty("result", out var imageResult)
                && imageResult.GetString() is { Length: > 0 } base64)
            {
                images.Add(new GeneratedImageData(base64, "image/png"));
                continue;
            }
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
        return webSearchPerformed;
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
