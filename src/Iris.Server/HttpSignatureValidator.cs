using System.Text.Json;
using Iris.Core;
using Microsoft.AspNetCore.Http;

namespace Iris.Server;

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
/// </remarks>
public sealed class HttpSignatureValidator(
    IInboundKeyResolver keyResolver,
    ISignatureVerifier verifier) : ISignatureValidator
{
    private readonly IInboundKeyResolver _keyResolver = keyResolver
        ?? throw new ArgumentNullException(nameof(keyResolver));
    private readonly ISignatureVerifier _verifier = verifier
        ?? throw new ArgumentNullException(nameof(verifier));

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
            return new SignatureValidationResult(false, default, ExtractActorIri(body));
        }

        if (!Iri.TryParse(header.KeyId, out var keyId))
        {
            return new SignatureValidationResult(false, default, ExtractActorIri(body));
        }

        // Resolve the remote public key. A null result (unknown actor / missing publicKey / fetch
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
            return new SignatureValidationResult(false, keyId, ExtractActorIri(body));
        }

        // Verify with the resolved key directly: the key came from a remote source (the sender's
        // actor document), not from this instance's key store, so it is passed to the verifier
        // explicitly rather than looked up by IRI. Dispose the key only when it is disposable
        // (a KeyPair is; an Ed25519Key is not — BouncyCastle params are not IDisposable).
        var disposableKey = key as IDisposable;
        try
        {
            return new SignatureValidationResult(_verifier.Verify(metadata, key, signatureHeader), keyId, ExtractActorIri(body));
        }
        finally
        {
            disposableKey?.Dispose();
        }
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
                 })
        {
            if (request.Headers.TryGetValue(headerName, out var value))
            {
                headers[headerName] = value.ToString();
            }
        }

        // The Date field of the metadata must be the raw wire value (BuildSignatureBase uses it for
        // the date component); fall back to an empty string when the header is absent.
        var date = headers.TryGetValue(Signatures.DateHeaderName, out var rawDate) ? rawDate : "";

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
