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
/// <c>{ServerBaseUri}/ap/v1/proxy/{target}</c> (the <see cref="Iris.Client.ProxyFallbackHandler"/>
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
    /// Gets or sets a value indicating whether the client falls back to the home server's proxy
    /// endpoint on a <c>401</c>/<c>403</c> from a remote instance. Requires
    /// <see cref="ProxyCredentials"/> to be set.
    /// </summary>
    public bool UseProxyFallback { get; init; } = true;

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
