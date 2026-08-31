using System;
using Iris.Core;

namespace Iris.Client;

/// <summary>
/// Options for an <see cref="ActivityPubClient"/>.
/// </summary>
public sealed class ActivityPubClientOptions
{
    /// <summary>
    /// Gets or sets the actor IRI the client signs as. Required for signed requests.
    /// </summary>
    public Iri? ActorId { get; set; }

    /// <summary>
    /// Gets or sets the HTTP client timeout. Defaults to <see cref="Timeout.InfiniteTimeSpan"/>.
    /// </summary>
    public TimeSpan? HttpClientTimeout { get; set; }

    /// <summary>
    /// Gets or sets the optional client-side caches (actor / collection-page reads). When null the
    /// client goes straight to the network for reads (no caching).
    /// </summary>
    public ClientCaches? Caches { get; set; }

    /// <summary>
    /// Gets or sets whether the <see cref="RetryHandler"/> is included in the pipeline. Defaults to
    /// true.
    /// </summary>
    public bool EnableRetry { get; set; } = true;

    /// <summary>
    /// Gets or sets the total number of attempts (including the first) the
    /// <see cref="RetryHandler"/> will make for idempotent requests. Defaults to
    /// <see cref="RetryHandler.DefaultMaxAttempts"/> (3).
    /// </summary>
    public int MaxRetryAttempts { get; set; } = RetryHandler.DefaultMaxAttempts;

    /// <summary>
    /// Gets or sets the proxy-fallback base (the home instance that hosts the
    /// <c>POST /ap/v1/proxy/{target}</c> endpoint). When set, the
    /// <see cref="ProxyFallbackHandler"/> retries requests rejected by a remote instance (401/403)
    /// by routing them through this proxy, which signs them with the actor's key. When null the
    /// handler is disabled and the client talks to remote instances directly.
    /// </summary>
    public Iri? ProxyBaseUrl { get; set; }

    /// <summary>
    /// Gets or sets the Basic-auth credentials (username:password) used to authenticate to the
    /// proxy endpoint. Required when <see cref="ProxyBaseUrl"/> is set. The proxy identifies the
    /// actor from these credentials and signs the forwarded request with the actor's key.
    /// </summary>
    public ProxyCredentials? ProxyCredentials { get; set; }

    /// <summary>
    /// Gets or sets whether every request is routed through the proxy <em>without</em> a direct
    /// attempt (the <see cref="ProxyFallbackHandler"/> "always-proxy" mode). Defaults to false (a
    /// direct attempt is made first; the proxy is used only on a 401/403). Set true when the acting
    /// actor's <em>advertised</em> host differs from the <em>dial</em> host (a browser whose WebCrypto
    /// signature cannot be validated against the advertised host — a direct attempt would always 401,
    /// so it is skipped and the write goes straight through the proxy, which re-signs).
    /// </summary>
    public bool AlwaysProxy { get; set; }

    /// <summary>
    /// Gets or sets the Basic-auth credentials (username:password) used for local, non-federated
    /// moderation requests (F-07 mute: <c>POST {actor}/mutes/{target}</c>). A mute is Iris-specific
    /// (no ActivityStreams type) and is a local decision, so it is authenticated by Basic auth to the
    /// actor's own instance rather than signed and delivered to an inbox. When null, the local-mute
    /// overloads that take no explicit credentials throw
    /// <see cref="InvalidOperationException"/> (call the overload that supplies credentials instead).
    /// </summary>
    public ProxyCredentials? LocalCredentials { get; set; }
}
