using ChatGPTConnector.Core;
using Xunit;

namespace ChatGPTConnector.Core.Tests;

public sealed class LocalGatewayProxyTests
{
    [Fact]
    public void KeepsAnAbsoluteRequestUrlOnTheConfiguredGatewayOrigin()
    {
        var result = LocalGatewayProxy.ResolveTarget(
            new Uri("https://520skx.com/base/"),
            new Uri("https://unexpected.example/v1/responses?stream=true"));

        Assert.Equal("https://520skx.com/v1/responses?stream=true", result.AbsoluteUri);
    }

    [Fact]
    public void PreservesLegitimateGatewayPathsAndQueries()
    {
        var result = LocalGatewayProxy.ResolveTarget(
            new Uri("https://520skx.com/"),
            new Uri("http://127.0.0.1:51234/v1/responses?stream=true"));

        Assert.Equal("https://520skx.com/v1/responses?stream=true", result.AbsoluteUri);
    }
}
