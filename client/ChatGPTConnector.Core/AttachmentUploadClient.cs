using System.Buffers;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace ChatGPTConnector.Core;

public sealed record UploadedAttachment(
    string Id, string Name, string Extension, long Size, string MimeType, string Category, DateTimeOffset ExpiresAt);

public sealed class AttachmentUploadClient(HttpClient http)
{
    public async Task<UploadedAttachment> UploadAsync(
        Uri gateway, string token, string path, string mimeType, IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, true);
        using var multipart = new MultipartFormDataContent();
        using var content = new ProgressStreamContent(stream, progress, cancellationToken);
        content.Headers.ContentType = MediaTypeHeaderValue.Parse(mimeType);
        multipart.Add(content, "file", Path.GetFileName(path));
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(gateway, "/v1/attachments")) { Content = multipart };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (!response.IsSuccessStatusCode) throw await CreateErrorAsync(response, cancellationToken);
        var result = await response.Content.ReadFromJsonAsync<UploadedAttachment>(cancellationToken: cancellationToken);
        return result ?? throw new InvalidOperationException("服务器没有返回附件信息。");
    }

    public async Task DeleteAsync(Uri gateway, string token, string id, CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Delete, new Uri(gateway, $"/v1/attachments/{Uri.EscapeDataString(id)}"));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await http.SendAsync(request, cancellationToken);
        if (response.IsSuccessStatusCode || response.StatusCode == System.Net.HttpStatusCode.NotFound) return;
        throw await CreateErrorAsync(response, cancellationToken);
    }

    private static async Task<Exception> CreateErrorAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
            if (document.RootElement.TryGetProperty("error", out var error)
                && error.TryGetProperty("message", out var message) && message.GetString() is { Length: > 0 } text)
                return new InvalidOperationException(text);
        }
        catch (JsonException) { }
        return new InvalidOperationException($"附件请求失败（HTTP {(int)response.StatusCode}）。");
    }

    private sealed class ProgressStreamContent(Stream source, IProgress<double>? progress, CancellationToken cancellationToken) : HttpContent
    {
        protected override async Task SerializeToStreamAsync(Stream target, TransportContext? context)
        {
            var buffer = ArrayPool<byte>.Shared.Rent(64 * 1024);
            try
            {
                long sent = 0;
                while (true)
                {
                    var read = await source.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken);
                    if (read == 0) break;
                    await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                    sent += read;
                    progress?.Report(source.Length == 0 ? 1 : Math.Clamp((double)sent / source.Length, 0, 1));
                }
                progress?.Report(1);
            }
            finally { ArrayPool<byte>.Shared.Return(buffer); }
        }

        protected override bool TryComputeLength(out long length) { length = source.Length; return true; }
    }
}
