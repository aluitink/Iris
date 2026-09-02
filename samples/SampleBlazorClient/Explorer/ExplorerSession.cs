using System.Net.Http;
using Iris.Client;
using Iris.Client.Discovery;
using Iris.Client.Extensions;
using Iris.Core;
using Iris.Core.Identity;
using Iris.Samples.SampleBlazorClient.Explorer;
using Iris.WebCrypto;
using Microsoft.Extensions.Options;
using Microsoft.JSInterop;

namespace Iris.Samples.SampleBlazorClient.Explorer;

/// <summary>
/// The Blazor host's composition root (Deliverable B): registers the Iris client pipeline for the
/// WASM app. The host's <c>Program.cs</c> calls <see cref="AddIrisExplorer"/> once; it registers a
/// singleton <see cref="ExplorerSession"/> (which wraps the <see cref="IrisClientBundle"/>) and the
/// transport <see cref="Func{TResult}"/> the session uses to build its innermost handlers.
/// </summary>
public static class ExplorerHostExtensions
{
    /// <summary>
    /// Registers the explorer's client pipeline (the <see cref="ExplorerSession"/> singleton + the
    /// transport factory) into <paramref name="services"/>.
    /// </summary>
    /// <param name="services">The DI service collection. Must not be null.</param>
    /// <param name="baseUrls">
    /// An optional instance base-URL map (advertised host → browser base URL) to register for the
    /// session. When <see langword="null"/> the session uses an empty map (every logon takes an
    /// explicit base URL).
    /// </param>
    /// <returns>The service collection, for chaining.</returns>
    public static IServiceCollection AddIrisExplorer(this IServiceCollection services, InstanceBaseUrls? baseUrls = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        // The innermost transport for the WASM app is a plain HttpClientHandler (the browser's fetch
        // loop). It is registered as a factory (not a singleton handler) so the ExplorerSession can
        // hand a fresh handler to the authenticator and to each client it builds, while a single
        // shared instance is reused for the session lifetime (one connection pool).
        services.AddSingleton<Func<HttpMessageHandler>>(_ => (Func<HttpMessageHandler>)(() => new HttpClientHandler()));
        if (baseUrls is not null)
        {
            services.AddSingleton(baseUrls);
        }

        // The private-key loader is the reusable Iris.WebCrypto factory only in a JS-interop host
        // (a Blazor WebAssembly app, where IJSRuntime is registered by the WASM host) — the
        // .NET-on-WASM BCL cannot load an RSA private key, so signing is delegated to the browser's
        // WebCrypto (the factory auto-injects its JS bridge on first use). In a non-browser host
        // (the integration tests / console, which have no IJSRuntime) the key factory is null and the
        // authenticator falls back to the default BCL/BouncyCastle loader. This keeps the same
        // AddIrisExplorer call working in both contexts.
        services.AddSingleton(sp =>
        {
            var js = sp.GetService<IJSRuntime>();
            Func<string, KeyAlgorithm, Iri, CancellationToken, Task<ISigningKey>>? keyFactory = null;
            if (js is not null)
            {
                keyFactory = new WebCryptoSigningKeyFactory(js).CreateAsync;
            }

            return new ExplorerSession(
                sp.GetRequiredService<Func<HttpMessageHandler>>(),
                baseUrls ?? sp.GetService<InstanceBaseUrls>() ?? new InstanceBaseUrls(),
                keyFactory);
        });
        return services;
    }
}

/// <summary>
/// Holds the <em>currently logged-on instance + actor</em> for the Blazor explorer and can re-login
/// to a different instance (local or remote). It wraps the <see cref="IrisClientBundle"/>
/// (Basic-auth authenticator → owner-only actor document → PEM private key → pre-configured signed
/// client) and exposes the small surface a component needs: log on by WebFinger address, log out,
/// and get the signed client.
/// </summary>
/// <remarks>
/// <para>
/// The transport is a <see cref="Func{TResult}"/> (injected by the host) so the same session runs
/// against a real instance on the wire (the WASM app) or an in-process <c>TestServer</c> handler
/// (the integration tests). The session is the single place that owns a logged-in identity; instance
/// switching is "log out + log on to a new address", and the session remembers recent instances so a
/// UI can offer them for one-click switching.
/// </para>
/// <para>
/// <strong>Base URL vs. IRI host.</strong> The dial base URI (what the browser reaches) is
/// <em>separate</em> from the advertised IRI host. A local instance's advertised host is its Docker
/// service name (only routable inside the network), but the browser dials a host-published port
/// (e.g. <c>localhost:8081</c>). <see cref="LogOnAsync"/> takes the dial base URI explicitly so the
/// two never have to be the same value.
/// </para>
/// </remarks>
public sealed class ExplorerSession : IDisposable
{
    private readonly Func<HttpMessageHandler> _transportFactory;
    private readonly InstanceBaseUrls _baseUrls;
    private readonly Func<string, KeyAlgorithm, Iri, CancellationToken, Task<ISigningKey>>? _keyFactory;
    private readonly object _gate = new();

