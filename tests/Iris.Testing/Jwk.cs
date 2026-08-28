using Iris.Core;

namespace Iris.Testing;

/// <summary>
/// Helpers for constructing the <c>publicKey</c> extension of a test-seeded actor document.
/// </summary>
/// <remarks>
/// Iris serves the public key as PEM (<c>publicKeyPem</c>) — the most widely compatible wire format —
/// so a remote resolver (including one that only accepts PEM) can verify signatures. This helper builds
/// the extension object ({@code id}, {@code owner}, {@code publicKeyPem}) in one place so the seeding
/// helpers and the tests that assert its shape stay in agreement.
/// </remarks>
public static class Jwk
{
    /// <summary>
    /// Builds the <c>publicKey</c> extension JSON for a seeded actor: the key's IRI, its owner, and the
    /// public key as a PKIX PEM string.
    /// </summary>
    /// <param name="key">The seeded key pair.</param>
    /// <param name="owner">The IRI of the actor that owns the key.</param>
    /// <returns>The <c>publicKey</c> extension as a JSON object string.</returns>
    /// <exception cref="ArgumentNullException">When <paramref name="key"/> is null.</exception>
    public static System.Text.Json.JsonElement BuildPublicExtension(KeyPair key, string owner)
    {
        ArgumentNullException.ThrowIfNull(key);
        return System.Text.Json.JsonSerializer.SerializeToElement(new
        {
            id = key.KeyId.Value,
            owner,
            publicKeyPem = key.ExportPublicKeyPem(),
        });
    }
}
