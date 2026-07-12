using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace ChatGPTConnector.Core;

public sealed record ConnectionTestResult(bool Success, string Code, string Message);

public sealed class GatewayConnectionTester(HttpClient httpClient)
{
    public async Task<ConnectionTestResult> TestAsync(
        ConnectorSettings settings,
        CancellationToken cancellationToken = default)
    {
        settings.Validate();
        try
        {
            using var health = await httpClient.GetAsync(
                new Uri(settings.GatewayBaseUri, "/healthz"),
                cancellationToken);
            if (!health.IsSuccessStatusCode)
                return new(false, "gateway_unhealthy", "网关健康检查失败。");

            using var request = new HttpRequestMessage(
                HttpMethod.Post,
                new Uri(settings.GatewayBaseUri, "/responses"));
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", settings.GatewayKey);
            request.Content = JsonContent.Create(new
            {
                model = settings.Model,
                input = "Reply with exactly: OK",
                max_output_tokens = 16,
                stream = false,
            });
            using var response = await httpClient.SendAsync(request, cancellationToken);
            if (response.IsSuccessStatusCode)
                return new(true, "ok", "网关连接和密钥验证成功。");
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                return new(false, "invalid_gateway_key", "网关密钥无效或已撤销。");
            if ((int)response.StatusCode == 429)
                return new(false, "gateway_limit", "当前请求频率、并发或额度已达到限制。");

            var errorCode = await ReadErrorCodeAsync(response, cancellationToken);
            return new(false, errorCode ?? "gateway_request_failed", $"网关测试失败（HTTP {(int)response.StatusCode}）。");
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new(false, "gateway_timeout", "连接网关超时。");
        }
        catch (HttpRequestException)
        {
            return new(false, "gateway_unreachable", "无法连接网关，请检查网络和TLS设置。");
        }
    }

    private static async Task<string?> ReadErrorCodeAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            var json = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);
            return json.TryGetProperty("error", out var error)
                && error.TryGetProperty("code", out var code)
                ? code.GetString()
                : null;
        }
        catch (Exception error) when (error is JsonException or NotSupportedException)
        {
            return null;
        }
    }
}
