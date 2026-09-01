using System.Net.Http;
using Iris.Client;
using Iris.Core;

namespace Iris.Client.Extensions;

/// <summary>
/// Builds a fully pre-configured <see cref="IActivityPubClient"/> for a given actor: the full
/// signing pipeline (retry → JSON-LD → signing → transport), client-side caching, and the
/// proxy-fallback stage when configured.
/// </summary>
/// <remarks>
/// This is the "pre-configured pipeline" the <c>AddIrisClient</c> surface promises. It composes
/// the <see cref="IActivityPubClientFactory"/> (which owns the handler pipeline) with the
/// session's key provider/store and the <see cref="IrisClientOptions"/>. The returned client
/// owns its <see cref="HttpClient"/> and disposes it on <see cref="IDisposable.Dispose"/>.
/// </remarks>
public sealed class IrisClientFactory
{
    private readonly IActivityPubClientFactory _clientFactory;
    private readonly IKeyProvider _keyProvider;
    private readonly IKeyStoreProvider _keyStoreProvider;
    private readonly IrisClientOptions _options;

    /// <summary>
    /// Creates a new <see cref="IrisClientFactory"/>.
    /// </summary>
    /// <param name="clientFactory">
    /// The underlying factory that assembles the signing pipeline. Must not be null.
    /// </param>
    /// <param name="keyProvider">
    /// The key provider that resolves the actor's signing identity. Must not be null.
    /// </param>
    /// <param name="keyStoreProvider">
    /// Provides the key store the pipeline's signer borrows keys from. Must not be null.
    /// </param>
    /// <param name="options">The client configuration. Must not be null.</param>
    public IrisClientFactory(
        IActivityPubClientFactory clientFactory,
        IKeyProvider keyProvider,
        IKeyStoreProvider keyStoreProvider,
        IrisClientOptions options)
    {
        _clientFactory = clientFactory ?? throw new ArgumentNullException(nameof(clientFactory));
        _keyProvider = keyProvider ?? throw new ArgumentNullException(nameof(keyProvider));
        _keyStoreProvider = keyStoreProvider ?? throw new ArgumentNullException(nameof(keyStoreProvider));
        _options = options ?? throw new ArgumentNullException(nameof(options));
    }

    /// <summary>
    /// Gets the <see cref="IKeyStore"/> the built clients sign with.
    /// </summary>
    public IKeyStore KeyStore => _keyStoreProvider.KeyStore;

    /// <summary>
    /// Builds a pre-configured <see cref="IActivityPubClient"/> for the given actor.
    /// </summary>
    /// <param name="actorId">The IRI of the actor the client signs as.</param>
    /// <param name="transport">
    /// The innermost transport handler (e.g. an <c>HttpClientHandler</c>). Not owned by the
    /// returned client. May be <see langword="null"/> to use the platform default.
    /// </param>
    /// <returns>A signed, cache-enabled, (optionally) proxy-fallback-enabled client.</returns>
    public IActivityPubClient Create(Iri actorId, HttpMessageHandler? transport = null)
    {
        if (_options.UseProxyFallback && _options.ProxyCredentials is null)
        {
            throw new InvalidOperationException(
                "Proxy fallback is enabled (UseProxyFallback) but no ProxyCredentials are configured.");
        }

        var options = new ActivityPubClientOptions
        {
            ActorId = actorId,
            EnableRetry = _options.EnableRetry,
            MaxRetryAttempts = _options.MaxRetryAttempts,
            HttpClientTimeout = _options.HttpClientTimeout,
            Caches = _options.Caches,
            ProxyBaseUrl = _options.UseProxyFallback ? new Iri(_options.ServerBaseUri) : null,
            ProxyCredentials = _options.UseProxyFallback ? _options.ProxyCredentials : null,
            AlwaysProxy = _options.AlwaysProxy,
            LocalCredentials = _options.LocalModeration ? _options.ProxyCredentials : null,
        };

        // The signer resolves identities through the key provider, which reads the session's key
        // store. The transport (when provided) is the innermost handler; the pipeline
        // (ProxyFallback → Retry → JsonLd → Signing) wraps it.
        return _clientFactory.Create(options, transport ?? new HttpClientHandler());
    }

    /// <summary>
    /// Builds a pre-configured <see cref="ILocalModerationClient"/> for the given actor: the local,
    /// Basic-authenticated moderation surface (a mute, F-07, and a relay subscription, F-06).
    /// </summary>
    /// <param name="actorId">The IRI of the (local) actor the moderation decisions act for.</param>
    /// <param name="transport">
    /// The innermost transport handler (e.g. an <c>HttpClientHandler</c>). Not owned by the returned
    /// client. May be <see langword="null"/> to use the platform default.
    /// </param>
    /// <returns>
    /// A local-moderation client. When <see cref="IrisClientOptions.LocalModeration"/> is enabled (and
    /// <see cref="IrisClientOptions.ProxyCredentials"/> is set) its no-credential overloads use those
    /// credentials; otherwise only the explicit-credential overloads work.
    /// </returns>
    public ILocalModerationClient CreateLocalModerationClient(Iri actorId, HttpMessageHandler? transport = null)
    {
        var options = new ActivityPubClientOptions
        {
            ActorId = actorId,
            LocalCredentials = _options.LocalModeration ? _options.ProxyCredentials : null,
        };

        return _clientFactory.CreateLocalModerationClient(options, transport ?? new HttpClientHandler());
    }
}