    private IrisClientBundle? _bundle;
    private ClientService? _service;
    private IActivityPubClient? _client;
    private Uri? _dialBaseUri;
    private Iri? _resolvedActorIri;
    private readonly List<RecentInstance> _recent = [];

    /// <summary>
    /// Initializes a new <see cref="ExplorerSession"/>.
    /// </summary>
    /// <param name="transportFactory">
    /// Builds the innermost transport handler (a real <c>HttpClientHandler</c> in the WASM app; an
    /// in-process <c>TestServer</c> handler in the tests). Must not be null.
    /// </param>
    /// <param name="baseUrls">
    /// The instance base-URL map (advertised host → browser base URL) used to pre-fill the dial base
    /// for known local instances (SAMPLE_PLAN §4.4). When <see langword="null"/> an empty map is used
    /// (every logon takes an explicit base URL).
    /// </param>
    /// <param name="keyFactory">
    /// An optional asynchronous private-key loader. The Blazor WebAssembly host supplies a WebCrypto
    /// loader (the .NET-on-WASM BCL cannot load an RSA private key); the integration tests + console
    /// pass <see langword="null"/> to use the default BCL/BouncyCastle loader.
    /// </param>
    public ExplorerSession(
        Func<HttpMessageHandler> transportFactory,
        InstanceBaseUrls? baseUrls = null,
        Func<string, KeyAlgorithm, Iri, CancellationToken, Task<ISigningKey>>? keyFactory = null)
    {
        _transportFactory = transportFactory ?? throw new ArgumentNullException(nameof(transportFactory));
        _baseUrls = baseUrls ?? new InstanceBaseUrls();
        _keyFactory = keyFactory;
    }

    /// <summary>
    /// Gets the instance base-URL map (advertised host → browser base URL). The UI uses this to
    /// pre-fill the dial base URL for a known local instance so the user only enters the WebFinger
    /// address and password.
    /// </summary>
    public InstanceBaseUrls BaseUrls => _baseUrls;

    /// <summary>
    /// Gets a value indicating whether the session is currently logged on to an instance.
    /// </summary>
    public bool IsLoggedIn => _service is not null && _service.Bundle.Session.IsAuthenticated;

    /// <summary>
    /// Gets the IRI of the currently logged-on actor, or <see langword="null"/> when logged out.
    /// </summary>
    public Iri? ActorIri => _service?.ActorIri;

    /// <summary>
    /// Gets the actor IRI the session resolved the logged-on address to (via WebFinger when
    /// available, else the direct IRI built from the address's host), or <see langword="null"/> when
    /// logged out. This is the authoritative advertised IRI a UI displays.
    /// </summary>
    public Iri? ResolvedActorIri => _resolvedActorIri;

    /// <summary>
    /// Gets the dial base URI of the current instance, or <see langword="null"/> when logged out.
    /// </summary>
    public Uri? DialBaseUri => _dialBaseUri;

    /// <summary>
    /// Gets the most recent logged-on instances (newest first), so a UI can offer one-click switching.
    /// </summary>
    public IReadOnlyList<RecentInstance> RecentInstances => _recent;

