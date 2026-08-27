namespace Iris.Core;

/// <summary>
/// The cryptographic algorithm used by a <see cref="KeyPair"/>.
/// Only the two algorithms required by the ActivityPub key model are supported.
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
}
