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
    /// <param name="ct">The cancellation token.</param>
    /// <returns>The actor IRI, or null if the account could not be resolved.</returns>
    public Task<Iri?> ResolveActorAsync(string account, CancellationToken ct = default);
}