    /// <summary>
    /// Logs on to an instance by WebFinger address (the headline explorer capability). The address is
    /// first resolved to an authoritative actor IRI via the instance's WebFinger
    /// (<c>/.well-known/webfinger</c>); when resolution is unavailable the session falls back to the
    /// direct actor IRI built from the address's host. The client dials <paramref name="dialBaseUri"/>
    /// (what the browser reaches) while the actor's <em>advertised</em> IRI may differ for local
    /// instances. On success the session holds the actor's key and the signed client is ready; the
    /// instance is recorded in <see cref="RecentInstances"/>.
    /// </summary>
    /// <param name="address">The WebFinger address (e.g. <c>alice@iris-a</c> or <c>@alice@iris-a</c>).</param>
    /// <param name="password">The actor's Basic-auth password.</param>
    /// <param name="dialBaseUri">The base URI the client dials (e.g. <c>http://localhost:8081</c>).</param>
    /// <param name="ct">A cancellation token.</param>
    /// <returns>
    /// <see langword="true"/> when logged on; <see langword="false"/> when the instance rejected the
    /// credentials or the actor document carried no loadable private key.
    /// </returns>
    public async Task<bool> LogOnAsync(
        string address, string password, Uri dialBaseUri, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(password);
        ArgumentNullException.ThrowIfNull(dialBaseUri);
        var parsed = WebFingerAddress.Parse(address);

        // Re-login is a fresh identity: tear down the previous instance (key + client) before building
        // the new one, so the session holds exactly one active identity at a time.
        DisposeCurrent();

        // Step 1 (the headline feature): resolve the address to an authoritative actor IRI via the
        // instance's WebFinger, over the same transport the session uses to reach the instance. The
        // dial authority is the explicit <paramref name="dialBaseUri"/> (scheme + host + port the
        // browser actually reaches) — NOT the address's own host, which for a local Docker instance
        // is not browser-reachable (the address host `localhost` ≠ the host-published port `8081`).
        // WebFinger is a public GET (no signature), so it can run before the Basic-auth login.
        var discoveryTransport = _transportFactory();
        var discovery = new WebFingerDiscoveryService(new WebFingerClient(new HttpClient(discoveryTransport, disposeHandler: false)));
        Iri? resolvedIri = null;
        try
        {
            resolvedIri = await discovery.ResolveActorAsync(parsed.AcctResource, dialBaseUri, ct).ConfigureAwait(false);
        }
        catch (HttpRequestException)
        {
            // Unreachable / not-a-webfinger endpoint: fall through to the direct IRI below.
            resolvedIri = null;
        }

        // Step 2: the actor IRI to authenticate as — the WebFinger-resolved IRI when available
        // (authoritative), else the direct IRI built from the address's host.
        var actorIri = resolvedIri ?? parsed.ToActorIri(dialBaseUri);

        // Build the client (the discovery rides the injected transport so the same handler reaches
        // the instance's well-known document) and log in (Basic auth → owner-only actor document →
        // PEM private key). The actor IRI to authenticate as is the WebFinger-resolved IRI when
        // available (its host may differ from the dial base for local instances); the dial base URI is
        // still what the transport reaches.
        var service = SampleBlazorClient.CreateClientService(
            dialBaseUri, parsed.Handle, password,
            _transportFactory,
            transport => new WebFingerDiscoveryService(new WebFingerClient(new HttpClient(transport, disposeHandler: false))),
            actorIriOverride: actorIri,
            keyFactory: _keyFactory);

        var logged = await service.LoginAsync(ct).ConfigureAwait(false);
        if (!logged)
        {
            service.Dispose();
            return false;
        }

        _service = service;
        RegisterCommunityIdentity(service);

        _bundle = service.Bundle;
        _dialBaseUri = dialBaseUri;
        _resolvedActorIri = actorIri;
        RecordRecent(parsed, dialBaseUri, actorIri);
        return true;
    }

