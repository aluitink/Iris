using System.Net.Http;
using Iris.Client;
using Iris.Client.Extensions;
using Iris.Core;
using Iris.Samples.SampleBlazorClient.Explorer;
using Microsoft.Extensions.Options;

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
    /// <returns>The service collection, for chaining.</returns>
    public static IServiceCollection AddIrisExplorer(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        // The innermost transport for the WASM app is a plain HttpClientHandler (the browser's fetch
        // loop). It is registered as a factory (not a singleton handler) so the ExplorerSession can
        // hand a fresh handler to the authenticator and to each client it builds, while a single
        // shared instance is reused for the session lifetime (one connection pool).
        services.AddSingleton<Func<HttpMessageHandler>>(_ => (Func<HttpMessageHandler>)(() => new HttpClientHandler()));
        services.AddSingleton<ExplorerSession>();
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
    private readonly object _gate = new();

    private IrisClientBundle? _bundle;
    private ClientService? _service;
    private IActivityPubClient? _client;
    private Uri? _dialBaseUri;
    private readonly List<RecentInstance> _recent = [];

    /// <summary>
    /// Initializes a new <see cref="ExplorerSession"/>.
    /// </summary>
    /// <param name="transportFactory">
    /// Builds the innermost transport handler (a real <c>HttpClientHandler</c> in the WASM app; an
    /// in-process <c>TestServer</c> handler in the tests). Must not be null.
    /// </param>
    public ExplorerSession(Func<HttpMessageHandler> transportFactory)
    {
        _transportFactory = transportFactory ?? throw new ArgumentNullException(nameof(transportFactory));
    }

    /// <summary>
    /// Gets a value indicating whether the session is currently logged on to an instance.
    /// </summary>
    public bool IsLoggedIn => _service is not null && _service.Bundle.Session.IsAuthenticated;

    /// <summary>
    /// Gets the IRI of the currently logged-on actor, or <see langword="null"/> when logged out.
    /// </summary>
    public Iri? ActorIri => _service?.ActorIri;

    /// <summary>
    /// Gets the dial base URI of the current instance, or <see langword="null"/> when logged out.
    /// </summary>
    public Uri? DialBaseUri => _dialBaseUri;

    /// <summary>
    /// Gets the most recent logged-on instances (newest first), so a UI can offer one-click switching.
    /// </summary>
    public IReadOnlyList<RecentInstance> RecentInstances => _recent;

    /// <summary>
    /// Logs on to an instance by WebFinger address. The client dials <paramref name="dialBaseUri"/>
    /// (what the browser reaches) while the actor's <em>advertised</em> IRI is built from the address's
    /// host (which may differ for local instances). On success the session holds the actor's key and
    /// the signed client is ready; the instance is recorded in <see cref="RecentInstances"/>.
    /// </summary>
    /// <param name="address">The WebFinger address (e.g. <c>alice@iris-a</c>).</param>
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

        var actorIri = parsed.ToActorIri(dialBaseUri);
        var service = SampleBlazorClient.CreateClientService(
            dialBaseUri, parsed.Handle, password, _transportFactory);

        var logged = await service.LoginAsync(ct).ConfigureAwait(false);
        if (!logged)
        {
            service.Dispose();
            return false;
        }

        _service = service;
        _bundle = service.Bundle;
        _dialBaseUri = dialBaseUri;
        RecordRecent(parsed, dialBaseUri, actorIri);
        return true;
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
