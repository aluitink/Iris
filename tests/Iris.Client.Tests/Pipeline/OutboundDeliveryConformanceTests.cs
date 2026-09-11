using System.Net;
using System.Text;
using Iris.Core;

namespace Iris.Client.Tests.Pipeline;

/// <summary>
/// End-to-end outbound delivery conformance tests for the full <see cref="Iris.Client.Pipeline.SigningHandler"/>
/// pipeline (Phase 82.2). These are the durable, offline mirror of the live verification that a real
/// non-permissive instance (mastodon.social, which rejects unsigned fetches with 401) accepts an
/// Iris-signed delivery: a strict peer (1) WebFinger-resolves the actor, (2) fetches the actor doc's
/// <c>publicKeyPem</c>, (3) reconstructs the signature base from the wire, and (4) verifies the
/// signature against the public key. These tests prove the exact request the pipeline emits satisfies
/// every step a strict peer performs — all required headers present (including the <c>created</c>
/// parameter added in 82.1), the body's <c>Digest</c> + <c>Content-Type</c> carried on the wire for
/// POSTs, and the signature verifiable by the public key alone.
/// </summary>
public class OutboundDeliveryConformanceTests
{
    private static readonly Iri Actor = new("https://iris.luit.ink/ap/v1/u/andrew");
    private static readonly Iri KeyId = new("https://iris.luit.ink/ap/v1/u/andrew#key-1");

    private static (InMemoryKeyStore Store, KeyPair Key, SigningHandler Handler, FakeHttpHandler Fake) Build()
    {
        var store = new InMemoryKeyStore();
        var key = KeyPairGenerator.GenerateRsa(KeyId);
        store.PutKey(key);

        var provider = new InMemoryKeyProvider(store);
        provider.RegisterKey(Actor, KeyId);

        var signer = new HttpSignatureSigner(store);
        var fake = new FakeHttpHandler(new HttpResponseMessage(HttpStatusCode.Accepted) { Content = new StringContent("") });
        var handler = new SigningHandler(signer, provider, fake) { ActorId = Actor };

        return (store, key, handler, fake);
    }

    [Fact]
    public async Task GetDelivery_HasAllHeadersStrictPeerChecks_CreatedPresent_SignatureVerifies()
    {
        var (store, _, handler, fake) = Build();
        using var client = new HttpClient(handler);

        await client.GetAsync("https://mastodon.social/users/gargron");

        var sent = fake.LastRequest!;

        // A strict peer requires Date + X-Signature-Date + Signature on the wire.
        Assert.True(sent.Headers.Contains(Signatures.DateHeaderName), "Date header present");
        Assert.True(sent.Headers.Contains(Signatures.SignatureDateHeaderName), "X-Signature-Date header present");
        Assert.True(sent.Headers.Contains(Signatures.SignatureHeaderName), "Signature header present");

        var signatureValue = sent.Headers.GetValues(Signatures.SignatureHeaderName).Single();
        Assert.True(SignatureHeader.TryParse(signatureValue, out var header));
        var h = header!;

        // The keyId is the actor's publicKey IRI (what the peer fetches to verify).
        Assert.Equal(KeyId.Value, h.KeyId);
        Assert.Equal("rsa-sha256", h.Algorithm);
        // ClientToServer profile (bodyless GET): (request-target) host date — no digest/content-type.
        Assert.Equal("(request-target) host date", h.Headers);
        // The created parameter (added in 82.1) is present and is a real epoch timestamp (not 0).
        Assert.True(h.Created > 0, "created must be a real epoch timestamp");

        // The signature must verify against the PUBLIC key alone (what the peer has, from the actor doc).
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
        Assert.True(new HttpSignatureVerifier(store).Verify(metadata, signatureValue));
    }

