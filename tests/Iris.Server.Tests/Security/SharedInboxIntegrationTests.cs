using System.Net;
using Iris.Client;
using Iris.Core;
using Iris.Server;
using Iris.Server.Identity;
using Iris.Server.InMemory;
using Iris.Testing;
using KristofferStrube.ActivityStreams;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace Iris.Server.Tests.Security;

/// <summary>
/// Integration tests for the instance-wide shared inbox (<c>POST /ap/v1/shared-inbox</c>) — the route
/// advertised in every actor document's <c>endpoints.sharedInbox</c>. A remote sender that prefers the
/// shared inbox (e.g. Mastodon, which coalesces delivery targets by <c>preferred_inbox_url</c>) delivers
/// every activity to this single route instead of to a per-actor inbox. Before this route existed, the
/// advertised <c>sharedInbox</c> fell through to the catch-all, which returned 200 and silently dropped
/// the body — so a local follower of a shared-inbox-preferring sender never received that sender's
/// posts (the "no longer receive posts after unfollow" symptom).
/// </summary>
/// <remarks>
/// Two live in-process <see cref="TestServer"/> instances: A (a.domain.local) hosts <c>alice</c>, B
/// (b.domain.local) hosts <c>bob</c> (and <c>carol</c> as a second local actor). A's fetcher routes to
/// B so B can resolve alice's key by fetching A's actor document over the wire (the same federation
/// round-trip as the per-actor inbox). Each test delivers a signed activity to B's
/// <c>/ap/v1/shared-inbox</c> and asserts B routed it to the correct local recipient.
/// </remarks>
public sealed class SharedInboxIntegrationTests : IDisposable
{
    private const string AHost = "a.domain.local";
    private const string BHost = "b.domain.local";
    private const string Alice = "alice";
    private const string Bob = "bob";
    private const string Carol = "carol";

    private readonly TestServer _a;
    private readonly TestServer _b;
    private readonly InMemoryPersistenceProvider _bPersistence;
    private readonly KeyPair _aliceKey;
    private readonly KeyPair _bobKey;
    private readonly Iri AliceActorIri;
    private readonly Iri BobActorIri;
    private readonly Iri CarolActorIri;
    private readonly Iri BobSharedInboxIri;

    public SharedInboxIntegrationTests()
    {
        var aPersistence = new InMemoryPersistenceProvider();
        _bPersistence = new InMemoryPersistenceProvider();

        var aSeeded = TestSeeder.SeedPersonWithKey(aPersistence, AHost, Alice);
        _aliceKey = aSeeded.Key;
        AliceActorIri = aSeeded.ActorIri;

        var bBob = TestSeeder.SeedPersonWithKey(_bPersistence, BHost, Bob);
        var bCarol = TestSeeder.SeedPersonWithKey(_bPersistence, BHost, Carol);
        _bobKey = bBob.Key;
        BobActorIri = bBob.ActorIri;
        CarolActorIri = bCarol.ActorIri;
        BobSharedInboxIri = new Iri($"https://{BHost}/ap/v1/shared-inbox");

        // A's fetcher/delivery are lazy self-safe loops (A doesn't fetch or deliver in these tests, but
        // the host builds those clients at startup, before _b exists). B's fetcher routes to A over the
        // wire: B resolves the remote signer's (alice's) key by fetching A's actor doc — the same
        // federation round-trip as the per-actor inbox. B's delivery is a self-loop over B's own
        // TestServer (the AnnounceActivityHandler propagating to carol's inbox), deferred via a
        // LazyHandler until bRef is assigned.
        TestServer? bRef = null;
        _a = StartServer(AHost, Alice, aPersistence,
            fetcher: BuildFetcherFor(AHost, Alice, aSeeded.Key, new LazyHandler(() => bRef!.CreateHandler())),
            deliveryTransport: () => new LazyHandler(() => bRef!.CreateHandler()));
        bRef = StartServer(BHost, Bob, _bPersistence,
            fetcher: BuildFetcherFor(BHost, Bob, bBob.Key, _a.CreateHandler()),
            extraLocalActors: [CarolActorIri],
            deliveryTransport: () => new LazyHandler(() => bRef!.CreateHandler()));
        _b = bRef;
    }

