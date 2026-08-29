namespace Iris.Core.Identity;

/// <summary>
/// The cryptographic algorithm used by a <see cref="KeyPair"/>.
/// The three algorithms the ActivityPub key model in practice uses are supported.
/// </summary>
public enum KeyAlgorithm
{
    /// <summary>
    /// RSA with a 2048-bit modulus, signing with PKCS#1 v1.5 and SHA-256.
    /// </summary>
    Rsa = 0,

    /// <summary>
    /// Elliptic Curve (NIST P-256 / secp256r1), signing with ECDSA and SHA-256.
    /// </summary>
    EcP256 = 1,

    /// <summary>
    /// EdDSA (RFC 8032) over the Ed25519 curve. Signatures are 64-byte; the key is a 32-byte
    /// seed (PKCS#8 private) and a 32-byte public key (PKIX). This is the algorithm Pleroma and
    /// several modern servers sign with by default.
    /// </summary>
    Ed25519 = 2,
}