    [Fact]
    public async Task PostDelivery_CarriesDigestAndContentTypeOnWire_CreatedPresent_SignatureVerifies()
    {
        // A real Follow to a non-permissive instance's inbox (the exact shape of the live 82.2 test).
        var body = """{"@context":"https://www.w3.org/ns/activitystreams","id":"https://iris.luit.ink/ap/v1/follows/822-test","type":"Follow","actor":"https://iris.luit.ink/ap/v1/u/andrew","object":"https://mastodon.social/users/gargron"}""";
        var (store, _, handler, fake) = Build();
        using var client = new HttpClient(handler);

        using var request = new HttpRequestMessage(HttpMethod.Post, "https://mastodon.social/users/gargron/inbox")
        {
            Content = new StringContent(body, Encoding.UTF8, "application/activity+json"),
        };
        await client.SendAsync(request);

        var sent = fake.LastRequest!;

        // The body must be the exact bytes that were signed, with digest + content-type on the wire
        // (the peer recomputes the digest from the body to rebuild the signed base).
        Assert.NotNull(sent.Content);
        var wireBody = await sent.Content!.ReadAsByteArrayAsync();
        Assert.Equal(Encoding.UTF8.GetBytes(body), wireBody);
        Assert.True(sent.Content.Headers.Contains(Signatures.DigestHeaderName), "Digest header on the wire");
        Assert.NotNull(sent.Content.Headers.ContentType);
        Assert.Equal("application/activity+json", sent.Content.Headers.ContentType!.MediaType);

        // The digest on the wire must match the body's actual SHA-256 (what the peer recomputes).
        var wireDigest = sent.Content.Headers.GetValues(Signatures.DigestHeaderName).Single();
        Assert.Equal(Signatures.ComputeDigest(wireBody), wireDigest);

        var signatureValue = sent.Headers.GetValues(Signatures.SignatureHeaderName).Single();
        Assert.True(SignatureHeader.TryParse(signatureValue, out var header));
        var h = header!;

        Assert.Equal(KeyId.Value, h.KeyId);
        Assert.Equal("rsa-sha256", h.Algorithm);
        // ServerToServer profile (body POST): adds digest + content-type.
        Assert.Equal("(request-target) host date digest content-type", h.Headers);
        Assert.True(h.Created > 0, "created must be a real epoch timestamp");

        // The signature must verify against the PUBLIC key alone, reconstructing the base from the
        // wire (including the digest + content-type the peer sees).
        var date = sent.Headers.GetValues(Signatures.DateHeaderName).Single();
        var host = sent.RequestUri!.Authority;
        var path = sent.RequestUri!.PathAndQuery;
        var metadata = new HttpRequestMetadata(
            "POST", path, host, date, "application/activity+json", wireBody,
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                [Signatures.HostHeaderName] = host,
                [Signatures.DateHeaderName] = date,
                [Signatures.DigestHeaderName] = wireDigest,
            });
        Assert.True(new HttpSignatureVerifier(store).Verify(metadata, signatureValue));
    }

    [Fact]
    public async Task UnsignedRequest_WouldBeRejected_SignedRequest_Verifies()
    {
        // The conformance invariant the live test proved: mastodon.social 401s an unsigned request
        // ("Request not signed") but 202s a correctly signed one. Offline, the equivalent is that a
        // signature produced by the pipeline verifies, while a signature made with a DIFFERENT key
        // (claiming the same keyId) does NOT verify against the actor's real public key — i.e. the
        // public key is what decides acceptance.
        var (store, _, handler, fake) = Build();
        using var client = new HttpClient(handler);
        await client.GetAsync("https://mastodon.social/users/gargron");
        var sent = fake.LastRequest!;
        var signatureValue = sent.Headers.GetValues(Signatures.SignatureHeaderName).Single();

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

        // The correct key verifies...
        Assert.True(new HttpSignatureVerifier(store).Verify(metadata, signatureValue));

        // ...but a different key (same keyId, wrong private key) does NOT — the peer's public key is
        // the deciding factor, exactly as the live 401-on-bad-key control showed.
        using var wrongKeyStore = new InMemoryKeyStore();
        var wrongKey = KeyPairGenerator.GenerateRsa(KeyId); // same keyId, different private key
        wrongKeyStore.PutKey(wrongKey);
        Assert.False(new HttpSignatureVerifier(wrongKeyStore).Verify(metadata, signatureValue));
    }
}
