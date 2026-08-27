using Iris.Core;

namespace Iris.Server;

/// <summary>
/// Validates that a request is authorized to read the owner-only extension of an actor document
/// (the <c>privateKey</c> PEM). Phase 3 uses Basic auth; Phase 9+ may swap in OAuth2 bearer.
/// </summary>
/// <remarks>
/// The validator is a drop-in seam (see Phase 9+ "Auth upgrade"). It receives the actor IRI and the
/// request's <c>Authorization</c> header value, and returns the authenticated actor handle (the
/// local username) when the credentials are valid for that actor, or null otherwise. The actor
/// document endpoint uses the returned handle to decide whether to include <c>privateKey</c>.
/// </remarks>
public interface IActorCredentialValidator
{
    /// <summary>
    /// Attempts to validate the request's credentials for the given actor.
    /// </summary>
    /// <param name="actorIri">The IRI of the actor whose owner-only extension is being requested.</param>
    /// <param name="authorizationHeader">The value of the request's <c>Authorization</c> header (may be null).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>
    /// A task that completes with the authenticated actor handle when the credentials are valid
    /// for <paramref name="actorIri"/>; otherwise null.
    /// </returns>
    public Task<string?> TryValidateAsync(Iri actorIri, string? authorizationHeader, CancellationToken ct = default);
}
