namespace Iris.Core.Identity;

/// <summary>
/// A signing/verification key used for ActivityPub HTTP signatures, abstracted over the
/// underlying algorithm. Both <see cref="KeyPair"/> (RSA / EC P-256, backed by the BCL
/// <see cref="System.Security.Cryptography.AsymmetricAlgorithm"/>) and <see cref="Ed25519Key"/>
/// (Ed25519, backed by BouncyCastle — the BCL has no Ed25519 type on this runtime) implement
/// this interface.
/// </summary>
/// <remarks>
/// The signing and verification pipelines (<see cref="ISignatureSigner"/>,
/// <see cref="ISignatureVerifier"/>, <see cref="IKeyStore"/>) depend on this interface rather than
/// <see cref="KeyPair"/> so an Ed25519 key is interchangeable with an RSA/EC key at the wire
/// boundary. The algorithm is carried by <see cref="Algorithm"/> and the signature label is derived
/// from it via the <c>Signatures.AlgorithmLabel</c> helper.
/// </remarks>
public interface ISigningKey
{
    /// <summary>
    /// Gets the algorithm of the key.
    /// </summary>
    public KeyAlgorithm Algorithm { get; }

    /// <summary>
    /// Gets the IRI that identifies this key (the <c>keyId</c> / <c>publicKey.id</c> in a signature).
    /// </summary>
    public Iri KeyId { get; }

    /// <summary>
    /// Signs the given data with this key's private key.
    /// </summary>
    /// <param name="data">The bytes to sign. Must not be null.</param>
    /// <returns>The signature bytes.</returns>
    /// <exception cref="ArgumentNullException">When <paramref name="data"/> is null.</exception>
    /// <exception cref="InvalidOperationException">When the key is public-only (cannot sign).</exception>
    public byte[] Sign(byte[] data);

    /// <summary>
    /// Signs the given data with this key's private key, asynchronously.
    /// </summary>
    /// <remarks>
    /// The default implementation defers to the synchronous <see cref="Sign"/>, which is correct for
    /// every BCL/BouncyCastle-backed key (they sign synchronously). A key whose signing backend is
    /// inherently asynchronous — e.g. the browser's WebCrypto (<c>crypto.subtle</c>) in a Blazor
    /// WebAssembly host, where the BCL has no usable RSA — overrides this to await its backend. The
    /// the signature-signer pipeline prefers <c>SignAsync</c> so a WebCrypto key signs without
    /// touching the synchronous path.
    /// </remarks>
    /// <param name="data">The bytes to sign. Must not be null.</param>
    /// <param name="ct">A cancellation token.</param>
    /// <returns>The signature bytes.</returns>
    /// <exception cref="ArgumentNullException">When <paramref name="data"/> is null.</exception>
    /// <exception cref="InvalidOperationException">When the key is public-only (cannot sign).</exception>
    Task<byte[]> SignAsync(byte[] data, CancellationToken ct = default)
        => Task.FromResult(Sign(data));

    /// <summary>
    /// Verifies a signature over the given data using this key's public key.
    /// </summary>
    /// <param name="data">The bytes that were signed. Must not be null.</param>
    /// <param name="signature">The signature to verify. Must not be null.</param>
    /// <returns><see langword="true"/> when the signature is valid; otherwise <see langword="false"/>.</returns>
    /// <exception cref="ArgumentNullException">When <paramref name="data"/> or <paramref name="signature"/> is null.</exception>
    public bool Verify(byte[] data, byte[] signature);

    /// <summary>
    /// Gets the JWK (JSON Web Key) representation of the public key, as a JSON object string.
    /// </summary>
    /// <returns>A JSON object string describing the public key.</returns>
    public string GetPublicJwk();

    /// <summary>
    /// Exports this key's public key as a SubjectPublicKeyInfo (PKIX) PEM string.
    /// </summary>
    /// <returns>A PEM string (e.g. <c>-----BEGIN PUBLIC KEY-----</c>).</returns>
    public string ExportPublicKeyPem();

    /// <summary>
    /// Exports this key's private key as a PKCS#8 PEM string.
    /// </summary>
    /// <returns>A PEM string (e.g. <c>-----BEGIN PRIVATE KEY-----</c>).</returns>
    /// <exception cref="InvalidOperationException">When the key is public-only (has no private key).</exception>
    public string ExportPrivateKeyPem();

    /// <summary>
    /// Gets a JWK Thumbprint (RFC 7638) of the public key.
    /// </summary>
    /// <returns>The base64url-encoded SHA-256 thumbprint.</returns>
    public string GetThumbprint();
}
