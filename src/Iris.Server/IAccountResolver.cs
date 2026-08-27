using Iris.Core;

namespace Iris.Server;

/// <summary>
/// Resolves a remote account (WebFinger <c>resource</c>, e.g. <c>acct:bob@b.test</c>) to the actor IRI
/// it points to. A seam so the server's outbound federation paths (e.g. the Phase 5 follow-by-handle
/// flow) can resolve handles without depending on a concrete WebFinger client.
/// </summary>
/// <remarks>
/// The default implementation (<see cref="WebFingerAccountResolver"/>) wraps the
/// <see cref="Iris.Client.WebFingerClient"/> and reads through the Phase 3
/// <see cref="WebFingerCache"/> (by account IRI), so a remote account is resolved once and reused
/// across lookups within the cache's TTL. It is registered by <see cref="ActivityPubServerExtensions"/>
/// via <c>AddActivityPubServer</c>.
/// </remarks>
public interface IAccountResolver
{
    /// <summary>
    /// Resolves the actor IRI for the given account.
    /// </summary>
    /// <param name="account">The account handle (e.g. <c>@bob@b.test</c>, <c>bob@b.test</c>) or a full
    /// <c>acct:</c> URI. Normalized to an <c>acct:handle@host</c> resource URI.</param>
    /// <param name="forceRefresh">When true, the cache is bypassed for the read (the resolution is
    /// always re-fetched), but a non-null result is still written back. The server's <c>?refresh=true</c>
    /// escape hatch.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>The resolved actor IRI, or null when discovery fails or no <c>self</c> link is found.</returns>
    /// <remarks>
    /// Resolution failures (unreachable host, 404, no self link) are an expected condition — return
    /// null, do not throw. An absent result is not cached, so a later lookup retries.
    /// </remarks>
    public Task<Iri?> ResolveAsync(string account, bool forceRefresh = false, CancellationToken ct = default);
}
