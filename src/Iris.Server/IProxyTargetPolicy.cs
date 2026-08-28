using Iris.Core;

namespace Iris.Server;

/// <summary>
/// Decides whether the proxy endpoint may forward a request to a given target.
/// </summary>
/// <remarks>
/// The proxy (<c>POST /ap/v1/proxy/{target}</c>) signs an authenticated actor's request with the
/// actor's own key and forwards it to an arbitrary target, so which targets it may reach is a
/// deployment policy. A host composes one or more <see cref="IProxyTargetPolicy"/> implementations
/// (e.g. an allowlist + a rate limiter) into a single policy; the proxy endpoint rejects the request
/// when <see cref="TryAuthorizeAsync"/> reports the target is not allowed. The policy is a drop-in
/// seam (like <see cref="IActorCredentialValidator"/>): the default composition (allowlist + rate
/// limit) is registered by <c>AddActivityPubServer</c>, and a host may replace it.
/// </remarks>
public interface IProxyTargetPolicy
{
    /// <summary>
    /// Attempts to authorize a proxy request to <paramref name="target"/> on behalf of
    /// <paramref name="actorIri"/>.
    /// </summary>
    /// <param name="actorIri">The IRI of the authenticated actor the request is being made for.</param>
    /// <param name="target">The absolute target IRI the request will be forwarded to.</param>
    /// <param name="reason">When the target is not allowed, a human-readable reason; otherwise null.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns><see langword="true"/> when the request may be forwarded; otherwise <see langword="false"/>.</returns>
    public Task<bool> TryAuthorizeAsync(Iri actorIri, Iri target, out string? reason, CancellationToken ct = default);
}
