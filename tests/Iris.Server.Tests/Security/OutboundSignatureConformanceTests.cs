using System.Net;
using Iris.Client;
using Iris.Core;
using Iris.Server;
using KristofferStrube.ActivityStreams;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Iris.Server.Tests.Security;

/// <summary>
/// Phase 12 Slice 12.6 — outbound signature conformance (the <c>ServerToServer</c> profile, C-03 /
/// draft-cavage-03). The server's outbound <see cref="DeliveryWorker"/> signs every body-carrying delivery
/// with the <c>ServerToServer</c> profile. These tests capture a real signed outbound delivery (the
/// worker pumps the queue over a capturing transport) and assert the spec-required parts of the signature:
/// </summary>
/// <list type="bullet">
/// <item>A body-carrying delivery's <c>Signature</c> header lists <c>digest</c> and <c>content-type</c> in
/// its signed header list (C-03 — the outbound signature base <em>must</em> cover the body's
/// <c>content-type</c>; the inbound verifier is lenient, but a strict peer's signature verifies only if
/// the base is complete).</item>
/// <item>The signature <c>algorithm</c> is the draft-cavage label for the key type.</item>
/// <item>The signed request round-trips through <see cref="HttpSignatureVerifier"/> (the signature is
/// cryptographically valid for the body + headers actually sent).</item>
/// <item>A bodyless GET uses the <c>ClientToServer</c> profile (no <c>digest</c>/<c>content-type</c>).</item>
/// </list>
/// <remarks>
/// The unit-level signature-base tests (in <c>Iris.Core.Tests</c>: <c>SignaturesTests</c> /
/// <c>HttpSignatureTests</c>) pin the base builder; these prove the <em>worker's</em> end-to-end path
/// (serialize → sign → send) emits a conformant signature.
/// </remarks>
public sealed class OutboundSignatureConformanceTests : IDisposable
{
    private const string InstanceHost = "sig.example";
    private const string Handle = "alice";
    private static readonly Iri ActorIri = new($"https://{InstanceHost}/ap/v1/u/{Handle}");
    private static readonly Iri InboxIri = ActorIri.InboxOf();

    private readonly InMemoryKeyStore _keyStore;
    private readonly KeyPair _key;
    private readonly IActivityPubClientFactory _factory;
    private readonly InMemoryDeliveryQueue _queue;
    private readonly CapturingHandler _capture;
    private readonly IHost _host;

    public OutboundSignatureConformanceTests()
    {
        _key = KeyPairGenerator.GenerateRsa(new Iri($"https://{InstanceHost}/ap/v1/u/{Handle}/#main-key"));
        _keyStore = new InMemoryKeyStore();
        _keyStore.PutKey(_key);
        var keyProvider = new InMemoryKeyProvider(_keyStore);
        keyProvider.RegisterKey(ActorIri, _key.KeyId);
        var signer = new HttpSignatureSigner(_keyStore);
        _factory = new ActivityPubClientFactory(_keyStore, keyProvider, signer);

        _capture = new CapturingHandler();
        var options = Options.Create(new ActivityPubServerOptions { InstanceActorId = ActorIri });
        _queue = new InMemoryDeliveryQueue();
        ILoggerFactory loggerFactory = NullLoggerFactory.Instance;
        var worker = new DeliveryWorker(
            _queue, _factory, () => _capture, options,
            loggerFactory.CreateLogger<DeliveryWorker>());

        _host = Host.CreateDefaultBuilder()
            .ConfigureLogging(l => l.ClearProviders())
            .ConfigureServices(s => s.AddHostedService<DeliveryWorker>(_ => worker))
            .Build();
        _host.StartAsync(CancellationToken.None).GetAwaiter().GetResult();
    }

    public void Dispose()
    {
        _host.StopAsync(CancellationToken.None).GetAwaiter().GetResult();
        _host.Dispose();
        _keyStore.Dispose();
        _key.Dispose();
    }

    // --- The ServerToServer profile covers digest + content-type (C-03) -------------

    [Fact]
    public async Task OutboundDelivery_SignsBody_WithServerToServerProfile_CoversDigestAndContentType()
    {
        var activity = BuildFollow();
        await _queue.EnqueueAsync(new DeliveryJob(InboxIri, activity, ActorIri), CancellationToken.None);

        await WaitForAsync(() => _capture.LastRequest is not null, timeout: TimeSpan.FromSeconds(5));

        var request = _capture.LastRequest;
        Assert.NotNull(request);

        // The request carries a body and an ActivityStreams content type (the worker serializes the
        // activity as application/activity+json).
        Assert.NotNull(request.Content);
        var contentType = request.Content!.Headers.ContentType;
        Assert.NotNull(contentType);
        Assert.Equal("application/activity+json", contentType!.MediaType);
        Assert.True(
            request.Content.Headers.TryGetValues(Signatures.DigestHeaderName, out _),
            "the signed delivery must carry a Digest header (the body-binding component of ServerToServer)");

        // The Signature header is present and parses.
        var signatureHeader = request.Headers.TryGetValues(Signatures.SignatureHeaderName, out var values)
            ? values.FirstOrDefault()
            : null;
        Assert.NotNull(signatureHeader);
        Assert.True(SignatureHeader.TryParse(signatureHeader, out var parsed),
            "the Signature header must be parseable");
        Assert.NotNull(parsed);

        // C-03: the signed header list (the signature base's components) covers digest + content-type —
        // the two body-binding components of the ServerToServer profile.
        var signedHeaders = SplitHeaders(parsed!.Headers);
        Assert.Contains("digest", signedHeaders);
        Assert.Contains("content-type", signedHeaders);

        // The algorithm is the draft-cavage label for the key type (RSA here).
        Assert.Equal(Signatures.AlgorithmLabel(_key.Algorithm), parsed.Algorithm);
    }

