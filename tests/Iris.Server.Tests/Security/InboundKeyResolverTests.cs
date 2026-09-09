using System.Text.Json;
using Iris.Client;
using Iris.Core;
using Iris.Server;
using KristofferStrube.ActivityStreams;

namespace Iris.Server.Tests.Security;

/// <summary>
/// Unit tests for <see cref="RemoteInboundKeyResolver"/>: it resolves a remote actor's public key by
/// fetching the actor's document (via a fake <see cref="IActorDocumentFetcher"/>), extracting the
/// <c>publicKey</c> (JWK or PEM), and reconstructing a public-only <see cref="KeyPair"/>.
/// </summary>
public class InboundKeyResolverTests
{
    private const string AHost = "a.domain.local";

    [Fact]
    public async Task Resolve_JwkPublicKey_ReturnsVerifyingKey()
    {
        // alice's real key + a document that carries its JWK (the standard ActivityPub shape).
        var aliceKey = KeyPairGenerator.GenerateEcP256(new Iri($"https://{AHost}/ap/v1/u/alice#key-1"));
        var fetcher = new StubActorDocumentFetcher(ActorWithPublicKey(
            id: $"https://{AHost}/ap/v1/u/alice#key-1",
            owner: $"https://{AHost}/ap/v1/u/alice",
            jwk: aliceKey.GetPublicJwk()));

        var resolver = new RemoteInboundKeyResolver(fetcher, new RemoteKeyCache());
        var resolved = await resolver.ResolveAsync(new Iri($"https://{AHost}/ap/v1/u/alice#key-1"));

        Assert.NotNull(resolved);
        var disposable = resolved as IDisposable;
        try
        {
            // The resolved public key must verify a signature made with alice's private key.
            byte[] payload = [1, 2, 3, 4, 5];
            var signature = aliceKey.Sign(payload);
            Assert.True(resolved.Verify(payload, signature));
        }
        finally
        {
            disposable?.Dispose();
        }
    }

    [Fact]
    public async Task Resolve_PemPublicKey_ReturnsVerifyingKey()
    {
        // A document that carries the public key as a PKIX PEM (e.g. an Iris-seeded document;
        // the default seeding algorithm is now RSA-2048).
        var bobKey = KeyPairGenerator.GenerateRsa(new Iri($"https://{AHost}/ap/v1/u/bob#key-1"));
        var pem = bobKey.ExportPublicKeyPem();
        var fetcher = new StubActorDocumentFetcher(ActorWithPublicKeyPem(
            id: $"https://{AHost}/ap/v1/u/bob#key-1",
            owner: $"https://{AHost}/ap/v1/u/bob",
            pem: pem));

        var resolver = new RemoteInboundKeyResolver(fetcher, new RemoteKeyCache());
        var resolved = await resolver.ResolveAsync(new Iri($"https://{AHost}/ap/v1/u/bob#key-1"));

        Assert.NotNull(resolved);
        var disposable = resolved as IDisposable;
        try
        {
            byte[] payload = [9, 8, 7];
            var signature = bobKey.Sign(payload);
            Assert.True(resolved.Verify(payload, signature));
        }
        finally
        {
            disposable?.Dispose();
        }
    }

    [Fact]
    public async Task Resolve_Pkcs1PemPublicKey_ReturnsVerifyingKey()
    {
        // A document that carries the public key as a PKCS#1 RSA public key PEM
        // (-----BEGIN RSA PUBLIC KEY-----), the raw form some real-world servers serve in
        // publicKeyPem.
        var bobKey = KeyPairGenerator.GenerateRsa(new Iri($"https://{AHost}/ap/v1/u/bob#key-1"));
        var rsa = (System.Security.Cryptography.RSA)bobKey.Key;
        var pem = $"-----BEGIN RSA PUBLIC KEY-----\n{Convert.ToBase64String(rsa.ExportRSAPublicKey(), Base64FormattingOptions.InsertLineBreaks)}\n-----END RSA PUBLIC KEY-----\n";
        var fetcher = new StubActorDocumentFetcher(ActorWithPublicKeyPem(
            id: $"https://{AHost}/ap/v1/u/bob#key-1",
            owner: $"https://{AHost}/ap/v1/u/bob",
            pem: pem));

        var resolver = new RemoteInboundKeyResolver(fetcher, new RemoteKeyCache());
        var resolved = await resolver.ResolveAsync(new Iri($"https://{AHost}/ap/v1/u/bob#key-1"));

        Assert.NotNull(resolved);
        var disposable = resolved as IDisposable;
        try
        {
            byte[] payload = [5, 6, 7];
            var signature = bobKey.Sign(payload);
            Assert.True(resolved.Verify(payload, signature));
        }
        finally
        {
            disposable?.Dispose();
        }
    }

