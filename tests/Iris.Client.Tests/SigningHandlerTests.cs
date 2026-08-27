using System.Net;
using System.Text;
using Iris.Core;

namespace Iris.Client.Tests;

/// <summary>
/// Unit tests for <see cref="Iris.Client.SigningHandler"/>: it adds the <c>Date</c> and
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
}