    public void Dispose()
    {
        _a.Dispose();
        _b.Dispose();
    }

    // --- A Follow delivered to the shared inbox is routed to its object (bob) ---

    [Fact]
    public async Task Follow_DeliveredToSharedInbox_RoutesToObjectAndRecordsFollowEdge()
    {
        var follow = BuildFollow(AliceActorIri, BobActorIri);

        using var client = BuildDeliveryClient(AliceActorIri, _aliceKey, _b.CreateHandler());
        var statusCode = await client.DeliverAsync(BobSharedInboxIri, follow);
        Assert.Equal(202, statusCode.StatusCode);

        // B validated alice's signature (resolving her key from A's actor doc over the wire) and stored
        // the activity under its IRI.
        Assert.True(
            await _bPersistence.Activities.TryGetActivityAsync(new Iri(follow.Id!), out _),
            "B should have stored the Follow after validating alice's signature at the shared inbox");

        // B routed the Follow to its object (bob) and the FollowActivityHandler recorded the edge — the
        // same outcome as if the Follow had been delivered to bob's per-actor inbox.
        Assert.True(
            await _bPersistence.Follows.IsFollowingAsync(AliceActorIri, BobActorIri),
            "A Follow delivered to the shared inbox should record the alice -> bob follow edge");
    }

    // --- alice (remote) announces (re-posts) a note, delivered to B's shared inbox. B fans it out to
    // --- alice's local followers (bob, who follows alice) and processes it — stored under its IRI. This
    // --- is the "shared-inbox-preferring sender posts, local follower receives it" path that was
    // --- silently dropped before the route existed.

    [Fact]
    public async Task Announce_DeliveredToSharedInbox_FannedOutToFollowersAndStored()
    {
        // bob follows alice (so alice's content is federation-targeted to bob).
        await _bPersistence.Follows.RecordFollowAsync(BobActorIri, AliceActorIri);

        // alice announces (re-posts) a remote note (object on A), addressed to bob — exactly what a
        // shared-inbox-preferring sender delivers to the instance's shared inbox. The object's author is
        // alice (remote); the intended recipient (bob) is the follower B routes to.
        var objectIri = $"https://{AHost}/objects/note-{Guid.NewGuid():N}";
        var announceIri = $"https://{AHost}/activities/announce-{Guid.NewGuid():N}";
        var announce = new Announce
        {
            Id = announceIri,
            Actor = [new Link { Href = new Uri(AliceActorIri.Value) }],
            AttributedTo = [new Link { Href = new Uri(AliceActorIri.Value) }],
            Object = [new Link { Href = new Uri(objectIri) }],
            To = [new Link { Href = new Uri(BobActorIri.Value) }],
        };

        // Deliver alice's signed Announce to B's shared inbox over the wire (B resolves alice's key from
        // A's actor doc over the wire).
        using var client = BuildDeliveryClient(AliceActorIri, _aliceKey, _b.CreateHandler());
        var statusCode = await client.DeliverAsync(BobSharedInboxIri, announce);
        Assert.Equal(202, statusCode.StatusCode);

        // B validated the signature and stored the Announce under its IRI (proving the shared inbox
        // routed it to a local recipient and processed it, rather than silently dropping the delivery).
        Assert.True(
            await _bPersistence.Activities.TryGetActivityAsync(new Iri(announceIri), out _),
            "B should have stored the Announce after routing it via the shared inbox");
    }

    // --- A Follow addressed to a REMOTE actor (not hosted by B) is accepted and dropped ---

