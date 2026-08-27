using System.Text.Json;

namespace Iris.Client;

/// <summary>
/// The cached public key of a remote actor: the JWK (as a JSON object) plus the algorithm label
/// used in the <c>Signature</c> header.
/// </summary>
/// <param name="Jwk">The JWK JSON object (kty, plus n/e or crv/x/y).</param>
/// <param name="AlgorithmLabel">The algorithm label (e.g. <c>rsa-sha256</c> / <c>ecdsa-p256-sha256</c>).</param>
public sealed record JwkKey(string Jwk, string AlgorithmLabel)
{
    /// <summary>
    /// Gets the JWK as a <see cref="JsonElement"/> (parsed from <see cref="Jwk"/>).
    /// </summary>
    public JsonElement ToElement() => JsonSerializer.Deserialize<JsonElement>(Jwk);
}
