using System.Net.Http;
using Iris.Client;
using Iris.Client.Auth;
using Iris.Client.Extensions;
using Iris.Core;

namespace Iris.Samples.SampleBlazorClient;

/// <summary>
/// The runnable sample client: a self-contained composition root that wires the Iris client
/// pipeline (session + pre-configured signed client) against a running <c>SampleServer</c>. It is
/// the Blazor client's <c>Program.cs</c> equivalent — a Blazor WASM host would register
/// <see cref="ClientService"/> as a singleton from this composition and render it in a component.
/// </summary>
/// <remarks>
/// The pipeline is exactly the <see cref="Iris.Client.Extensions.IrisClientBundle"/> the
/// <c>Iris.Client.Extensions</c> package composes: a <see cref="BasicAuthClientAuthenticator"/>
/// (Basic auth → owner-only actor document + PEM private key), an in-memory session key store, and
/// a pre-configured client (retry → JSON-LD → signing → proxy fallback → transport). The transport
/// is injected (a <see cref="Func{TResult}"/>), so the same composition runs against a real
/// <c>SampleServer</c> on the wire (the console entry point) or an in-process <c>TestServer</c>
/// (the integration tests).
/// </remarks>
public static partial class SampleBlazorClient
{
    /// <summary>
    /// The default <c>SampleServer</c> base URI the client talks to when run standalone.
    /// </summary>
    public static readonly Uri DefaultServerBaseUri = new("http://localhost:5000");

    /// <summary>
    /// The default actor handle the client authenticates as (must exist on the target server).
    /// </summary>
    public const string DefaultHandle = "alice";

    /// <summary>
    /// The <c>SampleServer</c> seeded actor's Basic-auth password (the value of
    /// <c>SampleServer.Password</c>). Repeated here so the client sample stays free of a project
    /// reference to the server sample.
    /// </summary>
    public const string SamplePassword = "iris-sample";

    /// <summary>
    /// Creates the client composition (bundle + service) for a given server and actor.
    /// </summary>
    /// <param name="serverBaseUri">The root base URI of the home server (e.g.
    /// <c>http://localhost:5000</c>).</param>
    /// <param name="handle">The actor handle to authenticate as (e.g. <c>alice</c>).</param>
    /// <param name="password">The actor's Basic-auth password.</param>
    /// <param name="transportFactory">
    /// Builds the innermost transport handler for both the authenticator and the client. When
    /// <see langword="null"/> a real <see cref="HttpClientHandler"/> is used (the wire).
    /// </param>
    /// <param name="discoveryFactory">
    /// Optionally builds the bundle's WebFinger <see cref="IDiscoveryService"/> over the given
    /// (already-created) transport handler, so discovery rides the same handler as the authenticator.
    /// When <see langword="null"/> the bundle builds its own default (a plain <c>https</c> client).
    /// </param>
    /// <param name="actorIriOverride">
    /// The authoritative actor IRI to authenticate as (e.g. the WebFinger-resolved IRI, whose host may
    /// differ from <paramref name="serverBaseUri"/> for local instances). When <see langword="null"/>
    /// the IRI is derived as <c>{serverBaseUri}/ap/v1/u/{handle}</c>.
    /// </param>
    /// <param name="keyFactory">
    /// An optional asynchronous private-key loader (PEM + algorithm + key id → loaded
    /// <see cref="Iris.Core.Identity.ISigningKey"/>). A Blazor WebAssembly host supplies a WebCrypto
    /// loader because the .NET-on-WASM BCL cannot load an RSA private key. When
    /// <see langword="null"/> the default BCL/BouncyCastle loader is used (the console + test paths).
    /// </param>
    /// <returns>The composed service (owns the bundle and any clients it builds).</returns>
    public static ClientService CreateClientService(
        Uri serverBaseUri,
        string handle,
        string password,
        Func<HttpMessageHandler>? transportFactory = null,
        Func<HttpMessageHandler, IDiscoveryService>? discoveryFactory = null,
        Iri? actorIriOverride = null,
        Func<string, KeyAlgorithm, Iri, CancellationToken, Task<ISigningKey>>? keyFactory = null)
    {
        ArgumentNullException.ThrowIfNull(serverBaseUri);
        ArgumentException.ThrowIfNullOrEmpty(handle);
        ArgumentException.ThrowIfNullOrEmpty(password);

        var baseString = serverBaseUri.ToString().TrimEnd('/');
        var actorIri = actorIriOverride ?? new Iri($"{baseString}/ap/v1/u/{handle}");

        // The transport is shared by the authenticator (which fetches the owner-only actor document
        // to log in) and every client the service builds (which sign + fetch through the pipeline).
        // A single handler instance is reused for the process lifetime, so both paths share one
        // connection pool.
        var transport = transportFactory?.Invoke() ?? new HttpClientHandler();
        var authenticatorHttp = new HttpClient(transport, disposeHandler: false)
        {
            Timeout = System.Threading.Timeout.InfiniteTimeSpan,
        };

        var authenticator = new BasicAuthClientAuthenticator(authenticatorHttp, actorIri, handle, password, keyFactory);

        // Always-proxy (the S1 browser write path): when the actor's *advertised* host (the host in
        // its resolved IRI) differs from the *dial* host (what the browser reaches), the browser's
        // WebCrypto signature cannot be validated against the advertised host, so a direct attempt
        // would always 401. Route such writes straight through the home proxy (which re-signs with
        // the actor's key) instead of wasting the guaranteed-401 direct attempt.
        var alwaysProxy = !string.Equals(
            actorIri.Uri?.Host, serverBaseUri.Host, StringComparison.OrdinalIgnoreCase);

        var options = new IrisClientOptions(serverBaseUri)
        {
            // The proxy-fallback credentials are the acting user's Basic-auth credentials (the proxy
            // identifies the actor from them and signs the forwarded request with the actor's key).
            ProxyCredentials = new ProxyCredentials(handle, password),
            UseProxyFallback = true,
            AlwaysProxy = alwaysProxy,
            // Cross-instance reads (Phase 22.4 / US-8): a browser cannot reach a cross-origin remote
            // instance directly (CORS), so a GET of another host is routed straight through the
            // same-origin home proxy (which relays it) instead of CORS-failing. The dial base is the
            // instance the browser reaches directly; reads of that host are also proxied (a harmless
            // same-origin relay) so every AP read the client makes goes through the home proxy.
            DialBaseUri = serverBaseUri,
            RouteCrossInstanceReadsViaProxy = true,
        };

        var builder = IrisClientBuilder.Create(options).WithAuthenticator(authenticator);

        // An optional discovery service (WebFinger) rides the same transport as the authenticator so
        // the same in-process handler / wire reaches the instance's /.well-known/webfinger. When not
        // supplied the bundle builds its own default (a plain https HttpClient).
        if (discoveryFactory is not null)
        {
            builder.WithDiscovery(discoveryFactory(transport));
        }

        var bundle = builder.Build();

        return new ClientService(bundle, actorIri, transportFactory ?? (() => new HttpClientHandler()));
    }

