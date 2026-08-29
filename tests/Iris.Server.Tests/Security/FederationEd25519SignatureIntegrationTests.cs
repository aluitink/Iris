using Iris.Client;
using Iris.Core;
using Iris.Server;
using Iris.Server.InMemory;
using Iris.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace Iris.Server.Tests.Security;

/// <summary>
/// Slice 12.4 (F-05) integration test: the one <strong>Ed25519</strong>-specific aspect of
/// instance-to-instance federation that the RSA suite (<see cref="FederationSignatureIntegrationTests"/>)
/// does not cover — that a remote inbound key resolved by fetching an actor document over the wire is
/// classified as Ed25519 (from the PEM + <c>keyAlgorithm</c> marker) and reconstructed as an
/// <see cref="Ed25519Key"/>, not a <see cref="KeyPair"/>.
/// </summary>
/// <remarks>
/// Topology: instance A (a.domain.local) hosts <c>alice</c> with an <see cref="Ed25519Key"/>; instance
/// B (b.domain.local) hosts <c>bob</c> (also Ed25519) and its <see cref="IActorDocumentFetcher"/> is
/// wired to A. The happy-path follow / follow-edge / unsigned-401 scenarios are identical to the RSA
/// suite's (the pipeline is algorithm-agnostic once the key resolves), so they are not repeated here;
/// this suite keeps only the resolver classification assertion that is unique to Ed25519.
/// </remarks>
public sealed class FederationEd25519SignatureIntegrationTests : IDisposable
{
    private const string AHost = "a.domain.local";
    private const string BHost = "b.domain.local";
    private const string Alice = "alice";
    private const string Bob = "bob";

    private readonly TestServer _a;
    private readonly TestServer _b;
    private readonly InMemoryPersistenceProvider _aPersistence;
    private readonly InMemoryPersistenceProvider _bPersistence;
    private readonly Ed25519Key _bobKey;

    private readonly Iri AliceKeyId;

    public FederationEd25519SignatureIntegrationTests()
    {
        _aPersistence = new InMemoryPersistenceProvider();
        _bPersistence = new InMemoryPersistenceProvider();

        var aSeeded = TestSeeder.SeedPersonWithEd25519Key(_aPersistence, AHost, Alice);
        AliceKeyId = aSeeded.KeyId;

        var bSeeded = TestSeeder.SeedPersonWithEd25519Key(_bPersistence, BHost, Bob);
        _bobKey = bSeeded.Key;

        _a = StartServer(AHost, Alice, _aPersistence);
        _b = StartServer(BHost, Bob, _bPersistence,
            fetcher: BuildFetcherFor(BHost, Bob, _bobKey, _a.CreateHandler()));
    }

    public void Dispose()
    {
        _a.Dispose();
        _b.Dispose();
    }

    // --- Key resolution: B resolves alice's Ed25519 key by fetching A's doc -----

    [Fact]
    public async Task Resolver_ResolvesRemoteEd25519Key_ByFetchingActorDocumentOverWire()
    {
        var resolver = _b.Services.GetRequiredService<IInboundKeyResolver>();
        var key = await resolver.ResolveAsync(AliceKeyId);
        Assert.NotNull(key);
        // The resolved key must be an Ed25519 key (not a KeyPair) — the resolver classified the
        // PEM + keyAlgorithm marker as Ed25519 and reconstructed it with the dedicated type.
        Assert.IsType<Ed25519Key>(key);
    }

    // --- Helpers ----------------------------------------------------------------

    private static IActorDocumentFetcher BuildFetcherFor(
        string host, string handle, Ed25519Key bobKey, HttpMessageHandler handler)
    {
        var keyStore = new InMemoryKeyStore();
        keyStore.PutKey(bobKey);
        var keyProvider = new InMemoryKeyProvider(keyStore);
        var bobActorIri = new Iri($"https://{host}/ap/v1/u/{handle}");
        keyProvider.RegisterKey(bobActorIri, bobKey.KeyId);
        var signer = new HttpSignatureSigner(keyStore);

        var factory = new ActivityPubClientFactory(keyStore, keyProvider, signer);
        var client = factory.Create(
            new ActivityPubClientOptions { ActorId = bobActorIri, EnableRetry = false },
            handler);

        return new IrisActorDocumentFetcher(client, new RemoteActorCache());
    }

    private static TestServer StartServer(
        string host, string handle, InMemoryPersistenceProvider persistence,
        IActorDocumentFetcher? fetcher = null)
        => ActivityPubHostFactory.Create(new ActivityPubHostOptions
        {
            Host = host,
            Handle = handle,
            Persistence = persistence,
            Fetcher = fetcher,
        });
}
