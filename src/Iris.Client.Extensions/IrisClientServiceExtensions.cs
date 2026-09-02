using System.Net.Http;
using Iris.Client;
using Iris.Core;

namespace Iris.Client.Extensions;

/// <summary>
/// Builds the Iris client composition (session + key store + key provider + pre-configured
/// client factory) from a single <see cref="IrisClientOptions"/>. This is the
/// "pre-configured pipeline" the <c>AddIrisClient</c> surface promises, expressed as a plain
/// BCL composition root so the package stays WASM-safe and free of a
/// <c>Microsoft.Extensions.DependencyInjection</c> dependency.
/// </summary>
/// <remarks>
/// A Blazor (WASM) host or a Microsoft-DI host creates one <see cref="IrisClientBundle"/> for the
/// application (typically a singleton), logs in via its <see cref="IrisSession"/>, and builds
/// clients with <see cref="IrisClientBundle.CreateClient(Iri, HttpMessageHandler?)"/>. The key is
/// held in memory for the session lifetime (Resolved Decision #5).
/// </remarks>
public sealed class IrisClientBuilder
{
    private readonly IrisClientOptions _options;
    private IKeyStore? _keyStore;
    private IKeyProvider? _keyProvider;
    private IClientAuthenticator? _authenticator;
    private IDiscoveryService? _discovery;

    private IrisClientBuilder(IrisClientOptions options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
    }

    /// <summary>
    /// Starts building an <see cref="IrisClientBundle"/> from the given options.
    /// </summary>
    /// <param name="options">The client configuration. Must not be null.</param>
    /// <returns>A builder to which the session's authenticator can be added.</returns>
    public static IrisClientBuilder Create(IrisClientOptions options)
        => new(options);

    /// <summary>
    /// Sets the <see cref="IClientAuthenticator"/> used to fetch the actor document + private key
    /// (Basic auth). Required before <see cref="Build"/>.
    /// </summary>
    /// <param name="authenticator">The authenticator. Must not be null.</param>
    /// <returns>This builder, for chaining.</returns>
    public IrisClientBuilder WithAuthenticator(IClientAuthenticator authenticator)
    {
        _authenticator = authenticator ?? throw new ArgumentNullException(nameof(authenticator));
        return this;
    }

    /// <summary>
    /// Overrides the <see cref="IKeyStore"/> the session uses to hold the key. Defaults to a new
    /// <see cref="InMemoryKeyStore"/>.
    /// </summary>
    /// <param name="keyStore">The key store. Must not be null.</param>
    /// <returns>This builder, for chaining.</returns>
    public IrisClientBuilder WithKeyStore(IKeyStore keyStore)
    {
        _keyStore = keyStore ?? throw new ArgumentNullException(nameof(keyStore));
        return this;
    }

    /// <summary>
    /// Overrides the <see cref="IKeyProvider"/> that maps an actor to its signing identity.
    /// Defaults to a new <see cref="InMemoryKeyProvider"/> over the key store.
    /// </summary>
    /// <param name="keyProvider">The key provider. Must not be null.</param>
    /// <returns>This builder, for chaining.</returns>
    public IrisClientBuilder WithKeyProvider(IKeyProvider keyProvider)
    {
        _keyProvider = keyProvider ?? throw new ArgumentNullException(nameof(keyProvider));
        return this;
    }

    /// <summary>
    /// Overrides the <see cref="IDiscoveryService"/> the bundle exposes for resolving an account
    /// handle (<c>@user@host</c> / <c>acct:</c> URI) to an actor IRI. Defaults to a WebFinger-backed
    /// <see cref="WebFingerDiscoveryService"/> over a plain (unsigned) <c>HttpClient</c>.
    /// </summary>
    /// <param name="discovery">The discovery service. Must not be null.</param>
    /// <returns>This builder, for chaining.</returns>
    public IrisClientBuilder WithDiscovery(IDiscoveryService discovery)
    {
        _discovery = discovery ?? throw new ArgumentNullException(nameof(discovery));
        return this;
    }

    /// <summary>
    /// Builds the <see cref="IrisClientBundle"/>.
    /// </summary>
    /// <returns>The composed session + client factory.</returns>
    /// <exception cref="InvalidOperationException">
    /// When no <see cref="IClientAuthenticator"/> was provided.
    /// </exception>
    public IrisClientBundle Build()
    {
        if (_authenticator is null)
        {
            throw new InvalidOperationException("An IClientAuthenticator is required. Call WithAuthenticator first.");
        }

        var keyStore = _keyStore ?? new InMemoryKeyStore();
        var keyProvider = _keyProvider ?? new InMemoryKeyProvider(keyStore);
        var session = new IrisSession(_authenticator, keyStore, keyProvider);
        var clientFactory = new IrisClientFactory(
            new ActivityPubClientFactory(keyStore, keyProvider),
            keyProvider,
            new SessionKeyStoreProvider(keyStore),
            _options);

        // The discovery service resolves an account handle (@user@host / acct: URI) to an actor IRI
        // via WebFinger (the client's first step in "follow/fetch @user@host"). It is exposed on the
        // bundle so a user has a public path from a handle to an IRI (the handle→IRI step that was
        // previously a dead-end; see PHASE_11_USER_JOURNEYS.md J-21). When no discovery service was
        // supplied via WithDiscovery, a WebFinger-backed one is built over a plain (unsigned)
        // HttpClient — WebFinger is a public GET that needs no signature — reusing the bundle's
        // WebFinger cache (when configured) so resolutions are shared with the read paths.
        var discovery = _discovery ?? new WebFingerDiscoveryService(
            new WebFingerClient(new HttpClient(), _options.Caches?.WebFinger));

        return new IrisClientBundle(session, clientFactory, keyStore, keyProvider, _options, discovery);
    }
}

