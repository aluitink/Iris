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
}
