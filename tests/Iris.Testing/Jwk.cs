using System.Text.Json;
using Iris.Core;

namespace Iris.Testing;

/// <summary>
/// Helpers for reading the JWK (JSON Web Key) representation of a <see cref="KeyPair"/>.
/// </summary>
/// <remarks>
/// A <see cref="KeyPair.GetPublicJwk()"/> returns a JSON object string with the algorithm's public
/// members (RSA: <c>n</c>/<c>e</c>; EC P-256: <c>crv</c>/<c>x</c>/<c>y</c>). Seeding a test actor's
/// <c>publicKey</c> extension needs these individual components; this helper extracts one by name.
/// </remarks>
public static class Jwk
{
    /// <summary>
    /// Extracts a single named component from the key's public JWK.
    /// </summary>
    /// <param name="key">The key pair to read.</param>
    /// <param name="name">The JWK member name (e.g. <c>x</c>, <c>y</c>, <c>n</c>, <c>e</c>).</param>
    /// <returns>The component's base64url-encoded value.</returns>
    /// <exception cref="ArgumentNullException">When <paramref name="key"/> is null.</exception>
    /// <exception cref="ArgumentException">When the JWK has no such member.</exception>
    public static string ExtractComponent(KeyPair key, string name)
    {
        ArgumentNullException.ThrowIfNull(key);
        using var doc = JsonDocument.Parse(key.GetPublicJwk());
        return doc.RootElement.GetProperty(name).GetString()!;
    }
}
