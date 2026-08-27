namespace Iris.Core;

/// <summary>
/// An identity that signs ActivityPub requests. An identity pairs the actor IRI (the
/// <c>actor</c> / signing principal) with the <see cref="KeyPair"/> whose <c>keyId</c>
/// appears in the HTTP signature.
/// </summary>
/// <remarks>
/// The general ActivityPub rule: the <c>actor</c> property on an activity matches the
/// <c>keyId</c> in the signature. The <see cref="KeyPair"/> is resolved from an
/// <see cref="IKeyStore"/> by the identity's <see cref="KeyId"/>.
/// </remarks>
public interface IIdentity
{
    /// <summary>
    /// Gets the IRI of the actor this identity signs as (the <c>actor</c> / signing principal).
    /// </summary>
    public Iri ActorId { get; }

    /// <summary>
    /// Gets the IRI of the key used to sign (the <c>keyId</c> / <c>publicKey.id</c> in a signature).
    /// </summary>
    public Iri KeyId { get; }
}
