namespace Iris.Server.Data.Entities;

/// <summary>
/// A local signing key row. The key is persisted as its PEM form (private + public) and algorithm so
/// it can be reconstructed after a restart via <c>KeyPair.FromPem</c> / <c>Ed25519Key.FromPem</c>.
/// </summary>
/// <remarks>
/// Keys are opaque strings already (there is no ActivityStreams shape to a signing key), so they are
/// plain text columns rather than a <c>jsonb</c> payload.
/// </remarks>
public sealed class KeyEntity
{
    /// <summary>
    /// The key's IRI (primary key; the <c>keyId</c> / <c>publicKey.id</c> in a signature).
    /// </summary>
    public string KeyId { get; set; } = string.Empty;

    /// <summary>
    /// The key's algorithm (<see cref="Iris.Core.Identity.KeyAlgorithm"/>) as its string name.
    /// </summary>
    public string Algorithm { get; set; } = string.Empty;

    /// <summary>
    /// The private key (PKCS#8 PEM for RSA/EC, the seed for Ed25519). Null for a public-only key.
    /// </summary>
    public string? PrivateKeyPem { get; set; }

    /// <summary>
    /// The public key (SubjectPublicKeyInfo PEM for RSA/EC, the public key for Ed25519).
    /// </summary>
    public string? PublicKeyPem { get; set; }
}
