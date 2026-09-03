using Iris.Client;
using Iris.Core;

namespace Iris.Client.Extensions;

/// <summary>
/// Configuration for a pre-configured Iris client (the <c>AddIrisClient</c> /
/// <see cref="IrisClientFactory"/> composition). Bundles the server the client talks to, the
/// proxy-fallback settings, and the retry/cache policy into one object.
/// </summary>
/// <remarks>
/// <c>ServerBaseUri</c> is the root of the Iris home server (e.g. <c>https://a.example</c>);
/// the proxy-fallback endpoint is always derived from it as
/// <c>{ServerBaseUri}/ap/v1/proxy/{target}</c> (the <see cref="Iris.Client.Pipeline.ProxyFallbackHandler"/>
/// appends the <c>ap/v1/proxy</c> path itself).
/// </remarks>
public sealed class IrisClientOptions
{
    /// <summary>
    /// Gets or sets the root base URI of the Iris home server the client authenticates against
    /// and (when <see cref="UseProxyFallback"/> is enabled) proxies through.
    /// </summary>
    public Uri ServerBaseUri { get; init; } = new("http://localhost");

    /// <summary>
    /// Gets or sets the Basic-auth credentials used both to authenticate the session (fetch the
    /// owner-only actor document + private key) and, when <see cref="UseProxyFallback"/> is
    /// enabled, to call the proxy endpoint.
    /// </summary>
    public ProxyCredentials? ProxyCredentials { get; init; }

    /// <summary>
    /// Gets or sets a value indicating whether the built client is configured for <em>local</em>,
    /// Basic-authenticated moderation (F-07: <c>MuteAsync</c>/<c>UnmuteAsync</c>). A mute is not a
    /// signed ActivityPub delivery — it is a Basic-authenticated POST to the acting actor's own
    /// instance — so the client needs the acting actor's credentials as
    /// <see cref="Iris.Client.ActivityPubClientOptions.LocalCredentials"/> to perform one. When
    /// <see langword="true"/> (the default, matching <see cref="UseProxyFallback"/>) the
    /// <see cref="ProxyCredentials"/> are also used as the local credentials (the same acting user's
    /// Basic auth). When <see langword="false"/> the client has no local credentials and
    /// <c>MuteAsync</c>/<c>UnmuteAsync</c> throw.
    /// </summary>
    public bool LocalModeration { get; init; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether the client falls back to the home server's proxy
    /// endpoint on a <c>401</c>/<c>403</c> from a remote instance. Requires
    /// <see cref="ProxyCredentials"/> to be set.
    /// </summary>
    public bool UseProxyFallback { get; init; } = true;

    /// <summary>
    /// Gets or sets whether every request is routed through the home server's proxy <em>without</em>
    /// a direct attempt (the <see cref="Iris.Client.Pipeline.ProxyFallbackHandler"/> always-proxy
    /// mode). Defaults to false. Set true when the acting actor's <em>advertised</em> host differs
    /// from the <em>dial</em> host (a browser whose WebCrypto signature cannot be validated against
    /// the advertised host — a direct attempt would always 401, so the write goes straight through
    /// the proxy, which re-signs with the actor's key). Requires <see cref="UseProxyFallback"/> and
    /// <see cref="ProxyCredentials"/>.
    /// </summary>
    public bool AlwaysProxy { get; init; }

    /// <summary>
    /// Gets or sets the base URI the client dials directly (the instance the browser can reach, e.g.
    /// the host-published port). Used with <see cref="RouteCrossInstanceReadsViaProxy"/> to detect a
    /// cross-instance read (a <c>GET</c> whose host differs from this base), which is then routed
    /// through the same-origin home proxy. When <see langword="null"/> no cross-instance read is ever
    /// detected (the mode is a no-op). Defaults to <see langword="null"/>.
    /// </summary>
    public Uri? DialBaseUri { get; init; }

    /// <summary>
    /// Gets or sets whether <c>GET</c> reads of a different host than the dial base are routed
    /// straight through the home proxy (the <see cref="Iris.Client.Pipeline.ProxyFallbackHandler"/>
    /// cross-instance-read mode). Defaults to false (reads dial the target directly; the proxy is used
    /// only on a 401/403 or, in always-proxy mode, for signed writes). Set true for a browser whose
    /// direct cross-origin read would be blocked by CORS. Requires <see cref="UseProxyFallback"/>,
    /// <see cref="ProxyCredentials"/>, and a non-null dial base.
    /// </summary>
    public bool RouteCrossInstanceReadsViaProxy { get; init; }

    /// <summary>
    /// Gets or sets a value indicating whether idempotent (GET) requests are retried on
    /// <c>429</c>/<c>5xx</c> and transient network failures.
    /// </summary>
    public bool EnableRetry { get; init; } = true;

    /// <summary>
    /// Gets or sets the maximum number of attempts for a retried idempotent request.
    /// </summary>
    public int MaxRetryAttempts { get; init; } = RetryHandler.DefaultMaxAttempts;

    /// <summary>
    /// Gets or sets the per-request <see cref="System.Net.Http.HttpClient.Timeout"/>.
    /// <see langword="null"/> means no timeout.
    /// </summary>
    public TimeSpan? HttpClientTimeout { get; init; }

    /// <summary>
    /// Gets or sets the client-side read-through caches (actor documents, collection pages,
    /// WebFinger). <see langword="null"/> disables caching (every read hits the network).
    /// </summary>
    public ClientCaches? Caches { get; init; }

    /// <summary>
    /// Creates a new <see cref="IrisClientOptions"/> with the given server base URI and the
    /// default retry/proxy/cache settings.
    /// </summary>
    /// <param name="serverBaseUri">The root base URI of the Iris home server.</param>
    public IrisClientOptions(Uri serverBaseUri)
    {
        ServerBaseUri = serverBaseUri ?? throw new ArgumentNullException(nameof(serverBaseUri));
    }

    /// <summary>
    /// Creates a new <see cref="IrisClientOptions"/> with default settings.
    /// </summary>
    public IrisClientOptions()
    {
    }
}
