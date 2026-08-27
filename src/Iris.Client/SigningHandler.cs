using System.Net.Http.Headers;
using System.Text;
using Iris.Core;

namespace Iris.Client;

/// <summary>
/// A <see cref="DelegatingHandler"/> that signs outgoing ActivityPub requests with the
/// <see cref="SigningProfile.ClientToServer"/> profile, setting the <c>Date</c> and
/// <c>Signature</c> headers.
/// </summary>
/// <remarks>
/// The handler is the <c>Iris.Client</c> boundary that maps a <see cref="HttpRequestMessage"/>
/// onto the <c>Iris.Core</c> <see cref="HttpRequestMetadata"/> snapshot, asks the
/// <see cref="ISignatureSigner"/> for the <c>Signature</c> header, and writes both headers back
/// onto the request. For requests with a body (POSTs), the <see cref="SigningProfile.ServerToServer"/>
/// profile is used so the <c>digest</c> and <c>content-type</c> are covered; for bodyless
/// requests (GETs) the <see cref="SigningProfile.ClientToServer"/> profile is used.
/// </remarks>
public sealed class SigningHandler : DelegatingHandler
{
    private readonly ISignatureSigner _signer;
    private readonly IKeyProvider _keyProvider;

    /// <summary>
    /// Initializes a new <see cref="SigningHandler"/>.
    /// </summary>
    /// <param name="signer">The signer that produces the <c>Signature</c> header.</param>
    /// <param name="keyProvider">Resolves the signing identity for a given actor.</param>
    public SigningHandler(ISignatureSigner signer, IKeyProvider keyProvider)
        : this(signer, keyProvider, null)
    {
    }

    /// <summary>
    /// Initializes a new <see cref="SigningHandler"/> with an explicit inner handler.
    /// </summary>
    /// <param name="signer">The signer that produces the <c>Signature</c> header.</param>
    /// <param name="keyProvider">Resolves the signing identity for a given actor.</param>
    /// <param name="innerHandler">The inner handler to forward to, or null to use the default
    /// <see cref="HttpClientHandler"/>.</param>
    public SigningHandler(ISignatureSigner signer, IKeyProvider keyProvider, HttpMessageHandler? innerHandler)
    {
        _signer = signer ?? throw new ArgumentNullException(nameof(signer));
        _keyProvider = keyProvider ?? throw new ArgumentNullException(nameof(keyProvider));
        InnerHandler = innerHandler ?? new HttpClientHandler();
    }

    /// <summary>
    /// The actor IRI to sign as. Must be set before sending a request (e.g. via
    /// <see cref="ActivityPubClientOptions"/> or directly).
    /// </summary>
    public Iri ActorId { get; set; }

    /// <inheritdoc/>
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        var identity = ResolveIdentity(request);
        var body = await ReadBodyAsync(request, ct).ConfigureAwait(false);
        var metadata = ToMetadata(request, body);
        var signature = _signer.Sign(metadata, identity, ProfileFor(body.Length > 0));

        request.Headers.TryAddWithoutValidation(Signatures.DateHeaderName, metadata.Date);
        request.Headers.TryAddWithoutValidation(Signatures.SignatureHeaderName, signature);

        if (body.Length > 0)
        {
            // Replace the content with the exact bytes that were signed, and restore the
            // content-type + set the digest header so the receiving instance can reconstruct the
            // same signature base (the ServerToServer profile covers digest + content-type).
            var content = new ByteArrayContent(body);
            if (metadata.ContentType is not null)
            {
                content.Headers.ContentType = new MediaTypeHeaderValue(metadata.ContentType);
            }

            content.Headers.TryAddWithoutValidation(Signatures.DigestHeaderName, metadata.Headers[Signatures.DigestHeaderName]);
            request.Content = content;
        }

        return await base.SendAsync(request, ct).ConfigureAwait(false);
    }

    private IIdentity ResolveIdentity(HttpRequestMessage request)
    {
        // The actor to sign as: the ActorId set on the handler, or a "X-Iris-Actor" header override.
        var actorIri = request.Headers.TryGetValues("X-Iris-Actor", out var values)
            && values is { } v
            && v.FirstOrDefault() is { } actorValue
            ? new Iri(actorValue)
            : ActorId;

        if (!_keyProvider.TryGetIdentity(actorIri, out var identity) || identity is null)
        {
            throw new KeyNotFoundException($"No signing identity registered for actor '{actorIri}'.");
        }

        return identity;
    }

    private static async Task<byte[]> ReadBodyAsync(HttpRequestMessage request, CancellationToken ct)
    {
        if (request.Content is null)
        {
            return [];
        }

        return await request.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
    }

    private static SigningProfile ProfileFor(bool hasBody)
        => hasBody ? SigningProfile.ServerToServer : SigningProfile.ClientToServer;

    private static HttpRequestMetadata ToMetadata(HttpRequestMessage request, byte[] body)
    {
        var uri = request.RequestUri ?? throw new InvalidOperationException("Request URI is not set.");
        // Prefer an explicit Host header (it may differ from the URI host for virtual hosts /
        // SNI); otherwise derive it from the request URI.
        var host = request.Headers.Host?.ToString() ?? uri.Authority;
        var date = DateTime.UtcNow.ToString("R");
        var contentType = request.Content?.Headers.ContentType?.MediaType;

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [Signatures.HostHeaderName] = host,
            [Signatures.DateHeaderName] = date,
        };

        if (contentType is not null)
        {
            headers[Signatures.ContentTypeHeaderName] = contentType;
        }

        if (body.Length > 0)
        {
            headers[Signatures.DigestHeaderName] = Signatures.ComputeDigest(body);
        }

        return new HttpRequestMetadata(
            request.Method.Method.ToUpperInvariant(),
            uri.PathAndQuery,
            host,
            date,
            contentType,
            body,
            headers);
    }
}
