using System.Security.Cryptography;

namespace Iris.Core.Identity;

/// <summary>
/// Helpers to load and save the <c>privateKey</c> actor-document property as PKCS#8 PEM.
/// The <c>privateKey</c> property is only ever served to the authenticated owner.
/// </summary>
/// <remarks>
/// These are thin conveniences over <see cref="KeyPair.FromPem"/> / <see cref="KeyPair.ExportPrivateKeyPem"/>
/// (RSA / EC) and <see cref="Ed25519Key.FromPem"/> / <see cref="Ed25519Key.ExportPrivateKeyPem"/>
/// (Ed25519) that keep the algorithm and key id with the PEM blob. A deployment can persist the
/// returned PEM string (plus the algorithm) and call <see cref="Load"/> on startup.
/// </remarks>
public static class KeyPem
{
    /// <summary>
    /// Loads a signing key from a PKCS#8 PEM string.
    /// </summary>
    /// <param name="pem">The PEM-encoded private key.</param>
    /// <param name="algorithm">The algorithm the key was generated with.</param>
    /// <param name="keyId">The IRI that identifies the key.</param>
    /// <returns>The loaded key (an RSA / EC <see cref="KeyPair"/> or an Ed25519 <see cref="Ed25519Key"/>).</returns>
    /// <exception cref="ArgumentOutOfRangeException">When <paramref name="algorithm"/> is not supported.</exception>
    public static ISigningKey Load(string pem, KeyAlgorithm algorithm, Iri keyId)
        => algorithm switch
        {
            KeyAlgorithm.Ed25519 => Ed25519Key.FromPem(pem, keyId),
            _ => KeyPair.FromPem(pem, algorithm, keyId),
        };

    /// <summary>
    /// Saves a signing key as a PKCS#8 PEM string.
    /// </summary>
    /// <param name="key">The key to export. Must not be null.</param>
    /// <returns>A PEM string (e.g. <c>-----BEGIN PRIVATE KEY-----</c>).</returns>
    /// <exception cref="ArgumentNullException">When <paramref name="key"/> is null.</exception>
    public static string Save(ISigningKey key)
    {
        ArgumentNullException.ThrowIfNull(key);
        return key.ExportPrivateKeyPem();
    }
}
