using Iris.Core;

namespace Iris.Client;

/// <summary>
/// Resolves the signing <see cref="IIdentity"/> for a given actor IRI.
/// </summary>
/// <remarks>
/// The client needs to know *which key* to sign with for a given actor. This abstraction
/// lets the key source be an in-memory session store (v1), a server-fetched key (the
/// Basic-auth → private-key flow), or any future source (OAuth2, key server) without
/// changing the transport.
/// </remarks>
public interface IKeyProvider
{
    /// <summary>
    /// Attempts to resolve the signing identity for the given actor.
    /// </summary>
    /// <param name="actorId">The actor IRI to sign as.</param>
    /// <param name="identity">When successful, the identity (actor + key id); otherwise null.</param>
    /// <returns><see langword="true"/> if an identity was found; otherwise <see langword="false"/>.</returns>
    public bool TryGetIdentity(Iri actorId, out IIdentity? identity);

    /// <summary>
    /// Registers (or replaces) the key IRI used to sign for the given actor. The key must already be
    /// present in the backing <see cref="IKeyStore"/>. A host (or the server's startup) calls this to
    /// make a local actor signable — e.g. the instance actor whose key backs outbound delivery.
    /// </summary>
    /// <param name="actorId">The actor IRI to sign as.</param>
    /// <param name="keyId">The key IRI (must already be present in the key store).</param>
    public void RegisterKey(Iri actorId, Iri keyId);
}
