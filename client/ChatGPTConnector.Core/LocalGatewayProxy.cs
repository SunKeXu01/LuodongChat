using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace ChatGPTConnector.Core;

public sealed class LocalGatewayProxy(HttpClient http, Uri upstream, string accountAccessToken, string localAccessKey, int port = 51234) : IAsyncDisposable
{
    private readonly HttpListener _listener = new();
    private readonly CancellationTokenSource _stopping = new();
    private Task? _loop;
    public Uri BaseUri { get; } = new($"http://127.0.0.1:{port}");

    public void Start()
    {
        _listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        _listener.Start();
        _loop = AcceptLoopAsync();
    }

    private async Task AcceptLoopAsync()
    {
        while (!_stopping.IsCancellationRequested)
        {
            try { _ = ForwardAsync(await _listener.GetContextAsync()); }
            catch (Exception) when (_stopping.IsCancellationRequested || !_listener.IsListening) { break; }
        }
    }

    private async Task ForwardAsync(HttpListenerContext context)
    {
        try
        {
            if (!string.Equals(context.Request.Headers["Authorization"], $"Bearer {localAccessKey}", StringComparison.Ordinal))
            {
                context.Response.StatusCode = 401;
                return;
            }
            var target = ResolveTarget(upstream, context.Request.Url);
            using var request = new HttpRequestMessage(new HttpMethod(context.Request.HttpMethod), target);
            if (context.Request.HasEntityBody) request.Content = new StreamContent(context.Request.InputStream);
            foreach (var key in context.Request.Headers.AllKeys)
            {
                if (key is null || key.Equals("Host", StringComparison.OrdinalIgnoreCase)
                    || key.Equals("Authorization", StringComparison.OrdinalIgnoreCase)
                    || key.Equals("Content-Length", StringComparison.OrdinalIgnoreCase)) continue;
                var values = context.Request.Headers.GetValues(key);
                if (values is null) continue;
                if (!request.Headers.TryAddWithoutValidation(key, values)) request.Content?.Headers.TryAddWithoutValidation(key, values);
            }
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accountAccessToken);
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, _stopping.Token);
            context.Response.StatusCode = (int)response.StatusCode;
            foreach (var header in response.Headers.Concat(response.Content.Headers))
            {
                if (header.Key.Equals("Transfer-Encoding", StringComparison.OrdinalIgnoreCase)
                    || header.Key.Equals("Connection", StringComparison.OrdinalIgnoreCase)) continue;
                try { context.Response.Headers[header.Key] = string.Join(",", header.Value); } catch { }
            }
            await response.Content.CopyToAsync(context.Response.OutputStream, _stopping.Token);
        }
        catch (Exception error)
        {
            if (!context.Response.OutputStream.CanWrite) return;
            context.Response.StatusCode = 502;
            var body = JsonSerializer.SerializeToUtf8Bytes(new { error = new { code = "local_proxy_error", message = error.Message } });
            await context.Response.OutputStream.WriteAsync(body);
        }
        finally { context.Response.Close(); }
    }

    internal static Uri ResolveTarget(Uri upstream, Uri? requestUrl)
    {
        var target = new Uri(upstream, requestUrl?.PathAndQuery ?? "/");
        if (!string.Equals(target.Scheme, upstream.Scheme, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(target.Host, upstream.Host, StringComparison.OrdinalIgnoreCase)
            || target.Port != upstream.Port)
            throw new InvalidOperationException("Local proxy target must remain on the configured gateway origin.");
        return target;
    }

    public async ValueTask DisposeAsync()
    {
        _stopping.Cancel();
        _listener.Stop();
        _listener.Close();
        if (_loop is not null) await _loop.ConfigureAwait(false);
        _stopping.Dispose();
    }
}
