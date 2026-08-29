using System.Security.Cryptography;

namespace Iris.Core.Identity;

/// <summary>
/// Generates new <see cref="KeyPair"/> instances for the supported algorithms.
/// </summary>
/// <remarks>
/// Use this on first run to mint an ephemeral key when none is configured, or to rotate keys.
/// The returned <see cref="KeyPair"/> owns its key material and must be disposed.
/// </remarks>
public static class KeyPairGenerator
{
    /// <summary>
    /// The RSA modulus size (in bits) used for generated RSA keys.
    /// </summary>
    public const int RsaKeySizeBits = 2048;

    /// <summary>
    /// Generates a new key pair for the given algorithm.
    /// </summary>
    /// <param name="algorithm">The algorithm to generate a key for.</param>
    /// <param name="keyId">The IRI that will identify the key.</param>
    /// <returns>A new <see cref="KeyPair"/> (owns the key material; dispose when done).</returns>
    /// <exception cref="ArgumentOutOfRangeException">When <paramref name="algorithm"/> is not supported.</exception>
    public static KeyPair Generate(KeyAlgorithm algorithm, Iri keyId)
        => algorithm switch
        {
            KeyAlgorithm.Rsa => new KeyPair(RSA.Create(RsaKeySizeBits), algorithm, keyId),
            KeyAlgorithm.EcP256 => new KeyPair(ECDsa.Create(ECCurve.NamedCurves.nistP256), algorithm, keyId),
            // Ed25519 is not an <see cref="AsymmetricAlgorithm"/> and has no BCL type on this
            // runtime; it is handled by the dedicated <see cref="Ed25519Key"/> type instead.
            _ => throw new ArgumentOutOfRangeException(nameof(algorithm), algorithm,
                $"Algorithm {algorithm} is not supported by KeyPairGenerator (use Ed25519Key for Ed25519)."),
        };

    /// <summary>
    /// Generates a new RSA-2048 key pair.
    /// </summary>
    /// <param name="keyId">The IRI that will identify the key.</param>
    /// <returns>A new <see cref="KeyPair"/> (owns the key material; dispose when done).</returns>
    public static KeyPair GenerateRsa(Iri keyId) => Generate(KeyAlgorithm.Rsa, keyId);

    /// <summary>
    /// Generates a new EC P-256 key pair.
    /// </summary>
    /// <param name="keyId">The IRI that will identify the key.</param>
    /// <returns>A new <see cref="KeyPair"/> (owns the key material; dispose when done).</returns>
    public static KeyPair GenerateEcP256(Iri keyId) => Generate(KeyAlgorithm.EcP256, keyId);
}
