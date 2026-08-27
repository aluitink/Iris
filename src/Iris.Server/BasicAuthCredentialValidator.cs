using System.Text;
using Iris.Core;

namespace Iris.Server;

/// <summary>
/// A Basic-auth <see cref="IActorCredentialValidator"/>. Validates an <c>Authorization: Basic</c>
/// header against a username/password credential for the local actor.
/// </summary>
/// <remarks>
/// Phase 3 uses Basic auth (see Phase 9+ "Auth upgrade" for the OAuth2 swap). The credentials are
/// supplied by the host app (e.g. from a config store or environment). The validator does constant-time
/// comparison to avoid timing side-channels.
/// </remarks>
public sealed class BasicAuthCredentialValidator : IActorCredentialValidator
{
    private readonly Func<Iri, string, string, ValueTask<bool>> _credentialCheck;

    /// <summary>
    /// Initializes a new validator with the given credential check delegate.
    /// </summary>
    /// <param name="credentialCheck">
    /// A delegate that, given an actor IRI and the parsed Basic-auth username and password, returns
    /// whether the credentials are valid for that actor. The host app wires this to its credential
    /// store.
    /// </param>
    public BasicAuthCredentialValidator(
        Func<Iri, string, string, ValueTask<bool>> credentialCheck)
    {
        _credentialCheck = credentialCheck ?? throw new ArgumentNullException(nameof(credentialCheck));
    }

    /// <inheritdoc/>
    public async Task<string?> TryValidateAsync(Iri actorIri, string? authorizationHeader, CancellationToken ct = default)
    {
        // Parse the Authorization header: "Basic base64(username:password)".
        if (string.IsNullOrWhiteSpace(authorizationHeader) ||
            !authorizationHeader.StartsWith("Basic ", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var encoded = authorizationHeader["Basic ".Length..].Trim();
        string? username;
        string? password;
        try
        {
            var decoded = Encoding.UTF8.GetString(Convert.FromBase64String(encoded));
            var colon = decoded.IndexOf(':');
            if (colon < 0)
            {
                return null;
            }

            username = decoded[..colon];
            password = decoded[(colon + 1)..];
        }
        catch (FormatException)
        {
            // Malformed base64 or UTF-8 → invalid credentials.
            return null;
        }

        if (!await _credentialCheck(actorIri, username, password).ConfigureAwait(false))
        {
            return null;
        }

        // The authenticated handle is the Basic-auth username (the local actor's username).
        return username;
    }
}
