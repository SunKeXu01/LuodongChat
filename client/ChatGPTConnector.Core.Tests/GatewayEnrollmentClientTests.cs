using System.Net;
using System.Text;
using ChatGPTConnector.Core;
using Xunit;

namespace ChatGPTConnector.Core.Tests;

public sealed class GatewayEnrollmentClientTests
{
    [Fact]
    public async Task ReadsSelfServiceAvailability()
    {
        var handler = new StubHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{\"enabled\":true}", Encoding.UTF8, "application/json")
        });
        Assert.True(await new GatewayEnrollmentClient(new HttpClient(handler)).IsEnabledAsync(new Uri("https://gateway.example")));
    }

    [Fact]
    public async Task RequestsCodeAndReadsIssuedKey()
    {
        var handler = new StubHandler((request, call) => call == 1
            ? new HttpResponseMessage(HttpStatusCode.Accepted) { Content = new StringContent("{\"status\":\"code_sent\"}", Encoding.UTF8, "application/json") }
            : new HttpResponseMessage(HttpStatusCode.Created) { Content = new StringContent("{\"key\":\"gw_example\",\"prefix\":\"gw_exampl\"}", Encoding.UTF8, "application/json") });
        var client = new GatewayEnrollmentClient(new HttpClient(handler));
        Assert.True((await client.RequestCodeAsync(new Uri("https://gateway.example"), "user@example.com")).Success);
        var result = await client.VerifyAsync(new Uri("https://gateway.example"), "user@example.com", "123456");
        Assert.True(result.Success);
        Assert.Equal("gw_example", result.GatewayKey);
    }

    [Fact]
    public async Task IdentifiesExplicitRotationConflict()
    {
        var handler = new StubHandler((_, _) => new HttpResponseMessage(HttpStatusCode.Conflict)
        {
            Content = new StringContent("{\"error\":{\"code\":\"active_key_exists\"}}", Encoding.UTF8, "application/json")
        });
        var result = await new GatewayEnrollmentClient(new HttpClient(handler))
            .VerifyAsync(new Uri("https://gateway.example"), "user@example.com", "123456");
        Assert.True(result.ActiveKeyExists);
    }

    private sealed class StubHandler(Func<HttpRequestMessage, int, HttpResponseMessage> response) : HttpMessageHandler
    {
        private int calls;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(response(request, Interlocked.Increment(ref calls)));
    }
}