    /// <summary>
    /// Logs on to an instance via the OAuth2 (Bearer-token) logon path (Phase 15.2). The
    /// <paramref name="token"/> is the Bearer access token obtained via the OAuth2 authorization-code
    /// browser flow (the browser is redirected to <c>/ap/v1/oauth2/authorize</c>, the server 302s back
    /// with a <c>code</c>, and the app exchanges it for this token — see
    /// <see cref="OAuth2BrowserFlow"/>). The session resolves the actor IRI via WebFinger (falling
    /// back to the direct IRI), builds an OAuth2 (Bearer-token) client, and loads the private key.
    /// </summary>
    /// <param name="handle">The actor handle the token authenticates as (e.g. <c>alice</c>).</param>
    /// <param name="token">The Bearer access token (from the OAuth2 code exchange).</param>
    /// <param name="dialBaseUri">The base URI the client dials (e.g. <c>http://localhost:5000</c>).</param>
    /// <param name="ct">A cancellation token.</param>
    /// <returns>
    /// <see langword="true"/> when logged on; <see langword="false"/> when the token is invalid, the
    /// server rejected it, or the actor document carried no loadable private key.
    /// </returns>
    public async Task<bool> LogOnWithOAuth2Async(
        string handle, string token, Uri dialBaseUri, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(handle);
        ArgumentException.ThrowIfNullOrEmpty(token);
        ArgumentNullException.ThrowIfNull(dialBaseUri);

        // Re-login is a fresh identity: tear down the previous instance (key + client) before building
        // the new one, so the session holds exactly one active identity at a time.
        DisposeCurrent();

        // Step 1: resolve the actor IRI via WebFinger (the dial host), falling back to the direct IRI
        // built from the dial base. The OAuth2 path has no separate client_id registration — the token
        // identifies the actor — so the handle + dial base give the actor IRI. The dial authority is
        // the explicit dial base URI (the browser-reachable scheme + host + port).
        var discoveryTransport = _transportFactory();
        var discovery = new WebFingerDiscoveryService(new WebFingerClient(new HttpClient(discoveryTransport, disposeHandler: false)));
        Iri? resolvedIri = null;
        try
        {
            resolvedIri = await discovery.ResolveActorAsync($"acct:{handle}@{dialBaseUri.Host}", dialBaseUri, ct).ConfigureAwait(false);
        }
        catch (HttpRequestException)
        {
            resolvedIri = null;
        }

        var actorIri = resolvedIri ?? new Iri($"{dialBaseUri.ToString().TrimEnd('/')}/ap/v1/u/{handle}");

        var service = SampleBlazorClient.CreateOAuth2ClientService(
            dialBaseUri, handle, token,
            _transportFactory,
            transport => new WebFingerDiscoveryService(new WebFingerClient(new HttpClient(transport, disposeHandler: false))),
            actorIriOverride: actorIri,
            keyFactory: _keyFactory);

        var logged = await service.LoginAsync(ct).ConfigureAwait(false);
        if (!logged)
        {
            service.Dispose();
            return false;
        }

        _service = service;
        RegisterCommunityIdentity(service);

        _bundle = service.Bundle;
        _dialBaseUri = dialBaseUri;
        _resolvedActorIri = actorIri;
        RecordRecent(
            WebFingerAddress.Parse($"{handle}@{dialBaseUri.Host}"),
            dialBaseUri, actorIri);
        return true;
    }

