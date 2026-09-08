using System.Text.Json;
using Iris.Core;
using Iris.Server.Stores;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Iris.Server.Security;

/// <summary>
/// The default <see cref="ISignatureValidator"/>. Reads the <c>Signature</c> header, resolves the
/// signing key via an <see cref="IInboundKeyResolver"/>, and verifies the signature via
/// <see cref="ISignatureVerifier"/> (accepting both signing profiles).
/// </summary>
/// <remarks>
/// The request body is buffered (so downstream handlers can re-read it) and, for requests that carry a
/// body, the body's <c>actor</c> field is extracted to bind the signature to the acting actor. The
/// binding is advisory: the result carries <see cref="SignatureValidationResult.ActorIri"/> when the
/// body's <c>actor</c> is present and parseable; a missing/unparseable actor does not, by itself, fail
/// validation (the cryptographic check is authoritative).
/// <para>
/// F-21 key-rotation invalidation: when a <paramref name="remoteKeyCache"/> is supplied and a
/// verification <em>fails</em> (distinct from a missing key), the cached key for the signing
/// <c>keyId</c> is considered stale — the remote actor rotated its key but kept the same key IRI —
/// and the entry is invalidated and the key re-resolved once (a fresh fetch of the actor document)
/// before re-verifying. Because the key is re-resolved by re-fetching the actor document, the owning
/// actor's entry in the <see cref="RemoteActorCache"/> is invalidated too (otherwise the re-resolve
/// would re-read the stale document and re-derive the old key, defeating the rotation). This closes
/// the window in which a rotated remote key would otherwise be served stale until the caches' TTL
/// (1h). A missing key (no resolvable public key) is not treated as a rotation signal, so no
/// invalidation occurs in that case.
/// </para>
/// </remarks>
public sealed class HttpSignatureValidator(
    IInboundKeyResolver keyResolver,
    ISignatureVerifier verifier,
    RemoteKeyCache? remoteKeyCache = null,
    RemoteActorCache? remoteActorCache = null,
    ILogger<HttpSignatureValidator>? logger = null,
    IPersistenceProvider? persistence = null) : ISignatureValidator
{
    private readonly IInboundKeyResolver _keyResolver = keyResolver
        ?? throw new ArgumentNullException(nameof(keyResolver));
    private readonly ISignatureVerifier _verifier = verifier
        ?? throw new ArgumentNullException(nameof(verifier));
    private readonly RemoteKeyCache? _keyCache = remoteKeyCache;
    private readonly RemoteActorCache? _actorCache = remoteActorCache;
    private readonly ILogger<HttpSignatureValidator> _logger = logger ?? NullLogger<HttpSignatureValidator>.Instance;
    private readonly IPersistenceProvider? _persistence = persistence;

    /// <inheritdoc/>
    public async ValueTask<SignatureValidationResult?> ValidateAsync(HttpContext context, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (!context.Request.Headers.TryGetValue(Signatures.SignatureHeaderName, out var signatureValues))
        {
            // Unsigned request: the caller decides the policy.
            return null;
        }

        var signatureHeader = signatureValues.ToString();

        // Buffer the body so downstream handlers (the inbox processor) can re-read it.
        context.Request.EnableBuffering();
        byte[] body;
        await using (var bodyStream = new MemoryStream())
        {
            await context.Request.Body.CopyToAsync(bodyStream, ct).ConfigureAwait(false);
            body = bodyStream.ToArray();
        }

        var metadata = ToMetadata(context, body);

        // Parse the header to get the keyId; if it's malformed, the signature is invalid.
        if (!SignatureHeader.TryParse(signatureHeader, out var header) || header is null)
        {
            _logger.LogWarning(
                "Signature rejected: malformed Signature header on {Method} {Path}",
                context.Request.Method,
                context.Request.Path);
            return new SignatureValidationResult(false, default, ExtractActorIri(body));
        }

        if (!Iri.TryParse(header.KeyId, out var keyId))
        {
            _logger.LogWarning(
                "Signature rejected: unparseable keyId '{KeyId}' on {Method} {Path}",
                header.KeyId,
                context.Request.Method,
                context.Request.Path);
            return new SignatureValidationResult(false, default, ExtractActorIri(body));
        }

        // A body-less request (a signed GET read) is only validated when the signer is a LOCAL actor.
        // Resolving a remote signer's key requires fetching its actor document (an outbound wire hop);
        // doing so for a GET would recurse — the fetch itself is a request that, on a federating peer,
        // triggers its own key resolution — and in a two-instance loop cascades into a stack overflow.
        // The only GETs that need a per-requester identity are the local UI's object reads (a local
        // user signing as themselves), and a local signer's key is resolvable without any wire hop. A
        // remote signer's GET (cross-instance read / the key-resolution bootstrap itself) is left
        // unvalidated (the caller treats it as anonymous — object reads are public), preserving the
        // pre-existing POST-only behavior for cross-instance reads.
        if (body.Length == 0 && _persistence is not null)
        {
            var keyOwner = OwnerActorIriFromKeyId(keyId);
            if (!await _persistence.Actors.TryGetActorAsync(keyOwner, out _, ct).ConfigureAwait(false))
            {
                // Remote signer on a GET: no local identity to bind; skip validation (anonymous read).
                return null;
            }
        }

        // Resolve the signing key. A null result (unknown actor / missing publicKey / fetch
        // failure) is an invalid signature, not an error.
        ISigningKey? key = null;
        try
        {
            key = await _keyResolver.ResolveAsync(keyId, ct).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // A resolver failure is an expected condition for validation purposes.
            key = null;
        }

        if (key is null)
        {
            _logger.LogWarning(
                "Signature rejected: could not resolve public key for keyId {KeyId} on {Method} {Path}",
                keyId,
                context.Request.Method,
                context.Request.Path);
            return new SignatureValidationResult(false, keyId, ExtractActorIri(body));
        }

        // Verify with the resolved key directly: the key came from a remote source (the sender's
        // actor document), not from this instance's key store, so it is passed to the verifier
        // explicitly rather than looked up by IRI.
        var isValid = VerifyAndDispose(key, metadata, signatureHeader);

        // F-21 key-rotation invalidation: a verification failure (as opposed to a missing key) is
        // the signal that the cached key is stale — the remote actor rotated its key but kept the
        // same key IRI, so the cache still holds the old public key. Invalidate the cached key for
        // this key IRI and re-resolve once (a fresh fetch of the actor document), then re-verify.
        // A missing key (key is null) is NOT a rotation signal: the actor simply has no resolvable
        // key, so no invalidation is attempted (there is nothing to invalidate and a re-fetch would
        // just repeat the same null).
        if (!isValid && _keyCache is not null)
        {
            // Invalidate BOTH the key cache (key IRI) and the actor-document cache (owner actor IRI):
            // the re-resolve re-derives the key by re-fetching the actor document, so a stale actor
            // document would re-derive the old key and defeat the rotation. The owner actor IRI is
            // the key IRI with any #fragment removed (the ActivityPub keyId = actorIri#key-N
            // convention).
            _keyCache.Invalidate(keyId);
            _actorCache?.Invalidate(OwnerActorIriFromKeyId(keyId));
            try
            {
                key = await _keyResolver.ResolveAsync(keyId, ct).ConfigureAwait(false);
            }
            catch (Exception)
            {
                key = null;
            }

            if (key is not null)
            {
                isValid = VerifyAndDispose(key, metadata, signatureHeader);
            }
        }

        if (!isValid)
        {
            _logger.LogWarning(
                "Signature rejected: cryptographic verification failed for keyId {KeyId} on {Method} {Path}",
                keyId,
                context.Request.Method,
                context.Request.Path);
        }

        // Bind the signature to the acting actor. The body's `actor` (a POST) is the acting actor;
        // for a request with no body (a signed GET read) the signer is the cryptographically-verified
        // key owner — the keyId with its #fragment removed (the ActivityPub keyId = actorIri#key-N
        // convention). Prefer the body actor when present, else fall back to the key owner so a signed
        // GET still carries an authenticated identity (the object-document handler uses it for the
        // per-requester iris:isLiked extension).
        var actor = ExtractActorIri(body) ?? OwnerActorIriFromKeyId(keyId);

        return new SignatureValidationResult(isValid, keyId, actor);
    }

    /// <summary>
    /// Verifies a signature with the given key and disposes the key when it is disposable.
    /// </summary>
    /// <param name="key">The resolved key.</param>
    /// <param name="metadata">The request metadata snapshot.</param>
    /// <param name="signatureHeader">The raw <c>Signature</c> header value.</param>
    /// <returns><see langword="true"/> when the signature is valid.</returns>
    private bool VerifyAndDispose(ISigningKey key, HttpRequestMetadata metadata, string signatureHeader)
    {
        // Dispose the key only when it is disposable (a KeyPair is; an Ed25519Key is not —
        // BouncyCastle params are not IDisposable).
        var disposableKey = key as IDisposable;
        try
        {
            return _verifier.Verify(metadata, key, signatureHeader);
        }
        finally
        {
            disposableKey?.Dispose();
        }
    }

    /// <summary>
    /// Derives the owner actor IRI from a key IRI by stripping the <c>#fragment</c> (the ActivityPub
    /// convention <c>keyId = actorIri + "#key-N"</c>). Used to invalidate the owning actor's document
    /// cache entry on a key rotation.
    /// </summary>
    /// <param name="keyId">The key IRI.</param>
    /// <returns>The owner actor IRI (the key IRI with any <c>#fragment</c> removed).</returns>
    private static Iri OwnerActorIriFromKeyId(Iri keyId)
    {
        var value = keyId.Value;
        var fragment = value.IndexOf('#');
        return fragment >= 0 ? new Iri(value[..fragment]) : keyId;
    }

    /// <summary>
    /// Builds the <see cref="HttpRequestMetadata"/> snapshot from an <see cref="HttpContext"/>.
    /// </summary>
    /// <param name="context">The HTTP context.</param>
    /// <param name="body">The buffered request body bytes.</param>
    /// <returns>The metadata snapshot.</returns>
    private static HttpRequestMetadata ToMetadata(HttpContext context, byte[] body)
    {
        var request = context.Request;
        var host = request.Headers.Host.ToString();
        var contentType = request.ContentType;

        // Collect the raw header values (case-insensitive) for the signature base. The verifier
        // reconstructs the base from the headers list declared in the Signature header, so it only
        // reads the ones it needs; include the common ones. In ASP.NET Core, Request.Headers is a
        // combined view of request + content headers, so digest (a content header) is read here too.
        // The values are the VERBATIM wire strings (critical: the signature base must contain the
        // exact bytes that were signed, so e.g. the Date component must be the raw header value,
        // not a re-formatted DateTimeOffset).
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var headerName in new[]
                  {
                      Signatures.HostHeaderName,
                      Signatures.DateHeaderName,
                      Signatures.ContentTypeHeaderName,
                      Signatures.DigestHeaderName,
                      Signatures.SignatureDateHeaderName,
                  })
        {
            if (request.Headers.TryGetValue(headerName, out var value))
            {
                headers[headerName] = value.ToString();
            }
        }

        // The date component must be the value the client SIGNED over, not necessarily the wire Date.
        // A browser (Blazor WASM) client cannot set the standard Date header (forbidden), so it
        // carries the signed value in X-Signature-Date; a non-browser client signs over its Date
        // header. ResolveDateComponent prefers X-Signature-Date, falling back to the wire Date —
        // exactly what the client's SigningHandler signs over, so the two never drift.
        var date = Signatures.ResolveDateComponent(headers);

        return new HttpRequestMetadata(
            request.Method,
            request.Path.ToString() + request.QueryString.ToString(),
            host,
            date,
            contentType,
            body,
            headers);
    }

    /// <summary>
    /// Extracts the <c>actor</c> IRI from an ActivityStreams activity body, when present and
    /// parseable.
    /// </summary>
    /// <param name="body">The raw activity JSON body.</param>
    /// <returns>The actor IRI, or null when the body has no parseable <c>actor</c> field.</returns>
    private static Iri? ExtractActorIri(byte[] body)
    {
        if (body.Length == 0)
        {
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("actor", out var actor)
                && TryGetActorIri(actor, out var iriValue)
                && iriValue is not null
                && Iri.TryParse(iriValue, out var iri))
            {
                return iri;
            }
        }
        catch (JsonException)
        {
            // Not JSON: no actor binding.
        }

        return null;
    }

    private static bool TryGetActorIri(JsonElement actor, out string? iri)
    {
        if (actor.ValueKind == JsonValueKind.String)
        {
            iri = actor.GetString();
            return true;
        }

        if (actor.ValueKind == JsonValueKind.Array && actor.GetArrayLength() > 0)
        {
            var first = actor[0];
            iri = first.ValueKind == JsonValueKind.String ? first.GetString() : null;
            return true;
        }

        iri = null;
        return false;
    }
}
