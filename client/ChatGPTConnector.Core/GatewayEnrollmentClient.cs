using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace ChatGPTConnector.Core;

public sealed record EnrollmentResult(bool Success, string? GatewayKey, string Message, bool ActiveKeyExists = false);

public sealed class GatewayEnrollmentClient(HttpClient http)
{
    public async Task<(bool Success, string Message)> RequestCodeAsync(Uri gateway, string email, CancellationToken cancellationToken = default)
    {
        using var response = await http.PostAsJsonAsync(new Uri(gateway, "/enrollment/code"), new { email }, cancellationToken);
        if (response.IsSuccessStatusCode) return (true, "验证码已发送，请检查邮箱。");
        return (false, await ErrorMessageAsync(response, "验证码发送失败。", cancellationToken));
    }

    public async Task<EnrollmentResult> VerifyAsync(Uri gateway, string email, string code, bool rotate = false, CancellationToken cancellationToken = default)
    {
        using var response = await http.PostAsJsonAsync(new Uri(gateway, "/enrollment/verify"), new { email, code, rotate }, cancellationToken);
        var text = await response.Content.ReadAsStringAsync(cancellationToken);
        if (response.IsSuccessStatusCode)
        {
            using var document = JsonDocument.Parse(text);
            var key = document.RootElement.GetProperty("key").GetString();
            return string.IsNullOrWhiteSpace(key)
                ? new(false, null, "服务器没有返回网关密钥。")
                : new(true, key, "网关密钥已领取并填入。请立即开启连接。");
        }
        var active = response.StatusCode == HttpStatusCode.Conflict && text.Contains("active_key_exists", StringComparison.Ordinal);
        return new(false, null, active ? "该邮箱已有有效密钥。如需换到当前设备，请确认轮换；旧密钥会立即失效。" : ErrorMessage(text, "验证码无效或已过期。"), active);
    }

    private static async Task<string> ErrorMessageAsync(HttpResponseMessage response, string fallback, CancellationToken cancellationToken) =>
        ErrorMessage(await response.Content.ReadAsStringAsync(cancellationToken), fallback);

    private static string ErrorMessage(string json, string fallback)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            return document.RootElement.GetProperty("error").TryGetProperty("message", out var message)
                ? message.GetString() ?? fallback : fallback;
        }
        catch (JsonException) { return fallback; }
    }
}