    // --- The signed request verifies against the public key (round-trip) ------------

    [Fact]
    public async Task OutboundDelivery_Signature_VerifiesAgainstPublicKey()
    {
        var activity = BuildFollow();
        await _queue.EnqueueAsync(new DeliveryJob(InboxIri, activity, ActorIri), CancellationToken.None);

        await WaitForAsync(() => _capture.LastRequest is not null, timeout: TimeSpan.FromSeconds(5));

        var request = _capture.LastRequest;
        Assert.NotNull(request);
        var signatureHeader = Assert.Single(request.Headers.GetValues(Signatures.SignatureHeaderName));

        // Rebuild the request metadata from the captured (signed) request and verify the signature
        // cryptographically against the actor's public key.
        var body = await request.Content!.ReadAsByteArrayAsync();
        var uri = request.RequestUri!;
        var date = request.Headers.TryGetValues(Signatures.DateHeaderName, out var dateValues)
            ? dateValues.FirstOrDefault()
            : null;
        Assert.NotNull(date);
        var metadata = new HttpRequestMetadata(
            request.Method.Method.ToUpperInvariant(),
            uri.PathAndQuery,
            string.IsNullOrEmpty(request.Headers.Host) ? uri.Authority : request.Headers.Host.ToString(),
            date!,
            request.Content.Headers.ContentType?.MediaType,
            body,
            HeaderDictionary(request));

        var verifier = new HttpSignatureVerifier(_keyStore);
        Assert.True(verifier.Verify(metadata, signatureHeader),
            "the outbound signature must verify against the actor's public key");
    }

    // --- A GET (bodyless) uses the ClientToServer profile (no digest/content-type) --

    [Fact]
    public async Task OutboundGet_Signs_ClientToServerProfile_NoDigestOrContentType()
    {
        using var client = _factory.Create(
            new ActivityPubClientOptions { ActorId = ActorIri, EnableRetry = false }, _capture);
        await client.SendAsync(new HttpRequestMessage(HttpMethod.Get, new Uri(InboxIri.Value)), CancellationToken.None);

        var request = _capture.LastRequest;
        Assert.NotNull(request);
        var signatureHeader = Assert.Single(request.Headers.GetValues(Signatures.SignatureHeaderName));
        Assert.True(SignatureHeader.TryParse(signatureHeader, out var parsed));
        Assert.NotNull(parsed);

        // A bodyless GET signs only (request-target) host date — no digest, no content-type.
        var signedHeaders = SplitHeaders(parsed!.Headers);
        Assert.DoesNotContain("digest", signedHeaders);
        Assert.DoesNotContain("content-type", signedHeaders);
    }

    // --- Helpers --------------------------------------------------------------------

    private static Follow BuildFollow() => new()
    {
        Id = $"https://{InstanceHost}/activities/follow-{Guid.NewGuid():N}",
        Actor = [new Link { Href = new Uri(ActorIri.Value) }],
        Object = [new Link { Href = new Uri(ActorIri.Value) }],
    };

    private static IReadOnlyList<string> SplitHeaders(string headerList)
        => headerList.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static IReadOnlyDictionary<string, string> HeaderDictionary(HttpRequestMessage request)
    {
        // Mirror the signer's metadata: request headers plus the body's content headers (the digest and
        // content-type the ServerToServer profile signs live on the content, not the request).
        var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, values) in request.Headers)
        {
            dict[name] = string.Join(", ", values);
        }

        if (request.Content is { } content)
        {
            foreach (var (name, values) in content.Headers)
            {
                dict[name] = string.Join(", ", values);
            }
        }

        return dict;
    }

    private static async Task WaitForAsync(Func<bool> probe, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (probe())
            {
                return;
            }

            await Task.Delay(25);
        }
    }

    /// <summary>
    /// Records the last outbound request (after the <see cref="SigningHandler"/> has signed it) and returns
    /// a canned 202 so the <see cref="DeliveryWorker"/> treats the delivery as successful.
    /// </summary>
    private sealed class CapturingHandler : HttpMessageHandler
    {
        private readonly object _gate = new();
        private HttpRequestMessage? _last;

        public HttpRequestMessage? LastRequest
        {
            get
            {
                lock (_gate)
                {
                    return _last is { } r ? Clone(r) : null;
                }
            }
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            lock (_gate)
            {
                _last = Clone(request);
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Accepted)
            {
                Content = new StringContent("accepted"),
            });
        }

        private static HttpRequestMessage Clone(HttpRequestMessage request)
        {
            var clone = new HttpRequestMessage(request.Method, request.RequestUri)
            {
                Version = request.Version,
            };
            foreach (var header in request.Headers)
            {
                clone.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }

            if (request.Content is { } content)
            {
                var body = content.ReadAsByteArrayAsync().GetAwaiter().GetResult();
                clone.Content = new ByteArrayContent(body);
                foreach (var header in content.Headers)
                {
                    clone.Content.Headers.TryAddWithoutValidation(header.Key, header.Value);
                }
            }

            return clone;
        }
    }
}
