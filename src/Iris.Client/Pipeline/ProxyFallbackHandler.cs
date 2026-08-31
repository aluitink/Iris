using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Iris.Core;

namespace Iris.Client.Pipeline;

/// <summary>
/// A <see cref="DelegatingHandler"/> that implements Phase 6's proxy fallback: when a request to a
/// remote ActivityPub instance is rejected with 401/403 (the remote instance cannot validate the
/// client's signature — the browser has no signed outbound, or the actor's key is not resolvable
/// cross-origin), the handler retries the request through the home instance's proxy endpoint
/// (<c>POST {proxyBase}/ap/v1/proxy/{target}</c>) with Basic auth. The proxy identifies the actor
/// from the credentials and signs the forwarded request with the actor's key, which the remote
/// instance can validate.
/// </summary>
/// <remarks>
/// The handler is the outermost stage of the client pipeline (it wraps the whole
/// <see cref="RetryHandler"/> → <see cref="JsonLdHandler"/> → <see cref="SigningHandler"/> chain).
/// The direct attempt is fully signed (the <see cref="SigningHandler"/> runs inside); when it is
/// rejected 401/403 the handler strips the direct-attempt <c>Signature</c> (the proxy will re-sign)
/// and forwards the request to the proxy as a <c>POST</c> with a Basic-auth
/// <c>Authorization</c> header. The proxy relays the remote response, which the handler returns.
///
/// Only 401/403 on the <em>direct</em> attempt trigger a fallback; a successful direct response is
/// returned unchanged. A proxy failure (the proxy itself returns 401/403/5xx) is returned as-is —
/// the handler does not loop.
/// </remarks>
public sealed class ProxyFallbackHandler : DelegatingHandler
{
    private readonly Iri _proxyBase;
    private readonly ProxyCredentials _credentials;
    private readonly bool _alwaysProxy;

    /// <summary>
    /// Initializes a new <see cref="ProxyFallbackHandler"/> over an explicit inner handler.
    /// </summary>
    /// <param name="proxyBase">The home instance that hosts the proxy endpoint (its base IRI, e.g.
    /// <c>https://a.example</c>). Must be non-null and a valid IRI.</param>
    /// <param name="credentials">The Basic-auth credentials to send to the proxy (the acting
    /// actor's username + password).</param>
    /// <param name="innerHandler">The inner handler (the signed client pipeline) to forward the
    /// direct attempt to.</param>
    public ProxyFallbackHandler(Iri proxyBase, ProxyCredentials credentials, HttpMessageHandler innerHandler)
        : this(proxyBase, credentials, innerHandler, alwaysProxy: false)
    {
    }

    /// <summary>
    /// Initializes a new <see cref="ProxyFallbackHandler"/> over an explicit inner handler.
    /// </summary>
    /// <param name="proxyBase">The home instance that hosts the proxy endpoint (its base IRI).</param>
    /// <param name="credentials">The Basic-auth credentials to send to the proxy.</param>
    /// <param name="innerHandler">The inner handler (the signed client pipeline).</param>
    /// <param name="alwaysProxy">
    /// When <see langword="true"/> every request is routed through the proxy <em>without</em> a direct
    /// attempt (the browser's WebCrypto signature cannot be validated against a remote/advertised
    /// host, so a direct attempt would always 401). When <see langword="false"/> (the default) a
    /// direct attempt is made first and the proxy is used only on a 401/403.
    /// </param>
    public ProxyFallbackHandler(
        Iri proxyBase, ProxyCredentials credentials, HttpMessageHandler innerHandler, bool alwaysProxy)
    {
        ArgumentNullException.ThrowIfNull(innerHandler);
        _proxyBase = proxyBase;
        _credentials = credentials ?? throw new ArgumentNullException(nameof(credentials));
        InnerHandler = innerHandler;
        _alwaysProxy = alwaysProxy;
    }

    /// <inheritdoc/>
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        // Always-proxy mode: the browser's signature cannot be validated against the target host
        // (e.g. the actor's advertised host differs from the dial host), so a direct attempt would
        // always 401 — go straight to the proxy (which re-signs with the actor's key). This applies
        // only to signed *writes* (POST/PUT to an outbox/inbox); reads (GET) are public and dial the
        // instance directly (the proxy is not a general-purpose GET relay and is not CORS-open to it).
        if (_alwaysProxy && (request.Method == HttpMethod.Post || request.Method == HttpMethod.Put))
        {
            request.Headers.Remove(Signatures.SignatureHeaderName);
            request.Headers.Remove(Signatures.DateHeaderName);
            return await SendViaProxyAsync(request, ct).ConfigureAwait(false);
        }

        // 1. The direct attempt (the inner signed pipeline).
        var direct = await base.SendAsync(request, ct).ConfigureAwait(false);

        // 2. Only a 401/403 on the direct attempt triggers a fallback. Any other outcome (success,
        // 404, 5xx, ...) is the final answer.
        if (direct.StatusCode is not (HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden))
        {
            return direct;
        }

        // The direct attempt's signature is for the direct (request-target); it cannot be reused for
        // the proxy POST. The proxy re-signs the forwarded request, so strip it before forwarding.
        direct.Headers.Remove(Signatures.SignatureHeaderName);
        direct.Headers.Remove(Signatures.DateHeaderName);
        direct.Dispose();

        // 3. Route through the proxy.
        return await SendViaProxyAsync(request, ct).ConfigureAwait(false);
    }

    private async Task<HttpResponseMessage> SendViaProxyAsync(HttpRequestMessage request, CancellationToken ct)
    {
        // Build the proxy request: POST {proxyBase}/ap/v1/proxy/{target} with Basic auth. The
        // target is the original request's absolute IRI; the proxy reconstructs the signed request
        // from it (relaying the client's Accept) and forwards it to the target.
        var target = request.RequestUri ?? throw new InvalidOperationException("Request URI is not set.");
        var proxyUri = new Uri(
            $"{_proxyBase.Value.TrimEnd('/')}/ap/v1/proxy/{target}");

        using var proxyRequest = new HttpRequestMessage(HttpMethod.Post, proxyUri);
        proxyRequest.Headers.Authorization = new AuthenticationHeaderValue(
            "Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{_credentials.Username}:{_credentials.Password}")));

        // The proxy transport is always a POST (the target IRI rides in the path); the X-Iris-Proxy-
        // Method header carries the REAL method of the request the client wants made, so the proxy
        // forwards a POST for a Create (a GET would only list the outbox, never create).
        proxyRequest.Headers.TryAddWithoutValidation("X-Iris-Proxy-Method", request.Method.Method);

        // Relay the client's Accept header (content negotiation) so the proxy forwards it to the
        // target. The original request's Signature/Date are not copied (the proxy re-signs).
        foreach (var value in request.Headers.Accept)
        {
            proxyRequest.Headers.Accept.Add(value);
        }

        // Relay the original request's body (the ActivityPub activity for a Create) so the proxy can
        // forward it to the target. Without this the proxied write is a bodyless no-op: the target
        // outbox returns its listing (200) instead of creating the activity. The content type
        // defaults to the ActivityPub JSON-LD media type (the client always sends it).
        if (request.Content is not null)
        {
            proxyRequest.Content = new ByteArrayContent(await request.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false));
            proxyRequest.Content.Headers.ContentType = new(
                request.Content.Headers.ContentType?.MediaType ?? ActivityJson.ActivityJsonContentType);
        }

        return await base.SendAsync(proxyRequest, ct).ConfigureAwait(false);
    }
}