    [Fact]
    public async Task Resolve_EcPemPublicKey_ReturnsVerifyingKey()
    {
        // EC P-256 documents are still accepted (the algorithm is inferred from the PEM).
        var erinKey = KeyPairGenerator.GenerateEcP256(new Iri($"https://{AHost}/ap/v1/u/erin#key-1"));
        var fetcher = new StubActorDocumentFetcher(ActorWithPublicKeyPem(
            id: $"https://{AHost}/ap/v1/u/erin#key-1",
            owner: $"https://{AHost}/ap/v1/u/erin",
            pem: erinKey.ExportPublicKeyPem()));

        var resolver = new RemoteInboundKeyResolver(fetcher, new RemoteKeyCache());
        var resolved = await resolver.ResolveAsync(new Iri($"https://{AHost}/ap/v1/u/erin#key-1"));

        Assert.NotNull(resolved);
        var disposable = resolved as IDisposable;
        try
        {
            byte[] payload = [1, 1, 2];
            var signature = erinKey.Sign(payload);
            Assert.True(resolved.Verify(payload, signature));
        }
        finally
        {
            disposable?.Dispose();
        }
    }

    [Fact]
    public async Task Resolve_UnknownActor_ReturnsNull()
    {
        var fetcher = new StubActorDocumentFetcher(null); // fetch fails / not found
        var resolver = new RemoteInboundKeyResolver(fetcher, new RemoteKeyCache());

        var resolved = await resolver.ResolveAsync(new Iri($"https://{AHost}/ap/v1/u/nobody#key-1"));
        Assert.Null(resolved);
    }

    [Fact]
    public async Task Resolve_MissingPublicKey_ReturnsNull()
    {
        // An actor document that does not carry a publicKey extension.
        var actor = new Person { Id = $"https://{AHost}/ap/v1/u/carol", PreferredUsername = "carol" };
        var fetcher = new StubActorDocumentFetcher(actor);
        var resolver = new RemoteInboundKeyResolver(fetcher, new RemoteKeyCache());

        var resolved = await resolver.ResolveAsync(new Iri($"https://{AHost}/ap/v1/u/carol#key-1"));
        Assert.Null(resolved);
    }

    [Fact]
    public async Task Resolve_PublicKeyWithReplaces_InvalidatesOldKeyCache()
    {
        // F-25: when the remote document's publicKey declares a `replaces` property, the old key's
        // cache entry is invalidated so the next resolution of the old key triggers a refetch.
        var oldKeyId = new Iri($"https://{AHost}/ap/v1/u/frank#key-1");
        var newKeyId = new Iri($"https://{AHost}/ap/v1/u/frank#key-2");
        var frankKey = KeyPairGenerator.GenerateEcP256(newKeyId);

        // Build an actor document whose publicKey has a `replaces` field pointing at the old key.
        var actor = ActorWithPublicKey(
            id: newKeyId.Value,
            owner: $"https://{AHost}/ap/v1/u/frank",
            jwk: frankKey.GetPublicJwk());
        var pk = actor.ExtensionData![ActivityPubExtensionNames.PublicKey];
        var pkObj = pk.EnumerateObject().ToDictionary(
            (JsonProperty p) => p.Name, p => p.Value, StringComparer.Ordinal);
        pkObj["replaces"] = JsonSerializer.SerializeToElement(oldKeyId.Value);
        actor.ExtensionData![ActivityPubExtensionNames.PublicKey] = JsonSerializer.SerializeToElement(
            pkObj.ToDictionary(kv => kv.Key, kv => kv.Value.Clone(), StringComparer.Ordinal));

        var cache = new RemoteKeyCache();
        // Pre-populate the cache with the old key's JWK (simulating a prior resolution).
        await cache.GetAsync(oldKeyId, bypassCache: false, async _ =>
        {
            await Task.Yield();
            return new JwkKey(frankKey.GetPublicJwk(), Signatures.AlgorithmLabel(KeyAlgorithm.EcP256));
        });
        Assert.Equal(1, cache.Count);

        var fetcher = new StubActorDocumentFetcher(actor);
        var resolver = new RemoteInboundKeyResolver(fetcher, cache);

        // Resolving the new key triggers the fetch, which discovers `replaces` and invalidates the old key.
        var resolved = await resolver.ResolveAsync(newKeyId);
        Assert.NotNull(resolved);
        (resolved as IDisposable)?.Dispose();

        // The cache holds exactly one entry: the new key (the old key was invalidated).
        Assert.Equal(1, cache.Count);
    }

