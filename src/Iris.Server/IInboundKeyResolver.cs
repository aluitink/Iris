using Iris.Core;

namespace Iris.Server;

/// <summary>
/// Resolves the public key for a given key IRI, for the purpose of verifying an inbound signature.
/// </summary>
/// <remarks>
/// This is the server-side counterpart to the client's key provider. When instance A receives a
/// signed request from instance B, A must resolve the public key identified by the request's
/// <c>keyId</c> — a key A has never seen locally. The default implementation
/// (<see cref="RemoteInboundKeyResolver"/>) fetches the owning actor's document over the wire and
/// extracts the key, caching it in the Phase 3 <see cref="RemoteKeyCache"/>. A host app may replace
/// this with a key-directory or trust-on-first-use (TOFU) implementation.
/// </remarks>
public interface IInboundKeyResolver
{
    /// <summary>
    /// Resolves the public key for the given key IRI.
    /// </summary>
    /// <param name="keyId">The key IRI (the <c>keyId</c> in a <c>Signature</c> header; the
    /// <c>publicKey.id</c> of the signing actor).</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>A public-only key suitable for verification (an RSA / EC <see cref="KeyPair"/> or an
    /// Ed25519 <see cref="Ed25519Key"/>), or null when the key cannot be resolved (unknown actor,
    /// missing <c>publicKey</c>, fetch failure).</returns>
    /// <remarks>
    /// The returned key, when non-null and <see cref="IDisposable"/>, is owned by the caller and must
    /// be disposed after use. Resolution failures are an expected condition (return null), not an
    /// error (do not throw).
    /// </remarks>
    public Task<ISigningKey?> ResolveAsync(Iri keyId, CancellationToken ct = default);
}
