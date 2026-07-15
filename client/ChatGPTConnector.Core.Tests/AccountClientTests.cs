using System.Net;
using System.Text;
using ChatGPTConnector.Core;
using Xunit;

namespace ChatGPTConnector.Core.Tests;

public sealed class AccountClientTests
{
    [Fact]
    public async Task LoginResponseContainsOnlyAccountSessionAndProfile()
    {
        var handler = new StubHandler("""
            {"accessToken":"usr_test","profile":{"id":"user-id","email":"user@example.com","nickname":"用户","avatarMediaType":null,"avatarBase64":null,"balanceMicrounits":0}}
            """);
        var session = await new AccountClient(new HttpClient(handler)).VerifyAsync(
            new Uri("https://gateway.example"), "user@example.com", "123456");
        Assert.Equal("usr_test", session.AccessToken);
        Assert.Equal("user@example.com", session.Profile.Email);
        Assert.DoesNotContain("GatewayKey", session.GetType().GetProperties().Select(property => property.Name));
    }

    [Fact]
    public async Task PasswordLoginPostsToDedicatedEndpoint()
    {
        var handler = new StubHandler("""
            {"accessToken":"usr_test","profile":{"id":"user-id","email":"user@example.com","nickname":"用户","avatarMediaType":null,"avatarBase64":null,"balanceMicrounits":0}}
            """);
        await new AccountClient(new HttpClient(handler)).LoginAsync(new Uri("https://gateway.example"), "user@example.com", "Secure123");
        Assert.Equal("/account/login", handler.RequestUri?.AbsolutePath);
    }

    [Fact]
    public async Task RegistrationConflictPreservesTheServerErrorCode()
    {
        var handler = new StubHandler("""{"error":{"code":"account_already_registered","message":"该邮箱已注册"}}""", HttpStatusCode.Conflict);
        var error = await Assert.ThrowsAsync<AccountApiException>(() => new AccountClient(new HttpClient(handler)).RegisterAsync(
            new Uri("https://gateway.example"), "user@example.com", "Secure123", "123456"));
        Assert.Equal("account_already_registered", error.Code);
    }

    [Fact]
    public async Task PasswordResetPostsToDedicatedEndpoint()
    {
        var handler = new StubHandler("""
            {"accessToken":"usr_test","profile":{"id":"user-id","email":"user@example.com","nickname":"用户","avatarMediaType":null,"avatarBase64":null,"balanceMicrounits":0}}
            """);
        await new AccountClient(new HttpClient(handler)).ResetPasswordAsync(new Uri("https://gateway.example"), "user@example.com", "Secure123", "123456");
        Assert.Equal("/account/password/reset", handler.RequestUri?.AbsolutePath);
    }

    private sealed class StubHandler(string responseBody, HttpStatusCode statusCode = HttpStatusCode.OK) : HttpMessageHandler
    {
        public Uri? RequestUri { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestUri = request.RequestUri;
            return Task.FromResult(new HttpResponseMessage(statusCode) { Content = new StringContent(responseBody, Encoding.UTF8, "application/json") });
        }
    }
}
