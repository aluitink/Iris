namespace Iris.Core;

/// <summary>
/// Verifies an incoming ActivityPub request's <c>Signature</c> header.
/// </summary>
/// <remarks>
/// The verifier reconstructs the signature base from whatever <c>headers</c> list is declared
/// in the <c>Signature</c> header, so it accepts both <see cref="SigningProfile.ClientToServer"/>
/// and <see cref="SigningProfile.ServerToServer"/> signatures. The key is resolved from the
/// <see cref="IKeyStore"/> by the <c>keyId</c> in the header. Callers that need to bind the
/// signature to a specific actor (e.g. the <c>actor</c> field of the request body) should
/// resolve that actor from the key's <c>keyId</c> and check it themselves — this type does the
/// pure cryptographic verification.
/// </remarks>
public interface ISignatureVerifier
{
    /// <summary>
    /// Verifies the signature on the given request.
    /// </summary>
    /// <param name="metadata">The request fields.</param>
    /// <param name="signatureHeader">The raw <c>Signature</c> header value.</param>
    /// <returns><see langword="true"/> when the signature is valid; otherwise <see langword="false"/>.</returns>
    /// <remarks>
    /// Returns <see langword="false"/> (not throws) when the header is malformed, the key is
    /// unknown, or the signature does not verify — an invalid signature is an expected condition,
    /// not an error.
    /// </remarks>
    public bool Verify(HttpRequestMetadata metadata, string signatureHeader);

    /// <summary>
    /// Verifies the signature on the given request using an explicitly provided key.
    /// </summary>
    /// <param name="metadata">The request fields.</param>
    /// <param name="key">The public key to verify with (e.g. a remote key resolved from the sender's
    /// actor document rather than looked up from the local <see cref="IKeyStore"/>).</param>
    /// <param name="signatureHeader">The raw <c>Signature</c> header value.</param>
    /// <returns><see langword="true"/> when the signature is valid; otherwise <see langword="false"/>.</returns>
    /// <remarks>
    /// This overload exists because a server validating an inbound federation request resolves the
    /// signing key from a *remote* source (the sender's actor document), not from its own key store.
    /// It performs the same base reconstruction and cryptographic check as
    /// <see cref="Verify(HttpRequestMetadata, string)"/> but with the key supplied directly. The key
    /// may be an RSA / EC <see cref="KeyPair"/> or an Ed25519 <see cref="Ed25519Key"/>.
    /// </remarks>
    public bool Verify(HttpRequestMetadata metadata, ISigningKey key, string signatureHeader);
}
