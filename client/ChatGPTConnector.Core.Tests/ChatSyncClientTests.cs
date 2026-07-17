using System.Net;
using System.Text;
using System.Text.Json;
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

            data: {"type":"response.completed","response":{"output":[{"type":"message","content":[{"type":"output_text","text":"你好","annotations":[{"type":"url_citation","url":"https://example.com/news","title":"示例来源"}]}]}]}}

            data: [DONE]

            """;
        var handler = new StubHandler(events, "text/event-stream");
        var deltas = new List<string>();
        var result = await new ChatSyncClient(new HttpClient(handler)).StreamResponseAsync(
            new Uri("https://gateway.example"), "usr_test", [], new InlineProgress(deltas.Add));
        Assert.Equal("你好", result.Text);
        Assert.Equal(new ChatCitation("示例来源", "https://example.com/news"), Assert.Single(result.Citations));
        Assert.Equal(["你", "好"], deltas);
        Assert.Equal("Bearer", handler.AuthorizationScheme);
        Assert.Equal("usr_test", handler.AuthorizationValue);
    }

    [Fact]
    public async Task EnablesWebSearchAndFallsBackWhenUpstreamDoesNotSupportIt()
    {
        var handler = new SequenceHandler();
        var result = await new ChatSyncClient(new HttpClient(handler)).StreamResponseAsync(
            new Uri("https://gateway.example"), "usr_test", [], enableWebSearch: true);

        Assert.True(result.WebSearchUnavailable);
        Assert.Equal("普通回答", result.Text);
        Assert.Equal(2, handler.Bodies.Count);
        using var first = JsonDocument.Parse(handler.Bodies[0]);
        Assert.Equal("web_search", first.RootElement.GetProperty("tools")[0].GetProperty("type").GetString());
        Assert.Equal("medium", first.RootElement.GetProperty("tools")[0].GetProperty("search_context_size").GetString());
        using var second = JsonDocument.Parse(handler.Bodies[1]);
        Assert.False(second.RootElement.TryGetProperty("tools", out _));
    }

    [Fact]
    public async Task ReportsOnlyARealWebSearchCallAsPerformed()
    {
        var events = """
            data: {"type":"response.output_item.done","item":{"type":"web_search_call","status":"completed"}}

            data: {"type":"response.completed","response":{"output":[{"type":"web_search_call","status":"completed"},{"type":"message","content":[{"type":"output_text","text":"联网回答","annotations":[{"type":"url_citation","url":"https://example.com/live","title":"实时来源"}]}]}]}}

            data: [DONE]

            """;
        var result = await new ChatSyncClient(new HttpClient(new CapturingHandler(events))).StreamResponseAsync(
            new Uri("https://gateway.example"), "usr_test", [], enableWebSearch: true);

        Assert.True(result.WebSearchPerformed);
        Assert.False(result.WebSearchUnavailable);
        Assert.Equal(new ChatCitation("实时来源", "https://example.com/live"), Assert.Single(result.Citations));
    }

    [Fact]
    public async Task RequestsAndParsesImageGenerationInsideTheNormalConversation()
    {
        var events = """
            data: {"type":"response.completed","response":{"output":[{"type":"image_generation_call","result":"aGVsbG8="}]}}

            data: [DONE]

            """;
        var handler = new CapturingHandler(events);
        var result = await new ChatSyncClient(new HttpClient(handler)).StreamResponseAsync(
            new Uri("https://gateway.example"), "usr_test", [], enableImageGeneration: true);

        Assert.Equal("aGVsbG8=", Assert.Single(result.Images).Base64);
        using var body = JsonDocument.Parse(handler.Body!);
        Assert.Equal("image_generation", body.RootElement.GetProperty("tools")[0].GetProperty("type").GetString());
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


    private sealed class SequenceHandler : HttpMessageHandler
    {
        public List<string> Bodies { get; } = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Bodies.Add(await request.Content!.ReadAsStringAsync(cancellationToken));
            if (Bodies.Count == 1)
                return new HttpResponseMessage(HttpStatusCode.BadRequest) { Content = new StringContent("{\"error\":{\"message\":\"unsupported tool\"}}", Encoding.UTF8, "application/json") };
            return new HttpResponseMessage(HttpStatusCode.OK) {
                Content = new StringContent("data: {\"type\":\"response.output_text.delta\",\"delta\":\"普通回答\"}\n\ndata: [DONE]\n\n", Encoding.UTF8, "text/event-stream"),
            };
        }
    }

    private sealed class CapturingHandler(string events) : HttpMessageHandler
    {
        public string? Body { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Body = await request.Content!.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(events, Encoding.UTF8, "text/event-stream") };
        }
    }
}
