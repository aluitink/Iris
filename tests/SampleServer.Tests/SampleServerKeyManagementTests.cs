using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Iris.Client.Auth;
using Iris.Core;
using Iris.Core.Identity;
using Iris.Server;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace Iris.Samples.SampleServer.Tests;

/// <summary>
/// Key-management hardening tests (Phase 82.3). The outbound/inbound federation depends on every
/// locally-provisioned actor's <c>publicKey</c> (served in its actor doc) being the SAME key the
/// instance actually signs outbound requests with. A divergence — e.g. a key-rotation or
/// misconfiguration drift where the served <c>publicKeyPem</c> and the store's signing key no longer
/// match — would silently break federation in both directions (peers could not verify our signatures,
/// and we could not sign as the actor with the key peers hold). These tests lock the invariant against
/// the real hosted sample (users + community, RSA + Ed25519):
/// <list type="bullet">
/// <item>every served actor doc carries a <c>publicKey</c> with <c>id</c> / <c>owner</c> /
/// <c>publicKeyPem</c>;</item>
/// <item>the served <c>publicKeyPem</c> <em>verifies</em> a signature the instance produces with its
/// store's signing key for that actor (the publicKey↔signing-key consistency invariant);</item>
/// <item>the <c>publicKey.id</c> resolves to the store's signing key (the key IRI on the doc is the key
/// we actually hold and sign with).</item>
/// </list>
/// </summary>
public sealed class SampleServerKeyManagementTests : IDisposable
{
    private const string Host = "localhost";
    private const int Port = 5000;

    private readonly TestServer _server;
    private readonly HttpClient _client;
    private readonly IPersistenceProvider _persistence;
    private readonly IKeyProvider _keyProvider;

    public SampleServerKeyManagementTests()
    {
        _server = new TestServer(SampleServer.CreateWebHostBuilder());
        _client = _server.CreateClient();
        _persistence = _server.Services.GetRequiredService<IPersistenceProvider>();
        _keyProvider = _server.Services.GetRequiredService<IKeyProvider>();
    }

    public void Dispose()
    {
        _server.Dispose();
    }

    private static string BaseUri => $"http://{Host}:{Port}";

    // --- Every locally-provisioned actor doc carries a usable publicKey --------

    [Fact]
    public async Task ServedActorDoc_CarriesPublicKey_WithIdOwnerAndPem()
    {
        var json = await _client.GetStringAsync($"/ap/v1/u/{SampleServer.BobHandle}");
        using var doc = JsonDocument.Parse(json);
        var publicKey = doc.RootElement.GetProperty("publicKey");

        // The three fields a strict peer needs to resolve + verify our signatures.
        Assert.NotNull(publicKey.TryGetProperty("id", out var id) ? id.GetString() : null);
        Assert.NotNull(publicKey.TryGetProperty("owner", out var owner) ? owner.GetString() : null);
        var pem = publicKey.TryGetProperty("publicKeyPem", out var pemEl) ? pemEl.GetString() : null;
        Assert.NotNull(pem);
        Assert.StartsWith("-----BEGIN PUBLIC KEY-----", pem);
    }

    [Fact]
    public async Task ServedCommunityDoc_CarriesPublicKey_WithIdOwnerAndPem()
    {
        // The community (Group) is a locally-provisioned actor too — it must carry its own publicKey so
        // a peer can verify its outbound signatures (the community signs its deliveries, F-1911-3).
        var json = await _client.GetStringAsync($"/ap/v1/c/{SampleServer.SampleCommunityName}");
        using var doc = JsonDocument.Parse(json);
        var publicKey = doc.RootElement.GetProperty("publicKey");

        Assert.NotNull(publicKey.GetProperty("id").GetString());
        Assert.NotNull(publicKey.GetProperty("owner").GetString());
        var pem = publicKey.GetProperty("publicKeyPem").GetString();
        Assert.NotNull(pem);
        Assert.StartsWith("-----BEGIN PUBLIC KEY-----", pem);
    }

    // --- The publicKey↔signing-key consistency invariant -----------------------