/// <summary>
/// The composed Iris client: an <see cref="IrisSession"/> (identity + in-memory key), an
/// <see cref="IrisClientFactory"/> (pre-configured pipeline), and the supporting seams. Create one
/// per application (a singleton in a Blazor host); log in via the session, then build clients.
/// </summary>
public sealed class IrisClientBundle : IDisposable
{
    /// <summary>
    /// Creates a new <see cref="IrisClientBundle"/>.
    /// </summary>
    internal IrisClientBundle(
        IrisSession session,
        IrisClientFactory clientFactory,
        IKeyStore keyStore,
        IKeyProvider keyProvider,
        IrisClientOptions options,
        IDiscoveryService discovery)
    {
        Session = session ?? throw new ArgumentNullException(nameof(session));
        ClientFactory = clientFactory ?? throw new ArgumentNullException(nameof(clientFactory));
        KeyStore = keyStore ?? throw new ArgumentNullException(nameof(keyStore));
        KeyProvider = keyProvider ?? throw new ArgumentNullException(nameof(keyProvider));
        Options = options ?? throw new ArgumentNullException(nameof(options));
        Discovery = discovery ?? throw new ArgumentNullException(nameof(discovery));
    }

    /// <summary>
    /// Gets the session (identity selection + in-memory key persistence).
    /// </summary>
    public IrisSession Session { get; }

    /// <summary>
    /// Gets the pre-configured client factory.
    /// </summary>
    public IrisClientFactory ClientFactory { get; }

    /// <summary>
    /// Gets the session's in-memory key store.
    /// </summary>
    public IKeyStore KeyStore { get; }

    /// <summary>
    /// Gets the key provider that resolves the actor's signing identity.
    /// </summary>
    public IKeyProvider KeyProvider { get; }

    /// <summary>
    /// Gets the client options the bundle was built with.
    /// </summary>
    public IrisClientOptions Options { get; }

    /// <summary>
    /// Gets the discovery service that resolves an account handle (e.g. <c>@user@example.com</c> or an
    /// <c>acct:</c> URI) to the actor's IRI via WebFinger. This is the client's public path from a
    /// handle to an IRI — the first step of "follow/fetch @user@host" (see
    /// <see cref="IActivityPubClient"/>). Resolutions are cached in the bundle's WebFinger cache
    /// (when <see cref="IrisClientOptions.Caches"/> configures one).
    /// </summary>
    public IDiscoveryService Discovery { get; }

    /// <summary>
    /// Resolves an account handle to an actor IRI (a convenience for
    /// <see cref="Discovery"/>/<see cref="IDiscoveryService.ResolveActorAsync(string, string, CancellationToken)"/>),
    /// dialing the instance over <paramref name="dialScheme"/>.
    /// </summary>
    /// <param name="account">The account handle (e.g. <c>@user@example.com</c>) or an <c>acct:</c> URI.</param>
    /// <param name="dialScheme">
    /// The scheme used to dial the instance's WebFinger endpoint (<c>https</c> by default; <c>http</c>
    /// for a local/self-signed instance).
    /// </param>
    /// <param name="ct">A cancellation token.</param>
    /// <returns>The actor IRI, or <see langword="null"/> if the account could not be resolved.</returns>
    public Task<Iri?> ResolveActorAsync(string account, string dialScheme = "https", CancellationToken ct = default)
        => Discovery.ResolveActorAsync(account, dialScheme, ct);

    /// <summary>
    /// Builds a pre-configured <see cref="IActivityPubClient"/> for the given actor.
    /// </summary>
    /// <param name="actorId">The IRI of the actor the client signs as.</param>
    /// <param name="transport">
    /// The innermost transport handler. <see langword="null"/> uses the platform default.
    /// </param>
    /// <returns>A signed, cache-enabled, (optionally) proxy-fallback-enabled client.</returns>
    public IActivityPubClient CreateClient(Iri actorId, HttpMessageHandler? transport = null)
        => ClientFactory.Create(actorId, transport);

    /// <summary>
    /// Builds a pre-configured <see cref="ILocalModerationClient"/> for the given actor (the local,
    /// Basic-authenticated moderation surface: a mute, F-07, and a relay subscription, F-06).
    /// </summary>
    /// <param name="actorId">The IRI of the (local) actor the moderation decisions act for.</param>
    /// <param name="transport">
    /// The innermost transport handler. <see langword="null"/> uses the platform default.
    /// </param>
    /// <returns>A local-moderation client (credentials per <see cref="IrisClientOptions.LocalModeration"/>).</returns>
    public ILocalModerationClient CreateLocalModerationClient(Iri actorId, HttpMessageHandler? transport = null)
        => ClientFactory.CreateLocalModerationClient(actorId, transport);

    /// <summary>
    /// Builds a pre-configured <see cref="IMediaClient"/> for the given actor (the local,
    /// Basic-authenticated surface for uploading a note's media attachment, Phase 20.4 (a)).
    /// </summary>
    /// <param name="actorId">The IRI of the (local) actor the media upload acts for.</param>
    /// <param name="transport">
    /// The innermost transport handler. <see langword="null"/> uses the platform default.
    /// </param>
    /// <returns>A media client (credentials per <see cref="IrisClientOptions.LocalModeration"/>).</returns>
    public IMediaClient CreateMediaClient(Iri actorId, HttpMessageHandler? transport = null)
        => ClientFactory.CreateMediaClient(actorId, transport);

    /// <summary>
    /// Disposes the bundle: logs out the session (removing the key) and disposes the key store.
    /// </summary>
    public void Dispose()
    {
        Session.Dispose();
        (KeyStore as IDisposable)?.Dispose();
    }
}
