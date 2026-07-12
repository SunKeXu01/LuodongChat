namespace ChatGPTConnector.Core;

public sealed record ConnectorSettings(
    Uri GatewayBaseUri,
    string GatewayKey,
    string Model = "gpt-5.5",
    string ProviderId = "ChatGPTConnector",
    string ReasoningEffort = "xhigh")
{
    public void Validate()
    {
        if (GatewayBaseUri.Scheme != Uri.UriSchemeHttps)
            throw new ArgumentException("Gateway URL must use HTTPS.", nameof(GatewayBaseUri));
        if (string.IsNullOrWhiteSpace(GatewayKey))
            throw new ArgumentException("Gateway key is required.", nameof(GatewayKey));
        if (string.IsNullOrWhiteSpace(Model))
            throw new ArgumentException("Model is required.", nameof(Model));
        if (string.IsNullOrWhiteSpace(ProviderId))
            throw new ArgumentException("Provider ID is required.", nameof(ProviderId));
    }
}
