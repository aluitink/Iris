using Iris.Core;

namespace Iris.Client;

/// <summary>
/// Authenticates a client session against an Iris server and yields the owner's actor
/// document plus its private key, so the client can sign requests as that actor.
/// </summary>
/// <remarks>
/// This is the "Basic-auth → private-key" flow: the host app already holds a credential
/// (a Basic-auth user:password); the authenticator proves it to the server, which returns
/// the actor document with the owner-only <c>privateKey</c> (PKCS#8 PEM) extension. The
/// loaded <see cref="KeyPair"/> is then registered in an <see cref="IKeyStore"/> and mapped
/// to the actor IRI in an <see cref="IKeyProvider"/> so the <see cref="SigningHandler"/> can
/// sign. The returned <see cref="KeyPair"/> is owned by the caller and must be disposed.
/// </remarks>
public interface IClientAuthenticator
{
    /// <summary>
    /// Authenticates the session and returns the owner's actor document and loaded private key.
    /// </summary>
    /// <param name="actorId">The IRI of the actor (the authenticated owner) to fetch the document for.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>The authenticated actor and its loaded private key, or null if authentication
    /// failed (no credentials, non-success response, no <c>privateKey</c> field, or unparsable key).</returns>
    public Task<AuthenticatedActor?> AuthenticateAsync(Iri actorId, CancellationToken ct = default);
}
