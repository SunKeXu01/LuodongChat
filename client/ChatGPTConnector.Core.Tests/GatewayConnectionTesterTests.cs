using System.Net;
using System.Text;
using ChatGPTConnector.Core;
using Xunit;

namespace ChatGPTConnector.Core.Tests;

public sealed class GatewayConnectionTesterTests
{
    [Fact]
    public async Task ValidatesHealthAndGatewayKey()
    {
        var requests = new List<HttpRequestMessage>();
        var handler = new StubHandler(request =>
        {
            requests.Add(request);
            return request.RequestUri!.AbsolutePath == "/healthz"
                ? new HttpResponseMessage(HttpStatusCode.OK)
                : new HttpResponseMessage(HttpStatusCode.OK);
        });
        var tester = new GatewayConnectionTester(new HttpClient(handler));

        var result = await tester.TestAsync(new(
            new Uri("https://520skx.com"),
            "gw_test_key"));

        Assert.True(result.Success);
        Assert.Equal(2, requests.Count);
        Assert.Equal("Bearer", requests[1].Headers.Authorization!.Scheme);
        Assert.Equal("gw_test_key", requests[1].Headers.Authorization!.Parameter);
    }

    [Fact]
    public async Task ReturnsFriendlyInvalidKeyError()
    {
        var handler = new StubHandler(request => request.RequestUri!.AbsolutePath == "/healthz"
            ? new HttpResponseMessage(HttpStatusCode.OK)
            : new HttpResponseMessage(HttpStatusCode.Unauthorized)
            {
                Content = new StringContent("{\"error\":{\"code\":\"invalid_gateway_key\"}}", Encoding.UTF8, "application/json"),
            });
        var result = await new GatewayConnectionTester(new HttpClient(handler)).TestAsync(
            new(new Uri("https://520skx.com"), "wrong"));

        Assert.False(result.Success);
        Assert.Equal("invalid_gateway_key", result.Code);
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) => Task.FromResult(responder(request));
    }
}
