using System.Net;
using System.Text;
using ChatGPTConnector.Core;
using Xunit;

namespace ChatGPTConnector.Core.Tests;

public sealed class ChatSyncClientTests
{
    [Fact]
    public async Task ParsesStreamingTextDeltasWithoutReturningEventMetadata()
    {
        var events = """
            data: {"type":"response.created","response":{"id":"one"}}

            data: {"type":"response.output_text.delta","delta":"你"}

            data: {"type":"response.output_text.delta","delta":"好"}

            data: [DONE]

            """;
        var handler = new StubHandler(events, "text/event-stream");
        var deltas = new List<string>();
        var result = await new ChatSyncClient(new HttpClient(handler)).StreamResponseAsync(
            new Uri("https://gateway.example"), "usr_test", [], new InlineProgress(deltas.Add));
        Assert.Equal("你好", result);
        Assert.Equal(["你", "好"], deltas);
        Assert.Equal("Bearer", handler.AuthorizationScheme);
        Assert.Equal("usr_test", handler.AuthorizationValue);
    }

    private sealed class InlineProgress(Action<string> report) : IProgress<string> { public void Report(string value) => report(value); }
    private sealed class StubHandler(string responseBody, string mediaType) : HttpMessageHandler
    {
        public string? AuthorizationScheme { get; private set; }
        public string? AuthorizationValue { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            AuthorizationScheme = request.Headers.Authorization?.Scheme;
            AuthorizationValue = request.Headers.Authorization?.Parameter;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(responseBody, Encoding.UTF8, mediaType) });
        }
    }
}
