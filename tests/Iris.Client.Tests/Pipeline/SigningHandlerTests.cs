using System.Net;
using System.Text;
using Iris.Core;

namespace Iris.Client.Tests.Pipeline;

/// <summary>
/// Unit tests for <see cref="Iris.Client.Pipeline.SigningHandler"/>: it adds the <c>Date</c> and
/// <c>Signature</c> headers and produces a signature that <see cref="HttpSignatureVerifier"/>
/// can verify (for both the bodyless ClientToServer profile and the body-carrying
/// ServerToServer profile).
/// </summary>
public class SigningHandlerTests
{
    private static readonly Iri ActorA = new("https://a.domain.local/u/alice");
    private static readonly Iri KeyIdA = new("https://a.domain.local/u/alice#main-key");

    private static (InMemoryKeyStore Store, InMemoryKeyProvider Provider, SigningHandler Handler, FakeHttpHandler Fake) Build()
    {
        var store = new InMemoryKeyStore();
        var key = KeyPairGenerator.GenerateRsa(KeyIdA);
        store.PutKey(key);

        var provider = new InMemoryKeyProvider(store);
        provider.RegisterKey(ActorA, KeyIdA);

        var signer = new HttpSignatureSigner(store);
        var fake = new FakeHttpHandler(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("") });
        var handler = new SigningHandler(signer, provider, fake) { ActorId = ActorA };

