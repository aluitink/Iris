using System.Net;
using System.Net.Http;
using Xunit;

namespace Iris.Server.Tests;

/// <summary>
/// Tests for <see cref="ServerOutboundTransport"/> — the bounded connection-phase transport for
/// server-side outbound federation dials (139.3-s6 Finding 1).
/// </summary>
public class ServerOutboundTransportTests
{
    [Fact]
    public void Create_Default_ReturnsSocketsHttpHandler_WithFiveSecondConnectTimeout()
    {
        var handler = ServerOutboundTransport.Create();

        Assert.IsType<SocketsHttpHandler>(handler);
        Assert.Equal(TimeSpan.FromSeconds(5), handler.ConnectTimeout);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(10)]
    public void Create_CustomTimeout_ReturnsSocketsHttpHandler_WithThatConnectTimeout(int seconds)
    {
        var handler = ServerOutboundTransport.Create(TimeSpan.FromSeconds(seconds));

        Assert.IsType<SocketsHttpHandler>(handler);
        Assert.Equal(TimeSpan.FromSeconds(seconds), handler.ConnectTimeout);
    }

    [Fact]
    public void Create_ZeroTimeout_ThrowsArgumentOutOfRange()
    {
        // SocketsHttpHandler.ConnectTimeout does not accept TimeSpan.Zero (the setter rejects it), so
        // this helper rejects it too — a zero bound is not a meaningful, bounded connection phase.
        Assert.Throws<ArgumentOutOfRangeException>(
            () => ServerOutboundTransport.Create(TimeSpan.Zero));
    }

    [Fact]
    public void Create_NegativeTimeout_ThrowsArgumentOutOfRange()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => ServerOutboundTransport.Create(TimeSpan.FromSeconds(-1)));
    }

    [Fact]
    public void Create_ConfiguresPoolingAndRedirectDefaults()
    {
        var handler = ServerOutboundTransport.Create();

        // Pooling: do not pin stale connections (24 h lifetime, 2 min idle).
        Assert.Equal(TimeSpan.FromHours(24), handler.PooledConnectionLifetime);
        Assert.Equal(TimeSpan.FromMinutes(2), handler.PooledConnectionIdleTimeout);
        // Outbound federation GETs (actor docs, collections, media) follow redirects.
        Assert.True(handler.AllowAutoRedirect);
        Assert.Equal(DecompressionMethods.All, handler.AutomaticDecompression);
    }
}
