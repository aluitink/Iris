using System.Text.Json;
using Iris.Core;
using Iris.Core.Signing;
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

        // Bind the signature to the acting actor and note the activity type for diagnostics: the
        // body's `actor` (a POST) is the acting actor; `type` is the ActivityStreams activity type
        // (e.g. "Create", "Follow") so a rejection log shows WHAT was being delivered.
        var (actor, activityType) = ExtractActivityFields(body);

        // Parse the header to get the keyId; if it's malformed, the signature is invalid.
        if (!SignatureHeader.TryParse(signatureHeader, out var header) || header is null)
        {
            // A header the legacy draft-cavage-03 parser rejects may be the RFC 9421 (new) format,
            // in which the Signature header carries only a label + base64 signature (e.g.
            // "sig1=:<base64>:"), with the parameters (keyid, created, covered components) in a
            // separate Signature-Input header. Route those to the RFC 9421 verifier (Phase 157).
            if (context.Request.Headers.TryGetValue(Signatures.SignatureInputHeaderName, out var signatureInputValues)
                && SignatureInputHeader.TryParse(signatureInputValues.ToString(), out var signatureInput)
                && signatureInput is not null)
            {
                return await ValidateRfc9421Async(context, signatureInput, metadata, body.Length > 0, actor, activityType, ct)
                    .ConfigureAwait(false);
            }

            _logger.LogWarning(
                "Signature rejected: malformed Signature header on {Method} {Path} (activity type {ActivityType})",
                context.Request.Method,
                context.Request.Path,
                activityType);
            return new SignatureValidationResult(false, default, actor);
        }

        if (!Iri.TryParse(header.KeyId, out var keyId))
        {
            _logger.LogWarning(
                "Signature rejected: unparseable keyId '{KeyId}' on {Method} {Path} (activity type {ActivityType})",
                header.KeyId,
                context.Request.Method,
                context.Request.Path,
                activityType);
            return new SignatureValidationResult(false, default, actor);
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
                "Signature rejected: could not resolve public key for keyId {KeyId} on {Method} {Path} (activity type {ActivityType})",
                keyId,
                context.Request.Method,
                context.Request.Path,
                activityType);
            return new SignatureValidationResult(false, keyId, actor);
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
                "Signature rejected: cryptographic verification failed for keyId {KeyId} on {Method} {Path} (activity type {ActivityType})",
                keyId,
                context.Request.Method,
                context.Request.Path,
                activityType);
        }

        // For a request with no body (a signed GET read) the signer is the cryptographically-verified
        // key owner — the keyId with its #fragment removed (the ActivityPub keyId = actorIri#key-N
        // convention). Prefer the body actor when present, else fall back to the key owner so a signed
        // GET still carries an authenticated identity (the object-document handler uses it for the
        // per-requester iris:isLiked extension).
        return new SignatureValidationResult(isValid, keyId, actor ?? OwnerActorIriFromKeyId(keyId));
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
    /// Validates an RFC 9421 (new-format) signature: the <c>Signature</c> header carries only
    /// <c>label=:base64:</c> and the parameters live in the <c>Signature-Input</c> header. This is
    /// the format modern fediverse peers (Mastodon 4.5+, Pleroma 2024+, Misskey, GoToSocial) send.
    /// </summary>
    /// <param name="context">The HTTP context (provides the raw <c>Signature</c> header).</param>
    /// <param name="signatureInput">The parsed <c>Signature-Input</c> header.</param>
    /// <param name="metadata">The buffered request metadata snapshot.</param>
    /// <param name="hasBody">Whether the request carried a body (a POST delivery, as opposed to a signed GET read).</param>
    /// <param name="actor">The acting actor IRI extracted from the body (null when absent).</param>
    /// <param name="activityType">The ActivityStreams activity type extracted from the body (for diagnostics).</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>The validation result (null is not returned here — an RFC 9421 signature that cannot
    /// be validated is invalid).</returns>
    private async ValueTask<SignatureValidationResult> ValidateRfc9421Async(
        HttpContext context,
        SignatureInputHeader signatureInput,
        HttpRequestMetadata metadata,
        bool hasBody,
        Iri? actor,
        string? activityType,
        CancellationToken ct)
    {
        var signatureHeader = context.Request.Headers[Signatures.SignatureHeaderName].ToString();

        // Find the Signature-Input member that matches the Signature label.
        var (label, signatureB64, labelOk) = ExtractSignatureLabelAndValue(signatureHeader);
        if (!labelOk)
        {
            _logger.LogWarning(
                "Signature rejected: unparseable RFC 9421 Signature header on {Method} {Path} (activity type {ActivityType})",
                context.Request.Method,
                context.Request.Path,
                activityType);
            return new SignatureValidationResult(false, default, actor);
        }

        var member = signatureInput.GetMember(label);
        if (member is null || string.IsNullOrEmpty(member.KeyId))
        {
            _logger.LogWarning(
                "Signature rejected: RFC 9421 Signature-Input has no member for label '{Label}' (or no keyid) on {Method} {Path} (activity type {ActivityType})",
                label,
                context.Request.Method,
                context.Request.Path,
                activityType);
            return new SignatureValidationResult(false, default, actor);
        }

        if (!Iri.TryParse(member.KeyId, out var keyId))
        {
            _logger.LogWarning(
                "Signature rejected: unparseable keyId '{KeyId}' (RFC 9421) on {Method} {Path} (activity type {ActivityType})",
                member.KeyId,
                context.Request.Method,
                context.Request.Path,
                activityType);
            return new SignatureValidationResult(false, default, actor);
        }

        // A body-less request (a signed GET read) is only validated when the signer is a LOCAL actor,
        // for the same reason as the legacy path (a remote GET key-resolve would recurse).
        if (!hasBody && _persistence is not null)
        {
            var keyOwner = OwnerActorIriFromKeyId(keyId);
            if (!await _persistence.Actors.TryGetActorAsync(keyOwner, out _, ct).ConfigureAwait(false))
            {
                return new SignatureValidationResult(false, default, actor);
            }
        }

        ISigningKey? key = null;
        try
        {
            key = await _keyResolver.ResolveAsync(keyId, ct).ConfigureAwait(false);
        }
        catch (Exception)
        {
            key = null;
        }

        if (key is null)
        {
            _logger.LogWarning(
                "Signature rejected: could not resolve public key for keyId {KeyId} (RFC 9421) on {Method} {Path} (activity type {ActivityType})",
                keyId,
                context.Request.Method,
                context.Request.Path,
                activityType);
            return new SignatureValidationResult(false, keyId, actor);
        }

        var isValid = VerifyRfc9421AndDispose(key, metadata, member, signatureB64);

        // F-21 key-rotation invalidation (same policy as the legacy path): a verification failure
        // (as opposed to a missing key) signals a stale cached key. Invalidate + re-resolve once.
        if (!isValid && _keyCache is not null)
        {
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
                isValid = VerifyRfc9421AndDispose(key, metadata, member, signatureB64);
            }
        }

        if (!isValid)
        {
            _logger.LogWarning(
                "Signature rejected: RFC 9421 cryptographic verification failed for keyId {KeyId} on {Method} {Path} (activity type {ActivityType})",
                keyId,
                context.Request.Method,
                context.Request.Path,
                activityType);
        }

        return new SignatureValidationResult(isValid, keyId, actor ?? OwnerActorIriFromKeyId(keyId));
    }

    /// <summary>
    /// Verifies an RFC 9421 signature with the given key and disposes the key when it is disposable.
    /// </summary>
    private bool VerifyRfc9421AndDispose(ISigningKey key, HttpRequestMetadata metadata, SignatureInputMember member, string signatureB64)
    {
        var disposableKey = key as IDisposable;
        try
        {
            if (!TryDecodeBase64(signatureB64, out var signature))
            {
                return false;
            }

            byte[] baseBytes;
            try
            {
                baseBytes = SignatureBase9421.Build(metadata, member, member.MemberValue);
            }
            catch (ArgumentException)
            {
                // A covered component that cannot be derived (unknown derived component, missing
                // header) makes the signature invalid.
                return false;
            }

            return Signatures.VerifyBase(key, baseBytes, signature);
        }
        finally
        {
            disposableKey?.Dispose();
        }
    }

    /// <summary>
    /// Extracts the label and base64 signature value from an RFC 9421 <c>Signature</c> header of the
    /// form <c>label=:base64:</c>.
    /// </summary>
    private static (string Label, string Base64, bool Ok) ExtractSignatureLabelAndValue(string header)
    {
        var firstColon = header.IndexOf(':');
        if (firstColon <= 0)
        {
            return (default!, default!, false);
        }

        // The form is label=:base64: — the label is everything before the first ':', minus a
        // trailing '=' (the '=' is the separator between label and value in the RFC 9421 Signature
        // header, e.g. "sig1=:...").
        var label = header[..firstColon].Trim();
        if (label.EndsWith('='))
        {
            label = label[..^1].TrimEnd();
        }

        var rest = header[(firstColon + 1)..];
        // The value is :base64: — strip the leading ':' and trailing ':' (if present).
        if (rest.StartsWith(':'))
        {
            rest = rest[1..];
        }

        if (rest.EndsWith(':'))
        {
            rest = rest[..^1];
        }

        rest = rest.Trim();
        if (label.Length == 0 || rest.Length == 0)
        {
            return (default!, default!, false);
        }

        return (label, rest, true);
    }

    private static bool TryDecodeBase64(string value, out byte[] bytes)
    {
        try
        {
            bytes = Convert.FromBase64String(value);
            return true;
        }
        catch (FormatException)
        {
            bytes = [];
            return false;
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
    /// Extracts the <c>actor</c> IRI and the <c>type</c> from an ActivityStreams activity body, when
    /// present and parseable. The type is the ActivityStreams activity type string (e.g. "Create",
    /// "Follow") — logged on rejection so an operator can see WHAT was being delivered when a
    /// signature failed.
    /// </summary>
    /// <param name="body">The raw activity JSON body.</param>
    /// <returns>The actor IRI (null when unparseable) and the activity type (null when absent).</returns>
    private static (Iri? Actor, string? ActivityType) ExtractActivityFields(byte[] body)
    {
        if (body.Length == 0)
        {
            return (null, null);
        }

        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;

            Iri? actor = null;
            if (root.TryGetProperty("actor", out var actorProp)
                && TryGetActorIri(actorProp, out var iriValue)
                && iriValue is not null
                && Iri.TryParse(iriValue, out var iri))
            {
                actor = iri;
            }

            string? activityType = null;
            if (root.TryGetProperty("type", out var typeProp)
                && typeProp.ValueKind == JsonValueKind.String)
            {
                activityType = typeProp.GetString();
            }

            return (actor, activityType);
        }
        catch (JsonException)
        {
            // Not JSON: no actor binding.
        }

        return (null, null);
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
