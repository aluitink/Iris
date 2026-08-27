namespace Iris.Core;

/// <summary>
/// Helpers to load and save the <c>privateKey</c> actor-document property as PKCS#8 PEM.
/// The <c>privateKey</c> property is only ever served to the authenticated owner.
/// </summary>
/// <remarks>
/// These are thin conveniences over <see cref="KeyPair.FromPem"/> and
/// <see cref="KeyPair.ExportPrivateKeyPem"/> that keep the algorithm and key id with the
/// PEM blob. A deployment can persist the returned PEM string (plus the algorithm) and
/// call <see cref="Load"/> on startup.
/// </remarks>
public static class KeyPem
{
    /// <summary>
    /// Loads a key pair from a PKCS#8 PEM string.
    /// </summary>
    /// <param name="pem">The PEM-encoded private key.</param>
    /// <param name="algorithm">The algorithm the key was generated with.</param>
    /// <param name="keyId">The IRI that identifies the key.</param>
    /// <returns>The loaded <see cref="KeyPair"/> (owns the key material).</returns>
    public static KeyPair Load(string pem, KeyAlgorithm algorithm, Iri keyId)
        => KeyPair.FromPem(pem, algorithm, keyId);

    /// <summary>
    /// Saves a key pair as a PKCS#8 PEM string.
    /// </summary>
    /// <param name="key">The key pair to export. Must not be null.</param>
    /// <returns>A PEM string (e.g. <c>-----BEGIN PRIVATE KEY-----</c>).</returns>
    /// <exception cref="ArgumentNullException">When <paramref name="key"/> is null.</exception>
    public static string Save(KeyPair key)
    {
        ArgumentNullException.ThrowIfNull(key);
        return key.ExportPrivateKeyPem();
    }
}
