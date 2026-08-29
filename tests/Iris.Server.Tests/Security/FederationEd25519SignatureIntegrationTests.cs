using System.Net;
using Iris.Client;
using Iris.Core;
using Iris.Server;
using Iris.Server.InMemory;
using Iris.Testing;
using KristofferStrube.ActivityStreams;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace Iris.Server.Tests.Security;

/// <summary>
/// Slice 12.4 (F-05) integration test: instance-to-instance federation with an
/// <strong>Ed25519</strong> signing key. Two live in-process <see cref="Microsoft.AspNetCore.TestHost.TestServer"/>
/// instances (A and B):
/// </summary>
/// <list type="bullet">
/// <item>Instance A (a.domain.local) hosts actor <c>alice</c> with an <see cref="Ed25519Key"/>.</item>
/// <item>Instance B (b.domain.local) hosts actor <c>bob</c> with an <see cref="Ed25519Key"/>.</item>
/// </list>
/// <para>
/// alice follows bob: a client signed with alice's Ed25519 key POSTs a <c>Follow</c> activity to B's
/// inbox. B's <see cref="SignatureValidationMiddleware"/> validates the signature by resolving alice's
/// public key — fetching A's actor document over the wire (B's <see cref="IActorDocumentFetcher"/> is
/// wired to A's <c>TestServer</c>), where the public key is served as PEM with a <c>keyAlgorithm</c>
/// marker — classifying it as Ed25519, reconstructing an <see cref="Ed25519Key"/>, and verifying
/// cryptographically. The inbox handler then stores the validated activity.
/// </para>
/// <remarks>
/// This proves the full inbound Ed25519 validation path end-to-end: signature parsing, remote key
/// resolution via actor-document fetch, Ed25519 key reconstruction from PEM, and cryptographic
/// verification — all over real HTTP between two independent Iris instances. It exercises the
/// <see cref="ISigningKey"/> unification (the same pipeline handles RSA/EC <see cref="KeyPair"/> and
/// Ed25519 <see cref="Ed25519Key"/>).
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
    private readonly Ed25519Key _aliceKey;
    private readonly Ed25519Key _bobKey;

    private readonly Iri AliceActorIri;
    private readonly Iri AliceKeyId;
    private readonly Iri BobActorIri;
    private readonly Iri BobInboxIri;

    public FederationEd25519SignatureIntegrationTests()
    {
        _aPersistence = new InMemoryPersistenceProvider();
        _bPersistence = new InMemoryPersistenceProvider();

        var aSeeded = TestSeeder.SeedPersonWithEd25519Key(_aPersistence, AHost, Alice);
        _aliceKey = aSeeded.Key;
        AliceActorIri = aSeeded.ActorIri;
        AliceKeyId = aSeeded.KeyId;

        var bSeeded = TestSeeder.SeedPersonWithEd25519Key(_bPersistence, BHost, Bob);
        _bobKey = bSeeded.Key;
        BobActorIri = bSeeded.ActorIri;
        BobInboxIri = BobActorIri.InboxOf();

        _a = StartServer(AHost, Alice, _aPersistence);
        _b = StartServer(BHost, Bob, _bPersistence,
            fetcher: BuildFetcherFor(BHost, Bob, _bobKey, _a.CreateHandler()));
    }

    public void Dispose()
    {
        _a.Dispose();
        _b.Dispose();
    }

    // --- The happy path: alice follows bob over the wire (Ed25519) --------------

    [Fact]
    public async Task Follow_SignedWithEd25519_IsValidatedAndAcceptedAtBobInbox()
    {
        var follow = BuildFollow(AliceActorIri, BobActorIri);

        // A client signed as alice (Ed25519), whose transport routes to B's TestServer.
        using var client = BuildDeliveryClient(AliceActorIri, _aliceKey, _b.CreateHandler());
        var statusCode = await client.DeliverAsync(BobInboxIri, follow);

        Assert.Equal(202, statusCode);

        // B validated the Ed25519 signature (by fetching A's actor doc to resolve alice's key) and
        // stored the activity under its IRI.
        var stored = await _bPersistence.Activities.TryGetActivityAsync(new Iri(follow.Id!), out var activity);
        Assert.True(stored);
        Assert.NotNull(activity);
        Assert.Equal(follow.Id, activity!.Id);
    }

    // --- The follow is recorded as an edge (proves the handler ran) -------------

    [Fact]
    public async Task Follow_SignedWithEd25519_RecordsFollowEdgeInBobFollowStore()
    {
        var follow = BuildFollow(AliceActorIri, BobActorIri);

        using var client = BuildDeliveryClient(AliceActorIri, _aliceKey, _b.CreateHandler());
        var statusCode = await client.DeliverAsync(BobInboxIri, follow);
        Assert.Equal(202, statusCode);

        // B's inbox processor dispatched the validated Follow to the FollowActivityHandler, which
        // recorded the directed edge alice → bob in B's follow store.
        Assert.True(
            await _bPersistence.Follows.IsFollowingAsync(AliceActorIri, BobActorIri),
            "After a signed (Ed25519) Follow, alice should follow bob in B's follow store");

        var bobFollowers = await _bPersistence.Follows.GetFollowersAsync(BobActorIri);
        Assert.Contains(AliceActorIri, bobFollowers);
    }

    // --- Negative: an unsigned inbox POST is rejected with 401 ------------------

    [Fact]
    public async Task Follow_UntouchedBySignature_IsRejectedWith401()
    {
        var follow = BuildFollow(AliceActorIri, BobActorIri);
        var json = ActivityJson.Serialize(follow);

        // A plain (unsigned) POST to B's inbox: no Signature header → 401.
        using var http = new HttpClient(_b.CreateHandler());
        using var content = new StringContent(json);
        content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/activity+json");
        var response = await http.PostAsync(
            $"https://{BHost}/ap/v1/u/{Bob}/inbox", content);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
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

    private static IActivityPubClient BuildDeliveryClient(
        Iri actorIri, Ed25519Key key, HttpMessageHandler handler)
    {
        var keyStore = new InMemoryKeyStore();
        keyStore.PutKey(key);
        var keyProvider = new InMemoryKeyProvider(keyStore);
        keyProvider.RegisterKey(actorIri, key.KeyId);
        var signer = new HttpSignatureSigner(keyStore);

        var factory = new ActivityPubClientFactory(keyStore, keyProvider, signer);
        return factory.Create(
            new ActivityPubClientOptions { ActorId = actorIri, EnableRetry = false },
            handler);
    }

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

    private static Follow BuildFollow(Iri actorIri, Iri targetIri)
    {
        var follow = new Follow
        {
            Id = $"https://{AHost}/activities/follow-{Guid.NewGuid():N}",
            Actor = [new Link { Href = new Uri(actorIri.Value) }],
            Object = [new Link { Href = new Uri(targetIri.Value) }],
        };
        return follow;
    }
}