    [Theory]
    [InlineData("alice", true)]
    [InlineData("bob", true)]
    public async Task ServedPublicKey_VerifiesSignatureMadeWithStoreSigningKey(string handle, bool _)
    {
        // The core invariant: the public key a peer reads from the actor doc must verify a signature the
        // instance actually produces (with the store's signing key for that actor). If the served PEM and
        // the signing key ever drift apart, this fails — which is exactly the federation-breaking
        // misconfiguration the test guards against.
        var actorIri = new Iri($"{BaseUri}/ap/v1/u/{handle}");
        var keyIri = new Iri($"{actorIri}#key-1");

        // (1) The instance's actual signing key for this actor (from the durable store).
        Assert.True(_persistence.Keys.TryGetKey(keyIri, out var signingKey) && signingKey is not null,
            "the instance must hold a signing key for its locally-provisioned actor");

        // (2) A signature the instance would produce over a test payload.
        var payload = Encoding.UTF8.GetBytes($"key-management-invariant:{handle}:{Guid.NewGuid()}");
        var signature = signingKey!.Sign(payload);

        // (3) The public key as SERVED in the actor doc (what a peer fetches to verify).
        var json = await _client.GetStringAsync($"/ap/v1/u/{handle}");
        using var doc = JsonDocument.Parse(json);
        var publicKey = doc.RootElement.GetProperty("publicKey");
        var servedPem = publicKey.GetProperty("publicKeyPem").GetString()!;

        // (4) Load the served PEM as a (public-only) key and verify the signature. If the served key
        //     matches the signing key, verification succeeds.
        var servedKey = LoadPublicOnlyKey(servedPem, signingKey.Algorithm, keyIri);
        Assert.True(servedKey.Verify(payload, signature),
            "the publicKeyPem served in the actor doc must verify a signature the instance produces "
            + "with its store's signing key (publicKey↔signing-key consistency)");
    }

    [Fact]
    public async Task ServedCommunityPublicKey_VerifiesSignatureMadeWithStoreSigningKey()
    {
        // The community signs with the primary actor's key (its publicKey.id points at alice's
        // #key-1). The invariant must hold for the community too: the PEM served on the community doc
        // verifies a signature produced with the key the community actually signs with.
        var communityIri = new Iri($"{BaseUri}/ap/v1/c/{SampleServer.SampleCommunityName}");

        // The community's signing identity (resolved by the key provider — the same path the
        // DeliveryWorker uses to sign the community's outbound deliveries).
        Assert.True(_keyProvider.TryGetIdentity(communityIri, out var identity) && identity is not null,
            "the key provider must resolve the community's signing identity");
        Assert.True(_persistence.Keys.TryGetKey(identity!.KeyId, out var signingKey) && signingKey is not null,
            "the instance must hold the community's signing key");

        var payload = Encoding.UTF8.GetBytes($"community-key-management-invariant:{Guid.NewGuid()}");
        var signature = signingKey!.Sign(payload);

        var json = await _client.GetStringAsync($"/ap/v1/c/{SampleServer.SampleCommunityName}");
        using var doc = JsonDocument.Parse(json);
        var servedPem = doc.RootElement.GetProperty("publicKey").GetProperty("publicKeyPem").GetString()!;

        var servedKey = LoadPublicOnlyKey(servedPem, signingKey.Algorithm, identity.KeyId);
        Assert.True(servedKey.Verify(payload, signature),
            "the community's served publicKeyPem must verify a signature it produces with its signing key");
    }

    // --- The publicKey.id resolves to the key we actually sign with -------------

    [Fact]
    public async Task PublicKeyId_ResolvesToStoreSigningKey()
    {
        // The publicKey.id on the served doc must be a key IRI the instance actually holds (so a peer
        // that resolves that IRI gets the key we sign with). A dangling id (pointing at a key we do not
        // hold) would mean the served key and our signing key diverge.
        var json = await _client.GetStringAsync($"/ap/v1/u/{SampleServer.BobHandle}");
        using var doc = JsonDocument.Parse(json);
        var idStr = doc.RootElement.GetProperty("publicKey").GetProperty("id").GetString();
        Assert.NotNull(idStr);
        Assert.True(Iri.TryParse(idStr!, out var keyIri));

        Assert.True(_persistence.Keys.TryGetKey(keyIri!, out var key) && key is not null,
            "the publicKey.id served on the actor doc must resolve to a key the instance holds");
    }

    /// <summary>
    /// Loads a (possibly public-only) PEM as an <see cref="ISigningKey"/> for the given algorithm, for
    /// the purpose of verifying signatures. RSA/EC public keys load via <see cref="KeyPair.FromPem"/>
    /// (which accepts a PKIX public key); Ed25519 via <see cref="Ed25519Key.FromPem"/>.
    /// </summary>
    private static ISigningKey LoadPublicOnlyKey(string pem, KeyAlgorithm algorithm, Iri keyId)
        => algorithm switch
        {
            KeyAlgorithm.Ed25519 => Ed25519Key.FromPem(pem, keyId),
            _ => KeyPair.FromPem(pem, algorithm, keyId),
        };
}
