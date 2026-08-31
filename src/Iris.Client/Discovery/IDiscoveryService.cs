using Iris.Core;

namespace Iris.Client.Discovery;

/// <summary>
/// Resolves accounts to actor IRIs (discovery).
/// </summary>
/// <remarks>
/// The primary mechanism is WebFinger; future implementations may add fallback (e.g. a
/// configured domain → base-IRI mapping, or a proxy) without changing callers.
/// </remarks>
public interface IDiscoveryService
{
    /// <summary>
    /// Resolves the actor IRI for the given account.
    /// </summary>
    /// <param name="account">The account handle (e.g. <c>@user@example.com</c>) or <c>acct:</c> URI.</param>
    /// <param name="dialScheme">
    /// The scheme used to dial the account's instance for the WebFinger query. Defaults to
    /// <c>https</c>; pass <c>http</c> for a local/self-signed instance serving its well-known document
    /// over plain HTTP.
    /// </param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>The actor IRI, or null if the account could not be resolved.</returns>
    public Task<Iri?> ResolveActorAsync(string account, string dialScheme = "https", CancellationToken ct = default);

    /// <summary>
    /// Resolves the actor IRI for the given account, dialing the instance at the explicit
    /// <paramref name="dialBaseUri"/> authority (scheme + host + port) rather than the address's own
    /// host. Use this when the address's advertised host is not the browser-reachable host (a local
    /// Docker instance whose WebFinger document is served on a host-published port, e.g.
    /// <c>http://localhost:8081</c> for address <c>alice@localhost</c>). The query resource still
    /// carries the account's host (so the instance knows which actor to resolve); only the dial
    /// authority changes.
    /// </summary>
    /// <param name="account">The account handle (e.g. <c>@user@example.com</c>) or <c>acct:</c> URI.</param>
    /// <param name="dialBaseUri">The base URI to dial for the WebFinger query (its scheme + host + port
    /// form the well-known URL's authority).</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>The actor IRI, or null if the account could not be resolved.</returns>
    public Task<Iri?> ResolveActorAsync(string account, Uri dialBaseUri, CancellationToken ct = default);
}