    /// <summary>
    /// Switches to a recently logged-on instance (one-click instance switching, §4.2). Logs out the
    /// current identity and logs on to <paramref name="instance"/> by its remembered address, dial
    /// base URI, and password.
    /// </summary>
    /// <param name="instance">The recent instance to switch to. Must not be null.</param>
    /// <param name="password">The actor's Basic-auth password (re-entered, since the session does not
    /// store credentials).</param>
    /// <param name="ct">A cancellation token.</param>
    /// <returns><see langword="true"/> when switched; <see langword="false"/> on rejection.</returns>
    public async Task<bool> SwitchInstanceAsync(
        RecentInstance instance, string password, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(instance);
        return await LogOnAsync($"{instance.Handle}@{instance.Host}", password, instance.DialBaseUri, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Logs out the current instance: removes the actor's key from the session, disposes the signed
    /// client, and clears the active identity. <see cref="IsLoggedIn"/> becomes <see langword="false"/>.
    /// </summary>
    public void LogOut()
    {
        DisposeCurrent();
    }

    /// <summary>
    /// Registers the instance's seeded community signing identity (F-1911-3): the sample server seeds
    /// a community whose <c>publicKey</c> extension points at the primary actor's key (the community
    /// signs its own outbound deliveries with it). The client session only registers the logged-on
    /// actor's identity, so a delivery that signs as the community (the Raw delivery screen's
    /// "act as" override, or a community follow) would dead-letter with "No signing identity
    /// registered for actor '.../c/iris'". After a successful logon this registers the community IRI
    /// (derived from the resolved actor IRI's host) under the actor's own key IRI (<c>#key-1</c>) —
    /// the same key the actor authenticated with, so no extra key material is needed.
    /// </summary>
    /// <param name="service">The just-authenticated client service (its bundle's key store already
    /// holds the actor's key from logon).</param>
    private void RegisterCommunityIdentity(ClientService service)
    {
        var bundle = service.Bundle;
        var actorIri = service.ActorIri;
        var keyId = new Iri($"{actorIri}#key-1");
        var hostBase = actorIri.Value[..actorIri.Value.IndexOf("/ap/v1/", StringComparison.Ordinal)];
        var communityIri = new Iri($"{hostBase}/ap/v1/c/iris");

        // The community signs with the primary actor's key (its publicKey extension points at it);
        // the key must be in the session's key store for the signer to load it. It is the actor's
        // own key (already stored at logon) when the IRI matches.
        if (bundle.KeyStore.TryGetKey(keyId, out _))
        {
            bundle.KeyProvider.RegisterKey(communityIri, keyId);
        }
    }

    /// <summary>
    /// Gets the pre-configured signed client for the currently logged-on actor (created on first call
    /// and reused). Throws when not logged on.
    /// </summary>
    /// <returns>A signed, cache-enabled, proxy-fallback-enabled client.</returns>
    /// <exception cref="InvalidOperationException">When the session is not logged on.</exception>
    public IActivityPubClient GetClient()
    {
        if (_service is null)
        {
            throw new InvalidOperationException("Not logged on to an instance.");
        }

        if (_client is null)
        {
            _client = _service.GetClient();
        }

        return _client;
    }

    /// <summary>
    /// Gets the local, Basic-authenticated moderation client for the currently logged-on actor (a mute,
    /// F-07, and a relay subscription, F-06 — not AP activities). Created on first call and reused.
    /// Throws when not logged on.
    /// </summary>
    /// <returns>A local-moderation client.</returns>
    /// <exception cref="InvalidOperationException">When the session is not logged on.</exception>
    public ILocalModerationClient GetLocalModerationClient()
    {
        if (_service is null)
        {
            throw new InvalidOperationException("Not logged on to an instance.");
        }

        return _service.GetLocalModerationClient();
    }

    /// <summary>
    /// Gets the local, Basic-authenticated media client for the currently logged-on actor (uploading a
    /// note's media attachment, Phase 20.4 (a) — not an AP activity). Created on first call and reused.
    /// Throws when not logged on.
    /// </summary>
    /// <returns>A media client.</returns>
    /// <exception cref="InvalidOperationException">When the session is not logged on.</exception>
    public IMediaClient GetMediaClient()
    {
        if (_service is null)
        {
            throw new InvalidOperationException("Not logged on to an instance.");
        }

        return _service.GetMediaClient();
    }

    /// <summary>
    /// Gets the current bundle's discovery service (for resolving an <c>@user@host</c> account to an
    /// actor IRI via WebFinger), or <see langword="null"/> when not logged on.
    /// </summary>
    public IDiscoveryService? Discovery => _bundle?.Discovery;

    /// <summary>
    /// Disposes the session (the current instance's key + client and the bundle).
    /// </summary>
    public void Dispose()
    {
        DisposeCurrent();
    }

    private void DisposeCurrent()
    {
        lock (_gate)
        {
            _client?.Dispose();
            _client = null;
            _service?.Dispose();
            _service = null;
            _bundle = null;
            _dialBaseUri = null;
            _resolvedActorIri = null;
        }
    }

    private void RecordRecent(WebFingerAddress address, Uri dialBaseUri, Iri actorIri)
    {
        lock (_gate)
        {
            // Keep the list short (a UI's "recent instances" row) and de-duplicated by host+handle.
            _recent.RemoveAll(r => r.Host == address.Host && r.Handle == address.Handle);
            _recent.Insert(0, new RecentInstance(address.Handle, address.Host, address.Scheme, dialBaseUri, actorIri));
            if (_recent.Count > 5)
            {
                _recent.RemoveRange(5, _recent.Count - 5);
            }
        }
    }
}

/// <summary>
/// A recently logged-on instance, remembered by the <see cref="ExplorerSession"/> so a UI can offer
/// one-click switching between instances.
/// </summary>
/// <param name="Handle">The actor's handle.</param>
/// <param name="Host">The instance's advertised host.</param>
/// <param name="Scheme">The dial scheme.</param>
/// <param name="DialBaseUri">The base URI the client dialed (host-published port for local
/// instances).</param>
/// <param name="ActorIri">The logged-on actor's advertised IRI.</param>
public sealed record RecentInstance(
    string Handle, string Host, string Scheme, Uri DialBaseUri, Iri ActorIri);