        return (store, provider, handler, fake);
    }

    [Fact]
    public async Task GetRequest_AddsDateAndSignatureHeaders_ClientToServer()
    {
        var (_, _, handler, fake) = Build();
        using var client = new HttpClient(handler);

        await client.GetAsync("https://a.domain.local/u/alice");

        var sent = fake.LastRequest!;
        // The inner handler captured the request AFTER the SigningHandler ran.
        Assert.True(sent.Headers.Contains(Signatures.DateHeaderName), "Date header should be present");
        Assert.True(sent.Headers.Contains(Signatures.SignatureHeaderName), "Signature header should be present");

        // Verify the signature is valid for the ClientToServer profile.
        var signatureValue = sent.Headers.GetValues(Signatures.SignatureHeaderName).Single();
        Assert.True(SignatureHeader.TryParse(signatureValue, out var header));
        Assert.Equal("rsa-sha256", header!.Algorithm);
        Assert.Equal(KeyIdA.Value, header.KeyId);
    }

    [Fact]
    public async Task PostRequest_SignsBody_ServerToServerProfile_IncludesDigest()
    {
        var body = "{\"@context\":\"https://www.w3.org/ns/activitystreams\",\"type\":\"Follow\"}";
        var (_, _, handler, fake) = Build();
        using var client = new HttpClient(handler);

        using var request = new HttpRequestMessage(HttpMethod.Post, "https://b.domain.local/u/bob/inbox")
        {
            Content = new StringContent(body, Encoding.UTF8, "application/activity+json"),
        };
        await client.SendAsync(request);

        var sent = fake.LastRequest!;
        var signatureValue = sent.Headers.GetValues(Signatures.SignatureHeaderName).Single();
        Assert.True(SignatureHeader.TryParse(signatureValue, out var header));

        // ServerToServer profile covers digest + content-type.
        Assert.Contains("digest", header!.Headers, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("content-type", header.Headers, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Signature_IsVerifiable_ByVerifier()
    {
        // Build the store/key directly so we keep a reference for the verifier.
        using var store = new InMemoryKeyStore();
        using var key = KeyPairGenerator.GenerateRsa(KeyIdA);
        store.PutKey(key);
        var provider = new InMemoryKeyProvider(store);
        provider.RegisterKey(ActorA, KeyIdA);
        var signer = new HttpSignatureSigner(store);
        var fake = new FakeHttpHandler(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("") });
        using var handler = new SigningHandler(signer, provider, fake) { ActorId = ActorA };
        using var client = new HttpClient(handler);

        await client.GetAsync("https://a.domain.local/u/alice");

        var sent = fake.LastRequest!;

        // Reconstruct the metadata exactly as the signer saw it and verify. The signer derives
        // the host from the request URI when no explicit Host header is set.
        var date = sent.Headers.GetValues(Signatures.DateHeaderName).Single();
        var host = sent.RequestUri!.Authority;
        var path = sent.RequestUri!.PathAndQuery;
        var metadata = new HttpRequestMetadata(
            "GET", path, host, date, null, [],
            new Dictionary<string, string>
            {
                [Signatures.HostHeaderName] = host,
                [Signatures.DateHeaderName] = date,
            });

        var signatureValue = sent.Headers.GetValues(Signatures.SignatureHeaderName).Single();

        var verifier = new HttpSignatureVerifier(store);
        Assert.True(verifier.Verify(metadata, signatureValue));
    }

    [Fact]
    public async Task SigningHandler_SetsXSignatureDateMatchingSignedDate()
    {
        // The browser (a Blazor WASM host's fetch) overrides the standard Date header on the wire
        // (it is a forbidden header), so the client must carry the signed date value in the custom
        // X-Signature-Date header, which the verifier reads for the date component. X-Signature-Date
        // must equal the Date value the client actually signed over.
        var (_, _, handler, fake) = Build();
        using var client = new HttpClient(handler);

        await client.GetAsync("https://a.domain.local/u/alice");

        var sent = fake.LastRequest!;
        var date = sent.Headers.GetValues(Signatures.DateHeaderName).Single();
        var xSignatureDate = sent.Headers.GetValues(Signatures.SignatureDateHeaderName).Single();

        Assert.Equal(date, xSignatureDate);
    }

    [Fact]
    public async Task Signature_StillVerifies_WhenWireDateIsOverriddenByBrowser()
    {
        // Simulates the browser overriding the wire Date after the client signed over its own Date.
        // The client signed over date = X-Signature-Date; the wire Date is now different (the browser
        // stamped its own). The verifier must read the date component from X-Signature-Date (not the
        // wire Date), so the reconstructed base matches the signed base and verification succeeds.
        var (store, _, handler, fake) = Build();
        using var client = new HttpClient(handler);

        await client.GetAsync("https://a.domain.local/u/alice");

        var sent = fake.LastRequest!;
        var signedDate = sent.Headers.GetValues(Signatures.SignatureDateHeaderName).Single();
        var host = sent.RequestUri!.Authority;
        var path = sent.RequestUri!.PathAndQuery;
        var signatureValue = sent.Headers.GetValues(Signatures.SignatureHeaderName).Single();

        // The browser overrides the wire Date with a DIFFERENT value. The X-Signature-Date header
        // (non-forbidden) survives with the signed value. The verifier must use X-Signature-Date.
        var browserOverrideDate = "Thu, 01 Jan 1970 00:00:00 GMT";
        var metadata = new HttpRequestMetadata(
            "GET", path, host, browserOverrideDate, null, [],
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                [Signatures.HostHeaderName] = host,
                [Signatures.DateHeaderName] = browserOverrideDate,
                [Signatures.SignatureDateHeaderName] = signedDate,
            });

        // Build the base the same way the server's HttpSignatureValidator now does (resolve the date
        // component from X-Signature-Date ?? Date) and verify against it.
        var dateComponent = Signatures.ResolveDateComponent(metadata.Headers);
        Assert.Equal(signedDate, dateComponent);
        var verifyingMetadata = metadata.With(date: dateComponent);

        var verifier = new HttpSignatureVerifier(store);
        Assert.True(verifier.Verify(verifyingMetadata, signatureValue));
    }

    [Fact]
    public async Task UnknownActor_Throws_KeyNotFound()
    {
        var store = new InMemoryKeyStore();
        var provider = new InMemoryKeyProvider(store);
        var signer = new HttpSignatureSigner(store);
        var fake = new FakeHttpHandler(new HttpResponseMessage(HttpStatusCode.OK));
        using var handler = new SigningHandler(signer, provider, fake)
        {
            ActorId = new("https://a.domain.local/u/unknown"),
        };

        using var client = new HttpClient(handler);
        await Assert.ThrowsAsync<KeyNotFoundException>(() => client.GetAsync("https://a.domain.local/u/alice"));
    }

    [Fact]
    public async Task ResignedRequest_DoesNotStackSignatureHeaders()
    {
        // Regression: a request that already carries Signature/Date/X-Signature-Date headers (a re-used
        // or re-dispatched message, e.g. a retry clone) must NOT accumulate a second set of headers.
        // The SigningHandler must remove the pre-existing signature headers before adding the new ones,
        // preserving the "exactly one Signature header" invariant. Without this fix, the headers stack
        // and the receiving peer's validator comma-joins them into a malformed signature → 401.
        var (_, _, handler, fake) = Build();
        using var client = new HttpClient(handler);

        // Simulate a request that was already signed (e.g. by a previous pass through the pipeline).
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://a.domain.local/u/alice");
        request.Headers.TryAddWithoutValidation(Signatures.SignatureHeaderName, "stale-signature-value");
        request.Headers.TryAddWithoutValidation(Signatures.DateHeaderName, "stale-date-value");
        request.Headers.TryAddWithoutValidation(Signatures.SignatureDateHeaderName, "stale-date-value");

        await client.SendAsync(request);

        var sent = fake.LastRequest!;
        // Exactly one of each signature header — the stale values were removed and replaced.
        Assert.Single(sent.Headers.GetValues(Signatures.SignatureHeaderName));
        Assert.Single(sent.Headers.GetValues(Signatures.DateHeaderName));
        Assert.Single(sent.Headers.GetValues(Signatures.SignatureDateHeaderName));

        // The Signature header is the FRESH one (not the stale value).
        var signatureValue = sent.Headers.GetValues(Signatures.SignatureHeaderName).Single();
        Assert.NotEqual("stale-signature-value", signatureValue);
        Assert.True(SignatureHeader.TryParse(signatureValue, out var header));
        Assert.Equal("rsa-sha256", header!.Algorithm);
    }
}
