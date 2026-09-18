using System.Security.Cryptography;
using System.Text.Json;
using Iris.Client;
using Iris.Core;
using KristofferStrube.ActivityStreams;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Iris.Server.Security;

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
    RemoteKeyCache remoteKeys,
    ILogger<RemoteInboundKeyResolver>? logger = null) : IInboundKeyResolver
{
    private readonly IActorDocumentFetcher _actorDocuments = actorDocuments!;
    private readonly RemoteKeyCache _remoteKeys = remoteKeys!;
    private readonly ILogger<RemoteInboundKeyResolver> _logger = logger ?? NullLogger<RemoteInboundKeyResolver>.Instance;

    /// <inheritdoc/>
    public async Task<ISigningKey?> ResolveAsync(Iri keyId, CancellationToken ct = default)
    {
        var (jwkKey, _, _) = await _remoteKeys
            .GetAsync(keyId, bypassCache: false, factory: key => FetchJwkAsync(key, ct), ct)
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
            return algorithm.Value switch
            {
                KeyAlgorithm.Ed25519 => Ed25519Key.FromJwk(jwkKey.Jwk, keyId),
                _ => KeyPair.FromJwk(jwkKey.Jwk, algorithm.Value, keyId),
            };
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
            // The outbound fetch of the remote actor document failed (a non-success HTTP status, a
            // transport error, or the IRI resolved to a non-actor). This is the most common cause of
            // an inbound "could not resolve public key": the remote instance (e.g. Hachyderm,
            // mastodon.social) requires the actor-doc GET to be signed and rejects it — often because
            // it cannot itself resolve Iris's public key (a mutual key-resolution bootstrap failure).
            // Log at Warning so the failure is diagnosable; previously the null was silent and the
            // only symptom was the downstream "could not resolve public key" with no cause.
            _logger.LogWarning(
                "Key resolution: could not fetch remote actor document {ActorIri} (keyId {KeyId}); the " +
                "outbound signed GET returned a non-success status or a non-actor. Inbound signatures " +
                "from this actor will be rejected until the document is reachable.",
                ownerActorIri, keyId);
            return null;
        }

        if (actor.ExtensionData is not { } extension
            || !extension.TryGetValue(ActivityPubExtensionNames.PublicKey, out var publicKey)
            || publicKey.ValueKind != JsonValueKind.Object)
        {
            // The actor document was fetched but carries no usable `publicKey` object. Log at Warning
            // so the "could not resolve public key" downstream is attributable to the document shape
            // (missing/malformed publicKey) rather than a fetch failure.
            _logger.LogWarning(
                "Key resolution: remote actor document {ActorIri} (keyId {KeyId}) has no usable " +
                "`publicKey` object; inbound signatures from this actor will be rejected.",
                ownerActorIri, keyId);
            return null;
        }

        // F-25: honor key rotation — when the remote document's publicKey declares a `replaces`
        // property, the old key has been superseded. Invalidate the old key's cache entry so the
        // next inbound signature with the old key IRI triggers a refetch (which will find the new key
        // or a 404). This is the read-side of key rotation: Iris does not store the mapping, it just
        // ensures stale entries don't outlive the rotation.
        if (publicKey.TryGetProperty("replaces", out var replacesEl)
            && replacesEl.ValueKind == JsonValueKind.String
            && replacesEl.GetString() is { Length: > 0 } replacedIri
            && Iri.TryParse(replacedIri, out var replacedKeyIri))
        {
            _remoteKeys.Invalidate(replacedKeyIri);
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

            // Ed25519 is not an AsymmetricAlgorithm; load it with the dedicated type. The
            // ownerActorIri keyId is irrelevant here (only the JWK is kept), but a placeholder
            // satisfies the parameter.
            if (pemAlgorithm.Value == KeyAlgorithm.Ed25519)
            {
                if (!Iri.TryParse("urn:placeholder", out var placeholder) || Ed25519Key.FromPem(pem, placeholder) is not { } edKey)
                {
                    return null;
                }

                return new JwkKey(edKey.GetPublicJwk(), Signatures.AlgorithmLabel(pemAlgorithm.Value));
            }

            using var key = KeyPair.FromPem(pem, pemAlgorithm.Value, ownerActorIri);
            return new JwkKey(key.GetPublicJwk(), Signatures.AlgorithmLabel(pemAlgorithm.Value));
        }

        // The publicKey object is present but is neither a recognizable JWK (kty) nor a usable
        // publicKeyPem. Log at Warning so the "could not resolve public key" downstream is
        // attributable to an unsupported/malformed key format (with the raw shape for diagnosis)
        // rather than a fetch failure.
        _logger.LogWarning(
            "Key resolution: remote actor document {ActorIri} (keyId {KeyId}) has a publicKey that is " +
            "neither a recognizable JWK nor a usable publicKeyPem (raw: {PublicKeyRaw}); inbound " +
            "signatures from this actor will be rejected.",
            ownerActorIri, keyId, publicKey.GetRawText());
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
    /// <param name="kty">The JWK key type (<c>"RSA"</c>, <c>"EC"</c>, or <c>"OKP"</c>).</param>
    /// <returns>The algorithm, or null when the <c>kty</c> is not supported.</returns>
    private static KeyAlgorithm? AlgorithmFromKty(string? kty) => kty switch
    {
        "RSA" => KeyAlgorithm.Rsa,
        "EC" => KeyAlgorithm.EcP256,
        "OKP" => KeyAlgorithm.Ed25519,
        _ => null,
    };

    /// <summary>
    /// Determines the <see cref="KeyAlgorithm"/> of a public key PEM. Accepts PKIX
    /// (<c>-----BEGIN PUBLIC KEY-----</c>) and PKCS#1 (<c>-----BEGIN RSA PUBLIC KEY-----</c>, the raw
    /// RSA form some real-world servers serve in <c>publicKeyPem</c>) for RSA, and PKIX for EC P-256.
    /// </summary>
    /// <param name="pem">The PEM-encoded public key.</param>
    /// <returns>The algorithm, or null when the PEM cannot be parsed as a supported public key.</returns>
    private static KeyAlgorithm? AlgorithmFromPem(string pem)
    {
        // The PKCS#1 header names the algorithm explicitly; it needs no import to classify.
        if (pem.Contains("RSA PUBLIC KEY", StringComparison.Ordinal))
        {
            return KeyAlgorithm.Rsa;
        }

        // The PKIX envelope names the Ed25519 AlgorithmIdentifier, so FromPem succeeds only for
        // Ed25519 public keys. Pleroma signs with Ed25519, so check it up front. The keyId is
        // irrelevant here (we only classify the PEM); a placeholder satisfies the parameter. An
        // EC/RSA PEM makes FromPem throw FormatException (the envelope does not carry a 32-byte
        // Ed25519 key) — treat that as "not Ed25519" and fall through to EC/RSA.
        try
        {
            if (Iri.TryParse("urn:placeholder", out var placeholder) && Ed25519Key.FromPem(pem, placeholder) is not null)
            {
                return KeyAlgorithm.Ed25519;
            }
        }
        catch (FormatException)
        {
            // Not an Ed25519 public key; fall through to EC.
        }

        try
        {
            // ImportFromPem on an ECDSA key with the P-256 curve set succeeds only for EC public
            // keys; an RSA public key fails to import into an ECDSA instance and vice versa. Try EC
            // next (the Iris historical default), then RSA.
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

        foreach (KeyAlgorithm algorithm in new[] { KeyAlgorithm.Rsa, KeyAlgorithm.EcP256, KeyAlgorithm.Ed25519 })
        {
            if (string.Equals(Signatures.AlgorithmLabel(algorithm), label, StringComparison.OrdinalIgnoreCase))
            {
                return algorithm;
            }
        }

        return null;
    }
}
