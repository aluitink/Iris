using Iris.Core;

namespace Iris.Server.Security;

/// <summary>
/// Stores OAuth2 authorization codes and Bearer tokens. The host app provides the backing store
/// (in-memory, database, Redis). Phase 15.2a introduces the server-side OAuth2 token endpoints;
/// this is the seam that makes the token store pluggable.
/// </summary>
public interface IOAuthTokenStore
{
    /// <summary>
    /// Stores an authorization code, associating it with the authenticated actor.
    /// </summary>
    /// <param name="code">The authorization code (a random opaque string).</param>
    /// <param name="actorIri">The IRI of the actor the code was issued for.</param>
    /// <param name="ct">Cancellation token.</param>
    public Task StoreAuthorizationCodeAsync(string code, Iri actorIri, CancellationToken ct = default);

    /// <summary>
    /// Redeems an authorization code, returning the associated actor IRI and removing the code.
    /// </summary>
    /// <param name="code">The authorization code to redeem.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The actor IRI the code was issued for, or null if the code is unknown or already redeemed.</returns>
    public Task<Iri?> RedeemAuthorizationCodeAsync(string code, CancellationToken ct = default);

    /// <summary>
    /// Stores a Bearer token, associating it with the authenticated actor.
    /// </summary>
    /// <param name="token">The Bearer token (a random opaque string).</param>
    /// <param name="actorIri">The IRI of the actor the token was issued for.</param>
    /// <param name="ct">Cancellation token.</param>
    public Task StoreTokenAsync(string token, Iri actorIri, CancellationToken ct = default);

    /// <summary>
    /// Resolves a Bearer token to the associated actor IRI.
    /// </summary>
    /// <param name="token">The Bearer token to resolve.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The actor IRI the token was issued for, or null if the token is unknown or revoked.</returns>
    public Task<Iri?> ResolveTokenAsync(string token, CancellationToken ct = default);

    /// <summary>
    /// Revokes a Bearer token.
    /// </summary>
    /// <param name="token">The Bearer token to revoke.</param>
    /// <param name="ct">Cancellation token.</param>
    public Task RevokeTokenAsync(string token, CancellationToken ct = default);
}
