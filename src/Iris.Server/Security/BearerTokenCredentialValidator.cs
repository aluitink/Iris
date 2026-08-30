using Iris.Core;

namespace Iris.Server.Security;

/// <summary>
/// A Bearer-token <see cref="IActorCredentialValidator"/>. Validates an
/// <c>Authorization: Bearer</c> header against a token credential for the local actor.
/// </summary>
/// <remarks>
/// Phase 15 introduces Bearer-token auth alongside Basic auth (see Phase 15 "Auth upgrade").
/// The token is supplied by the host app (e.g. from an OAuth2 authorization flow or a static
/// API-key store). The validator delegates the token→handle resolution to a host-provided
/// credential-check delegate, so the token format and storage are host-specific.
/// </remarks>
public sealed class BearerTokenCredentialValidator : IActorCredentialValidator
{
    private readonly Func<Iri, string, ValueTask<string?>> _tokenResolve;

    /// <summary>
    /// Initializes a new validator with the given token-resolution delegate.
    /// </summary>
    /// <param name="tokenResolve">
    /// A delegate that, given an actor IRI and the Bearer token, returns the authenticated actor
    /// handle when the token is valid for that actor, or null otherwise. The host app wires this
    /// to its token store (e.g. an OAuth2 token table or a static API-key map).
    /// </param>
    public BearerTokenCredentialValidator(
        Func<Iri, string, ValueTask<string?>> tokenResolve)
    {
        _tokenResolve = tokenResolve ?? throw new ArgumentNullException(nameof(tokenResolve));
    }

    /// <inheritdoc/>
    public async Task<string?> TryValidateAsync(Iri actorIri, string? authorizationHeader, CancellationToken ct = default)
    {
        // Parse the Authorization header: "Bearer <token>".
        if (string.IsNullOrWhiteSpace(authorizationHeader) ||
            !authorizationHeader.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var token = authorizationHeader["Bearer ".Length..].Trim();
        if (token.Length == 0)
        {
            return null;
        }

        return await _tokenResolve(actorIri, token).ConfigureAwait(false);
    }
}