    [Fact]
    public async Task Resolve_SecondCall_IsCached()
    {
        var daveKey = KeyPairGenerator.GenerateEcP256(new Iri($"https://{AHost}/ap/v1/u/dave#key-1"));
        var fetcher = new StubActorDocumentFetcher(ActorWithPublicKey(
            id: $"https://{AHost}/ap/v1/u/dave#key-1",
            owner: $"https://{AHost}/ap/v1/u/dave",
            jwk: daveKey.GetPublicJwk()));

        var cache = new RemoteKeyCache();
        var resolver = new RemoteInboundKeyResolver(fetcher, cache);
        var keyId = new Iri($"https://{AHost}/ap/v1/u/dave#key-1");

        var first = await resolver.ResolveAsync(keyId);
        Assert.NotNull(first);
        (first as IDisposable)?.Dispose();

        // Second call is a cache hit: the fetcher is not invoked again.
        var second = await resolver.ResolveAsync(keyId);
        Assert.NotNull(second);
        (second as IDisposable)?.Dispose();

        Assert.Equal(1, fetcher.FetchCount);
        Assert.Equal(1, cache.Count);
    }

    // --- Helpers -----------------------------------------------------------------

    private static Person ActorWithPublicKey(string id, string owner, string jwk)
    {
        var actor = new Person { Id = owner, PreferredUsername = owner.Split('/').Last() };
        actor.ExtensionData ??= new Dictionary<string, JsonElement>();
        // publicKey as a JWK object: { id, owner, kty, ... }.
        var jwkElement = JsonSerializer.Deserialize<JsonElement>(jwk);
        var publicKey = new Dictionary<string, JsonElement>
        {
            ["id"] = JsonSerializer.SerializeToElement(id),
            ["owner"] = JsonSerializer.SerializeToElement(owner),
        };
        foreach (var property in jwkElement.EnumerateObject())
        {
            publicKey[property.Name] = property.Value.Clone();
        }

        actor.ExtensionData["publicKey"] = JsonSerializer.SerializeToElement(publicKey);
        return actor;
    }

    private static Person ActorWithPublicKeyPem(string id, string owner, string pem)
    {
        var actor = new Person { Id = owner, PreferredUsername = owner.Split('/').Last() };
        actor.ExtensionData ??= new Dictionary<string, JsonElement>();
        actor.ExtensionData["publicKey"] = JsonSerializer.SerializeToElement(new
        {
            id,
            owner,
            publicKeyPem = pem,
        });
        return actor;
    }

    /// <summary>
    /// A stub <see cref="IActorDocumentFetcher"/> that returns a fixed actor (or null) and counts fetches.
    /// </summary>
    private sealed class StubActorDocumentFetcher(Actor? actor) : IActorDocumentFetcher
    {
        private readonly Actor? _actor = actor;

        /// <summary>
        /// The number of times <see cref="GetActorAsync"/> has been invoked.
        /// </summary>
        public int FetchCount { get; private set; }

        /// <inheritdoc/>
        public Task<Actor?> GetActorAsync(Iri actorIri, CancellationToken ct = default)
        {
            FetchCount++;
            return Task.FromResult(_actor);
        }
    }
}
