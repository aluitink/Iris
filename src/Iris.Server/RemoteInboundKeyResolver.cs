using System.Security.Cryptography;
using System.Text.Json;
using Iris.Client;
using Iris.Core;
using KristofferStrube.ActivityStreams;

namespace Iris.Server;

/// <summary>
/// The default <see cref="IInboundKeyResolver"/>. Resolves a remote actor's public key by fetching
/// the actor's document (via <see cref="IActorDocumentFetcher"/>), extracting the <c>publicKey</c>
/// (JWK or PEM), and reconstructing a public-only <see cref="KeyPair"/>.
/// </summary>
/// <remarks>
/// The key material is cached by key IRI in the Phase 3 <see cref="RemoteKeyCache"/> (via
/// <see cref="ServerCaches.RemoteKeys"/>) as a <see cref="JwkKey"/>. Both wire forms are normalized to a
/// JWK at the boundary: a <c>publicKeyPem</c> is loaded and re-serialized as a JWK, and a JWK
/// <c>publicKey</c> object is used as-is. The cached value is non-disposable (a JWK string), so the
/// cache never owns a <see cref="KeyPair"/>; a fresh public-only <see cref="KeyPair"/> is built from
/// the cached JWK on each resolution and owned by the caller. See Resolved Decision #27.
/// </remarks>
public sealed class RemoteInboundKeyResolver(
    IActorDocumentFetcher actorDocuments,
    RemoteKeyCache remoteKeys) : IInboundKeyResolver
{
    private readonly IActorDocumentFetcher _actorDocuments = actorDocuments!;
    private readonly RemoteKeyCache _remoteKeys = remoteKeys!;

    /// <inheritdoc/>
    public async Task<KeyPair?> ResolveAsync(Iri keyId, CancellationToken ct = default)
    {
        var (jwkKey, _, _) = await _remoteKeys
            .GetAsync(keyId, forceRefresh: false, factory: key => FetchJwkAsync(key, ct), ct)
            .ConfigureAwait(false);

        if (jwkKey is null)
        {
            return null;
        }

        var algorithm = AlgorithmFromLabel(jwkKey.AlgorithmLabel);
        if (algorithm is null)
        {
            return null;
        }

        try
        {
            return KeyPair.FromJwk(jwkKey.Jwk, algorithm.Value, keyId);
        }
        catch (FormatException)
        {
            // A malformed cached JWK is a resolution failure, not an error.
            return null;
        }
        catch (System.Security.Cryptography.CryptographicException)
        {
            return null;
        }
    }

    private async Task<JwkKey?> FetchJwkAsync(Iri keyId, CancellationToken ct)
    {
        var ownerActorIri = ActorIriFromKeyId(keyId);

        var actor = await _actorDocuments.GetActorAsync(ownerActorIri, ct).ConfigureAwait(false);
        if (actor is null)
        {
            return null;
        }

        if (actor.ExtensionData is not { } extension
            || !extension.TryGetValue("publicKey", out var publicKey)
            || publicKey.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        // Form 1: a JWK object (kty + n/e or crv/x/y) — the standard ActivityPub shape.
        if (publicKey.TryGetProperty("kty", out var kty) && kty.ValueKind == JsonValueKind.String)
        {
            var algorithm = AlgorithmFromKty(kty.GetString());
            if (algorithm is null)
            {
                return null;
            }

            return new JwkKey(publicKey.GetRawText(), Signatures.AlgorithmLabel(algorithm.Value));
        }

        // Form 2: a publicKeyPem string (e.g. an Iris-seeded document). Normalize to a JWK so the
        // cache stays uniform; load the public key and re-serialize its JWK.
        if (publicKey.TryGetProperty("publicKeyPem", out var pemElement)
            && pemElement.ValueKind == JsonValueKind.String
            && pemElement.GetString() is { Length: > 0 } pem)
        {
            var pemAlgorithm = AlgorithmFromPem(pem);
            if (pemAlgorithm is null)
            {
                return null;
            }

            using var key = KeyPair.FromPem(pem, pemAlgorithm.Value, ownerActorIri);
            return new JwkKey(key.GetPublicJwk(), Signatures.AlgorithmLabel(pemAlgorithm.Value));
        }

        return null;
    }

    /// <summary>
    /// Derives the owning actor IRI from a key IRI by stripping the <c>#fragment</c> (ActivityPub
    /// convention: <c>keyId = actorIri + "#key-1"</c>).
    /// </summary>
    /// <param name="keyId">The key IRI.</param>
    /// <returns>The actor IRI (the key IRI with any <c>#fragment</c> removed).</returns>
    private static Iri ActorIriFromKeyId(Iri keyId)
    {
        var value = keyId.Value;
        var fragment = value.IndexOf('#');
        return fragment >= 0 ? new Iri(value[..fragment]) : keyId;
    }

    /// <summary>
    /// Maps a JWK <c>kty</c> value to an <see cref="KeyAlgorithm"/>.
    /// </summary>
    /// <param name="kty">The JWK key type (<c>"RSA"</c> or <c>"EC"</c>).</param>
    /// <returns>The algorithm, or null when the <c>kty</c> is not supported.</returns>
    private static KeyAlgorithm? AlgorithmFromKty(string? kty) => kty switch
    {
        "RSA" => KeyAlgorithm.Rsa,
        "EC" => KeyAlgorithm.EcP256,
        _ => null,
    };

    /// <summary>
    /// Determines the <see cref="KeyAlgorithm"/> of a public key PEM (PKIX / SubjectPublicKeyInfo).
    /// </summary>
    /// <param name="pem">The PEM-encoded public key.</param>
    /// <returns>The algorithm, or null when the PEM cannot be parsed as a supported public key.</returns>
    private static KeyAlgorithm? AlgorithmFromPem(string pem)
    {
        try
        {
            // ImportFromPem on an ECDSA key with the P-256 curve set succeeds only for EC public
            // keys; an RSA public key fails to import into an ECDSA instance and vice versa. Try EC
            // first (the Iris default), then RSA.
            using var ec = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            ec.ImportFromPem(pem);
            return KeyAlgorithm.EcP256;
        }
        catch (System.Security.Cryptography.CryptographicException)
        {
            // Not an EC public key; fall through to RSA.
        }

        try
        {
            using var rsa = RSA.Create();
            rsa.ImportFromPem(pem);
            return KeyAlgorithm.Rsa;
        }
        catch (System.Security.Cryptography.CryptographicException)
        {
            return null;
        }
    }

    /// <summary>
    /// Maps an algorithm label (e.g. <c>rsa-sha256</c>) to an <see cref="KeyAlgorithm"/>.
    /// </summary>
    /// <param name="label">The label.</param>
    /// <returns>The algorithm, or null when the label is not recognized.</returns>
    private static KeyAlgorithm? AlgorithmFromLabel(string? label)
    {
        if (label is null)
        {
            return null;
        }

        foreach (KeyAlgorithm algorithm in new[] { KeyAlgorithm.Rsa, KeyAlgorithm.EcP256 })
        {
            if (string.Equals(Signatures.AlgorithmLabel(algorithm), label, StringComparison.OrdinalIgnoreCase))
            {
                return algorithm;
            }
        }

        return null;
    }
}