    /// <summary>
    /// Creates the client composition for the OAuth2 (Bearer-token) logon path (Phase 15.2). Unlike
    /// <see cref="CreateClientService"/> (Basic auth), this takes a Bearer <paramref name="token"/>
    /// (obtained via the OAuth2 authorization-code browser flow — see
    /// <see cref="Explorer.OAuth2BrowserFlow"/>) and wires an
    /// <see cref="OAuth2ClientAuthenticator"/> (which fetches the owner-only actor document with
    /// <c>Authorization: Bearer</c> and loads the private key).
    /// </summary>
    /// <param name="serverBaseUri">The root base URI of the home server (e.g.
    /// <c>http://localhost:5000</c>).</param>
    /// <param name="handle">The actor handle authenticated by the Bearer token (e.g. <c>alice</c>).</param>
    /// <param name="token">
    /// The Bearer access token (from the OAuth2 code exchange). Not null or empty.
    /// </param>
    /// <param name="transportFactory">
    /// Builds the innermost transport handler. When <see langword="null"/> a real
    /// <see cref="HttpClientHandler"/> is used (the wire).
    /// </param>
    /// <param name="discoveryFactory">
    /// Optionally builds the bundle's WebFinger <see cref="IDiscoveryService"/> over the given
    /// (already-created) transport handler. When <see langword="null"/> the bundle builds its own
    /// default.
    /// </param>
    /// <param name="actorIriOverride">
    /// The authoritative actor IRI to authenticate as (e.g. the WebFinger-resolved IRI, whose host may
    /// differ from <paramref name="serverBaseUri"/> for local instances). When <see langword="null"/>
    /// the IRI is derived as <c>{serverBaseUri}/ap/v1/u/{handle}</c>.
    /// </param>
    /// <param name="keyFactory">
    /// An optional asynchronous private-key loader (a Blazor WebAssembly host supplies a WebCrypto
    /// loader because the .NET-on-WASM BCL cannot load an RSA private key). When
    /// <see langword="null"/> the default BCL/BouncyCastle loader is used.
    /// </param>
    /// <returns>The composed service (owns the bundle and any clients it builds).</returns>
    public static ClientService CreateOAuth2ClientService(
        Uri serverBaseUri,
        string handle,
        string token,
        Func<HttpMessageHandler>? transportFactory = null,
        Func<HttpMessageHandler, IDiscoveryService>? discoveryFactory = null,
        Iri? actorIriOverride = null,
        Func<string, KeyAlgorithm, Iri, CancellationToken, Task<ISigningKey>>? keyFactory = null)
    {
        ArgumentNullException.ThrowIfNull(serverBaseUri);
        ArgumentException.ThrowIfNullOrEmpty(handle);
        ArgumentException.ThrowIfNullOrEmpty(token);

        var baseString = serverBaseUri.ToString().TrimEnd('/');
        var actorIri = actorIriOverride ?? new Iri($"{baseString}/ap/v1/u/{handle}");

        var transport = transportFactory?.Invoke() ?? new HttpClientHandler();
        var authenticatorHttp = new HttpClient(transport, disposeHandler: false)
        {
            Timeout = System.Threading.Timeout.InfiniteTimeSpan,
        };

        // The OAuth2 authenticator fetches the owner-only actor document with the Bearer token and
        // loads the private key. The token is fixed for the session lifetime (the v1 model has no
        // refresh rotation in the sample), so the provider returns it verbatim.
        var authenticator = new OAuth2ClientAuthenticator(
            authenticatorHttp,
            _ => new ValueTask<string?>(token),
            keyFactory);

        var options = new IrisClientOptions(serverBaseUri)
        {
            // No Basic-auth proxy-fallback credentials in the OAuth2 path (the token authenticates
            // directly); proxy fallback is disabled (it requires Basic-auth credentials).
            UseProxyFallback = false,
            LocalModeration = false,
        };

        var builder = IrisClientBuilder.Create(options).WithAuthenticator(authenticator);

        if (discoveryFactory is not null)
        {
            builder.WithDiscovery(discoveryFactory(transport));
        }

        var bundle = builder.Build();

        return new ClientService(bundle, actorIri, transportFactory ?? (() => new HttpClientHandler()));
    }
}

