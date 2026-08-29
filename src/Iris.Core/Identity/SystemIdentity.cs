namespace Iris.Core.Identity;

/// <summary>
/// A concrete <see cref="IIdentity"/> pairing an actor IRI with a key IRI.
/// </summary>
/// <param name="ActorId">The IRI of the actor this identity signs as.</param>
/// <param name="KeyId">The IRI of the key used to sign.</param>
public sealed record SystemIdentity(Iri ActorId, Iri KeyId) : IIdentity;
