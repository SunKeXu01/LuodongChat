using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace ChatGPTConnector.Core;

public sealed record AccountProfile(string Id, string Email, string Nickname, string? AvatarMediaType, string? AvatarBase64, long BalanceMicrounits);
public sealed record AccountSession(string AccessToken, string GatewayKey, AccountProfile Profile);

public sealed class AccountClient(HttpClient http)
{
    public async Task RequestCodeAsync(Uri gateway, string email, CancellationToken cancellationToken = default)
    {
        using var response = await http.PostAsJsonAsync(new Uri(gateway, "/account/code"), new { email }, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
    }

    public async Task<AccountSession> VerifyAsync(Uri gateway, string email, string code, CancellationToken cancellationToken = default)
    {
        using var response = await http.PostAsJsonAsync(new Uri(gateway, "/account/verify"), new { email, code }, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        var root = document.RootElement;
        return new AccountSession(root.GetProperty("accessToken").GetString()!, root.GetProperty("gatewayKey").GetString()!, ParseProfile(root.GetProperty("profile")));
    }

    public async Task<AccountProfile?> GetProfileAsync(Uri gateway, string accessToken, CancellationToken cancellationToken = default)
    {
        using var request = Authorized(HttpMethod.Get, new Uri(gateway, "/account/profile"), accessToken);
        using var response = await http.SendAsync(request, cancellationToken);
        if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized) return null;
        await EnsureSuccessAsync(response, cancellationToken);
        return ParseProfile(JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken)).RootElement);
    }

    public async Task<AccountProfile> UpdateProfileAsync(Uri gateway, string accessToken, string nickname, CancellationToken cancellationToken = default) =>
        await SendProfileAsync(gateway, accessToken, "/account/profile", HttpMethod.Patch, new { nickname }, cancellationToken);

    public async Task<AccountProfile> UpdateAvatarAsync(Uri gateway, string accessToken, string mediaType, string dataBase64, CancellationToken cancellationToken = default) =>
        await SendProfileAsync(gateway, accessToken, "/account/avatar", HttpMethod.Put, new { mediaType, dataBase64 }, cancellationToken);

    public async Task LogoutAsync(Uri gateway, string accessToken, CancellationToken cancellationToken = default)
    {
        using var request = Authorized(HttpMethod.Post, new Uri(gateway, "/account/logout"), accessToken);
        using var response = await http.SendAsync(request, cancellationToken);
        if (response.StatusCode != System.Net.HttpStatusCode.Unauthorized) await EnsureSuccessAsync(response, cancellationToken);
    }

    private async Task<AccountProfile> SendProfileAsync(Uri gateway, string token, string path, HttpMethod method, object body, CancellationToken cancellationToken)
    {
        using var request = Authorized(method, new Uri(gateway, path), token);
        request.Content = JsonContent.Create(body);
        using var response = await http.SendAsync(request, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        return ParseProfile(JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken)).RootElement);
    }

    private static HttpRequestMessage Authorized(HttpMethod method, Uri uri, string token) => new(method, uri) { Headers = { Authorization = new AuthenticationHeaderValue("Bearer", token) } };

    private static AccountProfile ParseProfile(JsonElement value) => new(
        value.GetProperty("id").GetString()!, value.GetProperty("email").GetString()!, value.GetProperty("nickname").GetString()!,
        value.TryGetProperty("avatarMediaType", out var media) && media.ValueKind == JsonValueKind.String ? media.GetString() : null,
        value.TryGetProperty("avatarBase64", out var avatar) && avatar.ValueKind == JsonValueKind.String ? avatar.GetString() : null,
        value.GetProperty("balanceMicrounits").GetInt64());

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode) return;
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        try
        {
            var message = JsonDocument.Parse(body).RootElement.GetProperty("error").GetProperty("message").GetString();
            throw new InvalidOperationException(message ?? $"服务器返回错误 {(int)response.StatusCode}");
        }
        catch (JsonException) { throw new InvalidOperationException($"服务器返回错误 {(int)response.StatusCode}"); }
    }
}
