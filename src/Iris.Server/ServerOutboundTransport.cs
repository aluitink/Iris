using System.Net;
using System.Net.Http;

namespace Iris.Server;

/// <summary>
/// Builds the server-side outbound <see cref="HttpMessageHandler"/> (the innermost transport of an
/// <c>IActivityPubClient</c> pipeline or a <c>DeliveryWorker</c>).
/// </summary>
/// <remarks>
/// A plain <see cref="HttpClientHandler"/> leaves the TCP/TLS connection phase to the
/// <see cref="SocketsHttpHandler"/> defaults (a 35 s <c>ConnectTimeout</c> in .NET 8+), which is NOT
/// bounded by <see cref="System.Net.Http.HttpClient.Timeout"/> (that bounds the send/read phase, not the
/// dial). A community feed that follows an unreachable peer (TCP accepted, TLS stalls) would therefore
/// hang ~60–70 s per contributor before the feed could render.
///
/// This helper returns a <see cref="SocketsHttpHandler"/> with an explicit <see cref="SocketsHttpHandler.ConnectTimeout"/>
/// (default 5 s, matching the <c>HttpClientTimeout</c> already applied to the feed's outbound client) so a
/// single unreachable peer cannot stall the others. Pooled-connection lifetimes are set to the
/// <see cref="SocketsHttpHandler"/> defaults (24 h / 2 min) to avoid pinning stale connections.
/// </remarks>
internal static class ServerOutboundTransport
{
    /// <summary>
    /// The default bound for the TCP/TLS connection phase of a server-side outbound request.
    /// </summary>
    public static readonly TimeSpan DefaultConnectTimeout = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Creates a <see cref="SocketsHttpHandler"/> with an explicit, bounded
    /// <see cref="SocketsHttpHandler.ConnectTimeout"/> for the server-side outbound transport.
    /// </summary>
    /// <param name="connectTimeout">
    /// The maximum time to establish a TCP/TLS connection before the dial is abandoned. Must be
    /// greater than <see cref="TimeSpan.Zero"/> (a zero or negative value is rejected — the
    /// <see cref="SocketsHttpHandler.ConnectTimeout"/> setter does not accept zero; pass
    /// <c>TimeSpan.Infinite</c> to mean "unbounded", though that defeats this helper's purpose).
    /// Defaults to <see cref="DefaultConnectTimeout"/> (5 s).
    /// </param>
    /// <returns>A <see cref="SocketsHttpHandler"/> whose connection phase is bounded by <paramref name="connectTimeout"/>.</returns>
    public static SocketsHttpHandler Create(TimeSpan? connectTimeout = null)
    {
        var timeout = connectTimeout ?? DefaultConnectTimeout;
        if (timeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(connectTimeout), timeout, "ConnectTimeout must be greater than zero.");
        }

        return new SocketsHttpHandler
        {
            ConnectTimeout = timeout,
            // Default pooling behavior (24 h lifetime, 2 min idle) — do not pin stale connections.
            PooledConnectionLifetime = TimeSpan.FromHours(24),
            PooledConnectionIdleTimeout = TimeSpan.FromMinutes(2),
            // Follow redirects for outbound federation GETs (actor docs, collections, media).
            AllowAutoRedirect = true,
            AutomaticDecompression = DecompressionMethods.All,
        };
    }
}