/// <summary>
/// The client pipeline service a Blazor component would consume: it holds the
/// <see cref="IrisClientBundle"/> (identity + pre-configured client factory), performs the login
/// (Basic auth → PEM private key, held in memory for the session), and builds signed clients.
/// </summary>
public sealed class ClientService : IDisposable
{
    private readonly IrisClientBundle _bundle;
    private readonly Func<HttpMessageHandler> _transportFactory;
    private IActivityPubClient? _client;
    private ILocalModerationClient? _localModerationClient;
    private IMediaClient? _mediaClient;
    private readonly Iri _actorIri;

    /// <summary>
    /// Initializes a new <see cref="ClientService"/>.
    /// </summary>
    /// <param name="bundle">The composed client bundle. Must not be null.</param>
    /// <param name="actorIri">The actor the service authenticates as.</param>
    /// <param name="transportFactory">
    /// Builds the innermost transport for clients this service creates. Must not be null.
    /// </param>
    public ClientService(
        IrisClientBundle bundle,
        Iri actorIri,
        Func<HttpMessageHandler> transportFactory)
    {
        _bundle = bundle ?? throw new ArgumentNullException(nameof(bundle));
        _actorIri = actorIri;
        _transportFactory = transportFactory ?? throw new ArgumentNullException(nameof(transportFactory));
    }

    /// <summary>
    /// Gets the underlying bundle (session + client factory).
    /// </summary>
    public IrisClientBundle Bundle => _bundle;

    /// <summary>
    /// Gets the actor IRI this service authenticates as.
    /// </summary>
    public Iri ActorIri => _actorIri;

    /// <summary>
    /// Authenticates the configured actor (fetching the owner-only actor document + private key) and
    /// stores the key in the session.
    /// </summary>
    /// <param name="ct">A cancellation token.</param>
    /// <returns>
    /// <see langword="true"/> when authenticated; <see langword="false"/> when the server rejected
    /// the credentials or the document carried no loadable private key.
    /// </returns>
    public async Task<bool> LoginAsync(CancellationToken ct = default)
    {
        var actor = await _bundle.Session.LoginAsync(_actorIri, ct).ConfigureAwait(false);
        return actor is not null;
    }

    /// <summary>
    /// Gets the pre-configured signed client for the configured actor (created on first call and
    /// reused thereafter; the transport is injected).
    /// </summary>
    /// <returns>A signed, cache-enabled, proxy-fallback-enabled client.</returns>
    public IActivityPubClient GetClient()
    {
        if (_client is not null)
        {
            return _client;
        }

        var client = _bundle.CreateClient(_actorIri, _transportFactory());
        _client = client;
        return client;
    }

    /// <summary>
    /// Gets the local, Basic-authenticated moderation client for the configured actor (a mute, F-07,
    /// and a relay subscription, F-06 — not AP activities). Created on first call and reused thereafter.
    /// </summary>
    /// <returns>A local-moderation client.</returns>
    public ILocalModerationClient GetLocalModerationClient()
    {
        if (_localModerationClient is not null)
        {
            return _localModerationClient;
        }

        _localModerationClient = _bundle.CreateLocalModerationClient(_actorIri, _transportFactory());
        return _localModerationClient;
    }

    /// <summary>
    /// Gets the local, Basic-authenticated media client for the configured actor (uploading a note's
    /// media attachment, Phase 20.4 (a) — not an AP activity). Created on first call and reused thereafter.
    /// </summary>
    /// <returns>A media client.</returns>
    public IMediaClient GetMediaClient()
    {
        if (_mediaClient is not null)
        {
            return _mediaClient;
        }

        _mediaClient = _bundle.CreateMediaClient(_actorIri, _transportFactory());
        return _mediaClient;
    }

    /// <summary>
    /// Disposes the service (the client it built and the bundle).
    /// </summary>
    public void Dispose()
    {
        _client?.Dispose();
        _bundle.Dispose();
    }
}