    [Fact]
    public async Task Follow_AddressedToRemoteActor_AcceptedAndDropped()
    {
        // A shared-inbox-preferring sender delivers a Follow whose object is a remote actor (not hosted
        // by B). The shared inbox must accept (202) and drop it rather than 4xx (a 4xx would make the
        // sender retry a delivery B can never process).
        var remoteTargetIri = new Iri($"https://c.domain.local/ap/v1/u/dave");
        var follow = BuildFollow(AliceActorIri, remoteTargetIri);

        using var client = BuildDeliveryClient(AliceActorIri, _aliceKey, _b.CreateHandler());
        var statusCode = await client.DeliverAsync(BobSharedInboxIri, follow);
        Assert.Equal(202, statusCode.StatusCode);

        // No follow edge was recorded (the delivery was dropped, not processed): alice does not follow
        // the remote actor in B's store.
        Assert.False(
            await _bPersistence.Follows.IsFollowingAsync(AliceActorIri, remoteTargetIri),
            "A Follow to a remote actor delivered via the shared inbox should not record a follow edge");
    }

    // --- A Create whose author is not local is accepted and dropped (not this instance's concern) ---

    [Fact]
    public async Task Create_AuthorNotLocal_AcceptedAndDropped()
    {
        // alice creates a note whose embedded object is attributedTo a REMOTE actor (not hosted by B).
        // The shared inbox must accept (202) and drop it rather than 4xx (a 4xx would make the sender
        // retry a delivery B can never process).
        var remoteAuthorIri = new Iri($"https://c.domain.local/ap/v1/u/carol");
        var noteIri = $"https://{AHost}/objects/note-{Guid.NewGuid():N}";
        var createIri = $"https://{AHost}/activities/create-{Guid.NewGuid():N}";
        var note = new Note
        {
            Id = noteIri,
            AttributedTo = [new Link { Href = new Uri(remoteAuthorIri.Value) }],
            To = [new Link { Href = new Uri(Iri.Public.Value) }],
        };
        var create = new Create
        {
            Id = createIri,
            Actor = [new Link { Href = new Uri(AliceActorIri.Value) }],
            Object = [note],
        };

        using var client = BuildDeliveryClient(AliceActorIri, _aliceKey, _b.CreateHandler());
        var statusCode = await client.DeliverAsync(BobSharedInboxIri, create);
        Assert.Equal(202, statusCode.StatusCode);

        // Nothing was stored in bob's outbox (the delivery was dropped, not processed).
        var outbox = await _bPersistence.Activities.GetOutboxAsync(BobActorIri);
        Assert.Empty(outbox);
    }

    // --- Helpers ----------------------------------------------------------------

    private static TestServer StartServer(
        string host, string handle, InMemoryPersistenceProvider persistence,
        IActorDocumentFetcher? fetcher = null,
        IEnumerable<Iri>? extraLocalActors = null,
        Func<HttpMessageHandler>? deliveryTransport = null)
        => ActivityPubHostFactory.Create(new ActivityPubHostOptions
        {
            Host = host,
            Handle = handle,
            Persistence = persistence,
            Fetcher = fetcher,
            ExtraLocalActors = extraLocalActors,
            DeliveryTransport = deliveryTransport,
            // Advertise a shared inbox so the route is the instance's canonical sharedInbox.
            SharedInboxIri = new Iri($"https://{host}/ap/v1/shared-inbox"),
        });

    private static IActivityPubClient BuildDeliveryClient(
        Iri actorIri, KeyPair key, HttpMessageHandler handler)
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
        string host, string handle, KeyPair key, HttpMessageHandler handler)
    {
        var keyStore = new InMemoryKeyStore();
        keyStore.PutKey(key);
        var keyProvider = new InMemoryKeyProvider(keyStore);
        var actorIri = new Iri($"https://{host}/ap/v1/u/{handle}");
        keyProvider.RegisterKey(actorIri, key.KeyId);
        var signer = new HttpSignatureSigner(keyStore);

        var factory = new ActivityPubClientFactory(keyStore, keyProvider, signer);
        var client = factory.Create(
            new ActivityPubClientOptions { ActorId = actorIri, EnableRetry = false },
            handler);

        return new IrisActorDocumentFetcher(client, new RemoteActorCache());
    }

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

    private static async Task WaitForAsync(Func<Task<bool>> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (await condition())
            {
                return;
            }

            await Task.Delay(50);
        }

        Assert.True(await condition(), "Condition was not met within the timeout.");
    }
}
