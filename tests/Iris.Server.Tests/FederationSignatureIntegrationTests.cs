using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Iris.Client;
using Iris.Core;
using Iris.Server;
using Iris.Server.InMemory;
using Iris.Testing;
using KristofferStrube.ActivityStreams;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Iris.Server.Tests;

/// <summary>
/// Phase 4 integration tests: the first true <strong>instance-to-instance federation</strong> test.
/// Two live in-process <see cref="Microsoft.AspNetCore.TestHost.TestServer"/> instances (A and B) are
/// wired together over a genuine HTTP stack:
/// </summary>
/// <list type="bullet">
/// <item>Instance A (a.domain.local) hosts actor <c>alice</c> (key <c>keyA</c>).</item>
/// <item>Instance B (b.domain.local) hosts actor <c>bob</c> (key <c>keyB</c>).</item>
/// </list>
/// <para>
/// alice follows bob: a client signed as alice POSTs a <c>Follow</c> activity to B's inbox. B's
/// <see cref="SignatureValidationMiddleware"/> validates the HTTP signature by resolving alice's public
/// key — fetching A's actor document over the wire (B's <see cref="IActorDocumentFetcher"/> is wired to
/// A's <c>TestServer</c>) — and checking it cryptographically. The inbox handler then stores the
/// validated activity.
/// </para>
/// <remarks>
/// This proves the full inbound validation path end-to-end: signature parsing, remote key resolution
/// via actor-document fetch, key reconstruction from a JWK, and cryptographic verification — all over
/// real HTTP between two independent Iris instances.
/// </remarks>
public sealed class FederationSignatureIntegrationTests : IDisposable
{
    private const string AHost = "a.domain.local";
    private const string BHost = "b.domain.local";
    private const string Alice = "alice";
    private const string Bob = "bob";
    private const string Carol = "carol";

    private readonly TestServer _a;
    private readonly TestServer _b;
    private readonly InMemoryPersistenceProvider _aPersistence;
    private readonly InMemoryPersistenceProvider _bPersistence;
    private readonly KeyPair _aliceKey;
    private readonly KeyPair _bobKey;

    private readonly Iri AliceActorIri;
    private readonly Iri AliceKeyId;
    private readonly Iri BobActorIri;
    private readonly Iri BobInboxIri;

    public FederationSignatureIntegrationTests()
    {
        _aPersistence = new InMemoryPersistenceProvider();
        _bPersistence = new InMemoryPersistenceProvider();

        var aSeeded = TestSeeder.SeedPersonWithKey(_aPersistence, AHost, Alice);
        _aliceKey = aSeeded.Key;
        AliceActorIri = aSeeded.ActorIri;
        AliceKeyId = aSeeded.KeyId;

        var bSeeded = TestSeeder.SeedPersonWithKey(_bPersistence, BHost, Bob);
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

    // --- The happy path: alice follows bob over the wire ----------------------

    [Fact]
    public async Task Follow_SignedByAlice_IsValidatedAndAcceptedAtBobInbox()
    {
        var follow = BuildFollow(AliceActorIri, BobActorIri);

        // A client signed as alice, whose transport routes to B's TestServer.
        using var client = BuildDeliveryClient(AliceActorIri, _aliceKey, _b.CreateHandler());
        var statusCode = await client.DeliverAsync(BobInboxIri, follow);

        Assert.Equal(202, statusCode);

        // B validated the signature (by fetching A's actor doc to resolve alice's key) and stored
        // the activity under its IRI.
        var stored = await _bPersistence.Activities.TryGetActivityAsync(new Iri(follow.Id!), out var activity);
        Assert.True(stored);
        Assert.NotNull(activity);
        Assert.Equal(follow.Id, activity!.Id);

        // A's actor document endpoint was hit by B's key-resolution fetch (the federation round-trip).
        // (The middleware on A does not validate GETs, so this is a plain public document fetch.)
    }

    // --- The follow is not just stored: the FollowActivityHandler records the edge ----

    [Fact]
    public async Task Follow_SignedByAlice_RecordsFollowEdgeInBobFollowStore()
    {
        var follow = BuildFollow(AliceActorIri, BobActorIri);

        // Deliver the signed Follow over the wire to B's inbox.
        using var client = BuildDeliveryClient(AliceActorIri, _aliceKey, _b.CreateHandler());
        var statusCode = await client.DeliverAsync(BobInboxIri, follow);
        Assert.Equal(202, statusCode);

        // B's inbox processor dispatched the validated Follow to the FollowActivityHandler, which
        // recorded the directed edge alice → bob in B's follow store. This proves the full inbound
        // pipeline end-to-end: signature validation → store → interpret (record the follow).
        Assert.True(
            await _bPersistence.Follows.IsFollowingAsync(AliceActorIri, BobActorIri),
            "After a signed Follow, alice should follow bob in B's follow store");

        var bobFollowers = await _bPersistence.Follows.GetFollowersAsync(BobActorIri);
        Assert.Contains(AliceActorIri, bobFollowers);

        // And the reverse direction (bob's following list) is also recorded.
        var aliceFollowing = await _bPersistence.Follows.GetFollowingAsync(AliceActorIri);
        Assert.Contains(BobActorIri, aliceFollowing);
    }

    // --- The full Follow/Accept loop: bob accepts alice's follow, delivered back over the wire --

    [Fact]
    public async Task Follow_ThenAccept_FullFederationLoop_AcceptIsDeliveredBackToAliceInbox()
    {
        // Self-contained two-instance federation (mirrors DeliveryIntegrationTests): fresh
        // persistence per instance so this test is isolated from the fixture's servers. B's outbound
        // delivery transport is wired to A so B's DeliveryWorker delivers the Accept back to alice's
        // inbox over the wire (signed as bob); A validates bob's signature by fetching B's actor doc.
        var aPersistence = new InMemoryPersistenceProvider();
        var bPersistence = new InMemoryPersistenceProvider();
        var aSeeded = TestSeeder.SeedPersonWithKey(aPersistence, AHost, Alice);
        var bSeeded = TestSeeder.SeedPersonWithKey(bPersistence, BHost, Bob);
        var aliceActorIri = aSeeded.ActorIri;
        var bobActorIri = bSeeded.ActorIri;
        var bobInboxIri = bobActorIri.InboxOf();

        // A's fetcher routes to the NEW b via a lazy handler (resolved on first use), which breaks the
        // A↔B wiring chicken-and-egg (A's fetcher needs B's handler; B's transport needs A's handler)
        // and ensures A fetches bob's actor doc carrying bSeeded.Key — the key B's worker actually
        // signs the Accept with — so A's signature validation succeeds.
        TestServer? bRef = null;
        var a = StartServer(AHost, Alice, aPersistence,
            fetcher: BuildFetcherFor(AHost, Alice, aSeeded.Key, new LazyHandler(() => bRef!.CreateHandler())));
        bRef = StartServer(BHost, Bob, bPersistence,
            fetcher: BuildFetcherFor(BHost, Bob, bSeeded.Key, a.CreateHandler()),
            deliveryTransport: () => a.CreateHandler());
        using var scope = new DisposeBoth(bRef, a);

        // Deliver a signed Follow from alice to bob's inbox over the wire.
        var follow = BuildFollow(aliceActorIri, bobActorIri);
        using var client = BuildDeliveryClient(aliceActorIri, aSeeded.Key, bRef.CreateHandler());
        var statusCode = await client.DeliverAsync(bobInboxIri, follow);
        Assert.Equal(202, statusCode);
        Assert.True(
            await bPersistence.Follows.IsFollowingAsync(aliceActorIri, bobActorIri),
            "B should record that alice follows bob");

        // B's FollowActivityHandler scheduled an Accept with a deterministic IRI (bob's actor IRI +
        // /accepts + the follow IRI). B's DeliveryWorker delivers it to alice's inbox over the wire;
        // A validates bob's signature (fetching B's actor doc) and stores it under that IRI.
        var acceptIri = new Iri($"{bobActorIri}/accepts/{follow.Id}");
        await WaitForAsync(
            () => aPersistence.Activities.TryGetActivityAsync(acceptIri, out _),
            timeout: TimeSpan.FromSeconds(10));

        Assert.True(
            await aPersistence.Activities.TryGetActivityAsync(acceptIri, out var stored),
            "A should have stored the Accept delivered by B over the wire");
        var accept = Assert.IsType<Accept>(stored!);

        // The Accept's object references the original follow (by IRI) and its actor is bob.
        Assert.NotNull(accept.Object);
        Assert.Contains(accept.Object!, o => o is ILink { Href: { } href } && href == new Uri(follow.Id!));
        Assert.NotNull(accept.Actor);
        Assert.Contains(accept.Actor!, a => a is ILink { Href: { } href } && href == new Uri(bobActorIri.Value));
    }

    // --- The full Follow/Reject loop: bob rejects alice's follow, delivered back over the wire --
    //
    // Self-contained two-instance federation (mirrors the Accept full loop above). alice follows bob:
    // A records the follow it sent (the activity + alice's own provisional follow edge, alice → bob) in
    // A's store. bob then rejects it: B constructs the Reject (deterministic IRI: bob's actor IRI +
    // /rejects + the follow IRI — the local actor's decision, not auto-generated) and delivers it back
    // to alice's inbox over the wire, signed as bob. A validates bob's signature (fetching B's actor
    // doc) and its RejectActivityHandler resolves the follow's target from A's local store and removes
    // the alice → bob edge from A's follow store (and, visibly, from alice's public `following`
    // collection). This proves the full inbound pipeline for a Reject end-to-end — and, critically,
    // that the DI now registers the RejectActivityHandler (the AddSingleton fix in Resolved Decision
    // #34): if the handler were (still) dropped, the Reject would be stored but the follow edge would
    // survive.
    //
    // Note: the FollowActivityHandler auto-accepts a follow (it schedules an Accept). A Reject is the
    // followed side's *explicit* decision and supersedes that auto-Accept — so this test does not wire
    // B's outbound delivery transport (the Reject is delivered directly, signed as bob), keeping the
    // auto-Accept from racing the Reject back into alice's inbox and re-adding the edge.

    [Fact]
    public async Task Follow_ThenReject_FullFederationLoop_RejectIsDeliveredBackAndRemovesFollowEdge()
    {
        var aPersistence = new InMemoryPersistenceProvider();
        var bPersistence = new InMemoryPersistenceProvider();
        var aSeeded = TestSeeder.SeedPersonWithKey(aPersistence, AHost, Alice);
        var bSeeded = TestSeeder.SeedPersonWithKey(bPersistence, BHost, Bob);
        var aliceActorIri = aSeeded.ActorIri;
        var bobActorIri = bSeeded.ActorIri;
        var aliceFollowingIri = new Iri($"{aliceActorIri}/following");

        // A's fetcher routes to B via a lazy handler (resolved on first use) — the same wiring as the
        // Accept full loop — so A fetches bob's actor doc carrying bSeeded.Key, the key the Reject is
        // signed with; A's signature validation therefore succeeds. B's outbound delivery transport is
        // NOT wired here: the Reject is delivered directly by this test (signed as bob), and B's
        // FollowActivityHandler auto-Accept must not race the Reject back into alice's inbox (the
        // Reject supersedes the auto-Accept — see the test remarks).
        TestServer? bRef = null;
        var a = StartServer(AHost, Alice, aPersistence,
            fetcher: BuildFetcherFor(AHost, Alice, aSeeded.Key, new LazyHandler(() => bRef!.CreateHandler())));
        bRef = StartServer(BHost, Bob, bPersistence,
            fetcher: BuildFetcherFor(BHost, Bob, bSeeded.Key, a.CreateHandler()));
        using var scope = new DisposeBoth(bRef, a);

        // alice follows bob over the wire. B validates alice's signature and records the provisional
        // follow edge (alice → bob) in B's follow store. A also records the follow it is sending in its
        // own store — both the activity (so the RejectActivityHandler can later resolve the follow's
        // target) and alice's own (provisional) follow edge, which the Reject will remove. (The
        // client's DeliverAsync is a plain signed POST; it does not store in A's own store.)
        var follow = BuildFollow(aliceActorIri, bobActorIri);
        await aPersistence.Activities.PutActivityAsync(follow);
        await aPersistence.Follows.RecordFollowAsync(aliceActorIri, bobActorIri);
        using var client = BuildDeliveryClient(aliceActorIri, aSeeded.Key, bRef.CreateHandler());
        var statusCode = await client.DeliverAsync(bobActorIri.InboxOf(), follow);
        Assert.Equal(202, statusCode);
        Assert.True(
            await bPersistence.Follows.IsFollowingAsync(aliceActorIri, bobActorIri),
            "Before the Reject, B should record that alice follows bob");
        Assert.True(
            await aPersistence.Follows.IsFollowingAsync(aliceActorIri, bobActorIri),
            "Before the Reject, A should record alice's own follow of bob");

        // bob rejects the follow. B builds the Reject (object = the original follow, by IRI) with the
        // deterministic IRI bob's actor IRI + /rejects + the follow IRI, and delivers it to alice's
        // inbox over the wire, signed as bob. A validates bob's signature (fetching B's actor doc for
        // bob) and stores the Reject under that IRI.
        var reject = FollowIris.BuildReject(bobActorIri, follow);
        using var bClient = BuildDeliveryClient(bobActorIri, bSeeded.Key, a.CreateHandler());
        var rejectStatusCode = await bClient.DeliverAsync(aliceActorIri.InboxOf(), reject);
        Assert.Equal(202, rejectStatusCode);

        var rejectIri = FollowIris.RejectIri(bobActorIri, follow);
        Assert.True(
            await aPersistence.Activities.TryGetActivityAsync(rejectIri, out var stored),
            "A should have stored the Reject delivered by B over the wire");
        var rejectActivity = Assert.IsType<Reject>(stored!);

        // The Reject's object references the original follow (by IRI) and its actor is bob.
        Assert.NotNull(rejectActivity.Object);
        Assert.Contains(rejectActivity.Object!, o => o is ILink { Href: { } href } && href == new Uri(follow.Id!));
        Assert.NotNull(rejectActivity.Actor);
        Assert.Contains(rejectActivity.Actor!, a => a is ILink { Href: { } href } && href == new Uri(bobActorIri.Value));

        // A's RejectActivityHandler (driven by the inbound Reject, recipient = alice) resolved the
        // follow's target from A's local store and removed the alice → bob edge from A's follow store.
        // This is the assertion that proves the Reject handler is registered and dispatched end-to-end
        // (a dropped handler would leave the edge in place).
        Assert.False(
            await aPersistence.Follows.IsFollowingAsync(aliceActorIri, bobActorIri),
            "After the Reject is delivered back to alice, alice should no longer follow bob in A's follow store");

        // And the removal is visible on alice's public `following` collection: bob is gone. (Invalidate
        // the page-1 cache key first so the endpoint re-renders rather than serving the pre-Reject page.)
        a.Services.GetRequiredService<LocalCollectionPageCache>().Invalidate(aliceFollowingIri);
        using var http = new HttpClient(a.CreateHandler());
        var followingJson = await http.GetStringAsync($"https://{AHost}/ap/v1/u/{Alice}/following");
        var following = ActivityJson.Deserialize<IObjectOrLink>(followingJson);
        var items = (following as OrderedCollection)?.OrderedItems
            ?? (following as OrderedCollectionPage)?.OrderedItems
            ?? [];
        Assert.DoesNotContain(items, i => i is ILink { Href: { } href } && href == new Uri(bobActorIri.Value));
    }

    // --- Delivery signs with the acting actor's key (not the instance actor) ---------
    //
    // The full loop, with B hosting two local actors (bob = instance actor, carol = a second
    // local actor). alice follows carol. B's FollowActivityHandler records the follow and
    // schedules an Accept that is signed as carol (the local actor being followed — the acting
    // actor), NOT as bob (the instance actor). A validates that signature by fetching B's actor
    // doc for carol (resolving carol's key) and stores the Accept. If the worker had (still)
    // signed as the instance actor (bob), A would have resolved bob's key, which does not match
    // the signature, and the Accept would be rejected (401) — never stored. Storing it therefore
    // proves the delivery was signed with carol's key.

    [Fact]
    public async Task Follow_TwoActors_AcceptIsSignedWithActingActorsKey_NotInstanceActor()
    {
        var aPersistence = new InMemoryPersistenceProvider();
        var bPersistence = new InMemoryPersistenceProvider();
        var aSeeded = TestSeeder.SeedPersonWithKey(aPersistence, AHost, Alice);

        // B hosts TWO local actors: bob (the instance actor) and carol (a second local actor).
        var bBob = TestSeeder.SeedPersonWithKey(bPersistence, BHost, Bob);
        var bCarol = TestSeeder.SeedPersonWithKey(bPersistence, BHost, Carol);
        var bobActorIri = bBob.ActorIri;
        var bobInboxIri = bobActorIri.InboxOf();
        var carolActorIri = bCarol.ActorIri;
        var carolInboxIri = carolActorIri.InboxOf();

        // A's fetcher routes to B (lazy, to break the A↔B wiring chicken-and-egg), so A can fetch
        // carol's actor doc to resolve carol's key when validating the Accept.
        TestServer? bRef = null;
        var a = StartServer(AHost, Alice, aPersistence,
            fetcher: BuildFetcherFor(AHost, Alice, aSeeded.Key, new LazyHandler(() => bRef!.CreateHandler())));
        bRef = StartServer(
            BHost, Bob, bPersistence,
            fetcher: BuildFetcherFor(BHost, Bob, bBob.Key, a.CreateHandler()),
            deliveryTransport: () => a.CreateHandler(),
            extraLocalActors: [carolActorIri]);
        using var scope = new DisposeBoth(bRef, a);

        // alice follows carol (not bob — the instance actor).
        var follow = BuildFollow(aSeeded.ActorIri, carolActorIri);
        using var client = BuildDeliveryClient(aSeeded.ActorIri, aSeeded.Key, bRef.CreateHandler());
        var statusCode = await client.DeliverAsync(carolInboxIri, follow);
        Assert.Equal(202, statusCode);

        // B recorded the follow edge (alice → carol).
        Assert.True(
            await bPersistence.Follows.IsFollowingAsync(aSeeded.ActorIri, carolActorIri),
            "B should record that alice follows carol");

        // B's FollowActivityHandler scheduled an Accept (deterministic IRI: carol's actor IRI +
        // /accepts + the follow IRI). B's DeliveryWorker delivers it to alice's inbox, signed as
        // carol (the acting actor). A validates carol's signature (fetching B's actor doc for
        // carol) and stores the Accept.
        var acceptIri = new Iri($"{carolActorIri}/accepts/{follow.Id}");
        await WaitForAsync(
            () => aPersistence.Activities.TryGetActivityAsync(acceptIri, out _),
            timeout: TimeSpan.FromSeconds(10));

        Assert.True(
            await aPersistence.Activities.TryGetActivityAsync(acceptIri, out var stored),
            "A should have stored the Accept signed with carol's key (the acting actor), " +
            "delivered by B's worker");
        var accept = Assert.IsType<Accept>(stored!);
        Assert.NotNull(accept.Actor);
        Assert.Contains(accept.Actor!, a => a is ILink { Href: { } href } && href == new Uri(carolActorIri.Value));
    }

    // --- Announce propagation: a local actor's boost is propagated to a local follower's inbox over the wire --
    //
    // B hosts two local actors (bob = the instance actor, carol = a second local actor) and bob
    // follows carol. carol announces (boosts) an object: a client signed as carol POSTs an Announce to
    // B's inbox. B's SignatureValidationMiddleware validates the signature (resolving carol's key from
    // B's own actor doc), stores the Announce, and B's AnnounceActivityHandler records it in carol's
    // outbox and propagates it to carol's local followers' inboxes (here, bob's inbox) via the
    // DeliveryWorker, signed as carol (the announcer). The propagated copy reuses the deterministic
    // Announce IRI ({carol}/announces/{object}) and is addressed to=bob, cc=carol — so the activity
    // stored under that IRI is the propagated form, and its presence proves the worker delivered it to
    // bob's inbox over the wire and B validated carol's signature on it.

    [Fact]
    public async Task Announce_LocalActorPropagatesToFollowersInbox_SignedWithAnnouncersKey()
    {
        // Self-contained two-instance federation (mirrors the two-actor Accept test): fresh
        // persistence per instance so this test is isolated from the fixture's servers. B hosts two
        // local actors (bob = instance actor, carol = a second local actor).
        var aPersistence = new InMemoryPersistenceProvider();
        var bPersistence = new InMemoryPersistenceProvider();
        var aSeeded = TestSeeder.SeedPersonWithKey(aPersistence, AHost, Alice);
        var bBob = TestSeeder.SeedPersonWithKey(bPersistence, BHost, Bob);
        var bCarol = TestSeeder.SeedPersonWithKey(bPersistence, BHost, Carol);
        var bobActorIri = bBob.ActorIri;
        var carolActorIri = bCarol.ActorIri;
        var carolInboxIri = carolActorIri.InboxOf();

        // bob (B's local instance actor) follows carol (B's second local actor): bob is one of
        // carol's local followers on B.
        await bPersistence.Follows.RecordFollowAsync(bobActorIri, carolActorIri);

        // B's inbound key resolver validates carol's signature by fetching carol's actor doc from B
        // (its own instance) — so B's fetcher is a self-loop (route to B's own handler, lazy to break
        // the wiring chicken-and-egg). B's DeliveryWorker delivers the propagated Announce to bob's
        // inbox (also on B), so B's delivery transport is likewise a self-loop. A is present only as
        // the origin of the announced object (the object IRI is on A); A's fetcher/delivery route to B.
        TestServer? bRef = null;
        var a = StartServer(AHost, Alice, aPersistence,
            fetcher: BuildFetcherFor(AHost, Alice, aSeeded.Key, new LazyHandler(() => bRef!.CreateHandler())),
            // A's delivery transport is a self-safe lazy (A doesn't deliver in this test, but the
            // worker creates its client at startup before bRef is assigned).
            deliveryTransport: () => new LazyHandler(() => bRef!.CreateHandler()));
        bRef = StartServer(BHost, Bob, bPersistence,
            // B's fetcher is a self-loop: B resolves its own actors' keys (bob, carol) by fetching its
            // own actor docs. This is the correct behavior — an instance validating a signature from
            // one of its own actors fetches its own actor doc.
            fetcher: BuildFetcherFor(BHost, Bob, bBob.Key, new LazyHandler(() => bRef!.CreateHandler())),
            // B's DeliveryWorker delivers the propagated Announce to bob's inbox (on B) — self-loop.
            // The worker creates its transport client at startup (before bRef is assigned), so the
            // transport is wrapped in a LazyHandler that defers bRef.CreateHandler() until the first
            // actual delivery (by which point bRef exists).
            deliveryTransport: () => new LazyHandler(() => bRef!.CreateHandler()),
            extraLocalActors: [carolActorIri]);
        using var scope = new DisposeBoth(bRef, a);

        // carol announces (boosts) an object: signed as carol, delivered to carol's inbox on B over the
        // wire. The original announce is addressed to the public (to=Public); the propagated copy
        // (built by the handler) is addressed to=bob, cc=carol.
        var objectIri = new Iri($"https://{AHost}/objects/note-1");
        var announceIri = new Iri($"{carolActorIri}/announces/{objectIri}");
        var announce = new Announce
        {
            Id = announceIri.Value,
            Actor = [new Link { Href = new Uri(carolActorIri.Value) }],
            AttributedTo = [new Link { Href = new Uri(carolActorIri.Value) }],
            Object = [new Link { Href = new Uri(objectIri.Value) }],
            To = [new Link { Href = new Uri(Iri.Public.Value) }],
        };

        // Deliver carol's signed Announce to carol's inbox on B over the wire.
        using var client = BuildDeliveryClient(carolActorIri, bCarol.Key, bRef.CreateHandler());
        var statusCode = await client.DeliverAsync(carolInboxIri, announce);
        Assert.Equal(202, statusCode);

        // B validated carol's signature (resolving carol's key from B's actor doc) and stored the
        // Announce under its deterministic IRI.
        Assert.True(
            await bPersistence.Activities.TryGetActivityAsync(announceIri, out _),
            "B should have stored the Announce after validating carol's signature");

        // B recorded the Announce in carol's (recipient's) outbox.
        var outbox = await bPersistence.Activities.GetOutboxAsync(carolActorIri);
        Assert.Single(outbox);
        Assert.IsType<Announce>(outbox[0]);

        // B's AnnounceActivityHandler propagated the Announce to bob's inbox (bob is carol's local
        // follower). The DeliveryWorker POSTs the propagated copy (to=bob, cc=carol, same
        // deterministic IRI) to bob's inbox over the wire, signed as carol (the announcer); B
        // validates carol's signature (fetching B's actor doc for carol) and stores it under the
        // deterministic IRI — the same IRI as the original announce. Because the propagated copy
        // reuses the deterministic IRI, B's store ends up holding the propagated form (to=bob); wait
        // until the stored activity is the propagated form, which only the worker's delivery produces.
        await WaitForAsync(async () =>
        {
            if (!await bPersistence.Activities.TryGetActivityAsync(announceIri, out var propagated)
                || propagated is not Announce a2)
            {
                return false;
            }

            return (a2.To?.FirstOrDefault() as ILink) is { Href: { } href } && href == new Uri(bobActorIri.Value);
        }, timeout: TimeSpan.FromSeconds(10));

        // The propagated Announce (stored under the deterministic IRI) is addressed to bob, cc'd to
        // carol, and references the announced object — proving the worker delivered it to bob's inbox
        // and B validated carol's signature on the propagated copy.
        Assert.True(
            await bPersistence.Activities.TryGetActivityAsync(announceIri, out var stored),
            "B should have stored the propagated Announce under the deterministic IRI");
        var storedAnnounce = Assert.IsType<Announce>(stored!);
        var toLink = storedAnnounce.To!.First() as ILink;
        Assert.NotNull(toLink);
        Assert.Equal(new Uri(bobActorIri.Value), toLink!.Href); // addressed to the local follower (bob)
        var ccLink = storedAnnounce.Cc!.First() as ILink;
        Assert.NotNull(ccLink);
        Assert.Equal(new Uri(carolActorIri.Value), ccLink!.Href); // cc'd to the announcer (carol)
        Assert.NotNull(storedAnnounce.Object);
        Assert.Contains(storedAnnounce.Object!, o => o is ILink { Href: { } href } && href == objectIri.Uri);
        Assert.NotNull(storedAnnounce.Actor);
        Assert.Contains(storedAnnounce.Actor!, a => a is ILink { Href: { } href } && href == new Uri(carolActorIri.Value));
    }

    // --- Key resolution: B resolves alice's key by fetching A's actor doc --------

    [Fact]
    public async Task Resolver_ResolvesRemoteKey_ByFetchingActorDocumentOverWire()
    {
        var resolver = _b.Services.GetRequiredService<IInboundKeyResolver>();
        var key = await resolver.ResolveAsync(AliceKeyId);
        Assert.True(key is not null,
            "B's IInboundKeyResolver should resolve alice's key by fetching A's actor doc over the wire");
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
        content.Headers.ContentType = new MediaTypeHeaderValue("application/activity+json");
        var response = await http.PostAsync(
            $"https://{BHost}/ap/v1/u/{Bob}/inbox", content);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // --- Negative: a tampered signature is rejected with 401 -------------------

    [Fact]
    public async Task Follow_TamperedSignature_IsRejectedWith401()
    {
        var follow = BuildFollow(AliceActorIri, BobActorIri);
        var json = ActivityJson.Serialize(follow);

        // Deliver to a capture handler that records the signed request, so we can replay it tampered.
        using var capture = new CaptureHandler(_b.CreateHandler());
        using var captureClient = BuildDeliveryClient(AliceActorIri, _aliceKey, capture);
        // (captureClient's transport is the CaptureHandler, which forwards to B's TestServer.)
        _ = await captureClient.DeliverAsync(BobInboxIri, follow);
        var captured = Assert.Single(capture.Captured);

        // Tamper with the body: the original Digest header no longer matches the (changed) body →
        // validation fails → 401. Reuse the signed request's Date + Signature message headers and
        // Digest + Content-Type content headers; only the body changes.
        var tampered = new HttpRequestMessage(HttpMethod.Post, captured.RequestUri!)
        {
            // The body is tampered (a trailing space changes the bytes → the digest mismatches).
            Content = new StringContent(json + " "),
        };

        // Copy the signature-relevant message headers (Date + Signature). Skip Host: HttpClient sets it.
        foreach (var key in new[] { "Date", "Signature" })
        {
            if (captured.Headers.TryGetValue(key, out var values))
            {
                tampered.Headers.TryAddWithoutValidation(key, values);
            }
        }

        // Copy the content headers (Digest + Content-Type) verbatim.
        tampered.Content!.Headers.ContentType = new MediaTypeHeaderValue("application/activity+json");
        if (captured.ContentHeaders.TryGetValue("Digest", out var digests))
        {
            tampered.Content.Headers.TryAddWithoutValidation("Digest", digests);
        }

        using var http = new HttpClient(_b.CreateHandler());
        var response = await http.SendAsync(tampered);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // --- Helpers ----------------------------------------------------------------

    /// <summary>
    /// Builds a delivery <see cref="IActivityPubClient"/> signed with the given key (as the given
    /// actor), whose transport is the given <paramref name="handler"/>.
    /// </summary>
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

    /// <summary>
    /// Builds an <see cref="IActorDocumentFetcher"/> whose client (signed as the given
    /// <paramref name="handle"/>) routes over <paramref name="handler"/> — i.e. B's fetcher reaches
    /// A's actor documents.
    /// </summary>
    private static IActorDocumentFetcher BuildFetcherFor(
        string host, string handle, KeyPair bobKey, HttpMessageHandler handler)
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

    /// <summary>
    /// Starts a single-instance <c>TestServer</c> with the given host/handle/persistence, optionally
    /// overriding the <see cref="IActorDocumentFetcher"/> (for the federation wiring) and the
    /// <c>Func&lt;HttpMessageHandler&gt;</c> delivery transport (so this instance's outbound
    /// <see cref="DeliveryWorker"/> routes to the other in-process <see cref="TestServer"/> instead of
    /// the real network).
    /// </summary>
    private static TestServer StartServer(
        string host, string handle, InMemoryPersistenceProvider persistence,
        IActorDocumentFetcher? fetcher = null,
        Func<HttpMessageHandler>? deliveryTransport = null,
        Action<Microsoft.Extensions.DependencyInjection.IServiceCollection>? extraServices = null,
        IEnumerable<Iri>? extraLocalActors = null)
        => ActivityPubHostFactory.Create(new ActivityPubHostOptions
        {
            Host = host,
            Handle = handle,
            Persistence = persistence,
            Fetcher = fetcher,
            DeliveryTransport = deliveryTransport,
            ExtraServices = extraServices,
            ExtraLocalActors = extraLocalActors,
        });

    private static Follow BuildFollow(Iri actorIri, Iri targetIri)
    {
        // Multi-valued Actor/Object: set via an object initializer of Links (Rule 2 — never a
        // positional constructor; Rule 3 — read multi-valued as IEnumerable).
        var follow = new Follow
        {
            Id = $"https://{AHost}/activities/follow-{Guid.NewGuid():N}",
            Actor = [new Link { Href = new Uri(actorIri.Value) }],
            Object = [new Link { Href = new Uri(targetIri.Value) }],
        };
        return follow;
    }

    /// <summary>
    /// A handler that records the signed request (headers + URI) and forwards it to the inner handler.
    /// Forwarding goes through an <see cref="HttpClient"/> over the inner handler (the handler's
    /// <c>SendAsync</c> is protected and cannot be invoked through a base-typed reference).
    /// </summary>
    private sealed class CaptureHandler(HttpMessageHandler inner) : HttpMessageHandler
    {
        private readonly HttpClient _forward = new(inner, disposeHandler: true);

        /// <summary>
        /// The captured (signed) requests, in order.
        /// </summary>
        public List<CapturedRequest> Captured { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var captured = new CapturedRequest
            {
                RequestUri = request.RequestUri,
                Headers = new Dictionary<string, IList<string>>(),
                ContentHeaders = new Dictionary<string, IList<string>>(),
            };
            foreach (var header in request.Headers)
            {
                captured.Headers[header.Key] = header.Value.ToList();
            }

            if (request.Content is { } content)
            {
                foreach (var header in content.Headers)
                {
                    captured.ContentHeaders[header.Key] = header.Value.ToList();
                }
            }

            Captured.Add(captured);

            // Forward a clone: the outer HttpClient has already marked the original request as
            // sent, so re-sending the same instance through _forward would throw. A clone carries
            // the same method/URI/headers/content (and the signing is already applied upstream).
            var clone = new HttpRequestMessage(request.Method, request.RequestUri)
            {
                Version = request.Version,
            };
            foreach (var header in request.Headers)
            {
                clone.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }

            if (request.Content is { } sourceContent)
            {
                var body = await sourceContent.ReadAsByteArrayAsync(cancellationToken);
                var clonedContent = new ByteArrayContent(body);
                foreach (var header in sourceContent.Headers)
                {
                    clonedContent.Headers.TryAddWithoutValidation(header.Key, header.Value);
                }

                clone.Content = clonedContent;
            }

            return await _forward.SendAsync(clone, cancellationToken);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _forward.Dispose();
            }

            base.Dispose(disposing);
        }
    }

    /// <summary>
    /// A captured HTTP request (URI + headers) for replaying with a tampered body.
    /// </summary>
    private sealed class CapturedRequest
    {
        public Uri? RequestUri { get; init; }

        public Dictionary<string, IList<string>> Headers { get; init; } = new();

        public Dictionary<string, IList<string>> ContentHeaders { get; init; } = new();
    }

    /// <summary>
    /// Awaits until <paramref name="probe"/> returns true or the timeout elapses.
    /// </summary>
    private static async Task WaitForAsync(Func<Task<bool>> probe, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (await probe())
            {
                return;
            }

            await Task.Delay(50);
        }
    }

    /// <summary>
    /// Disposes two <see cref="TestServer"/> instances (for the tests that spin up an extra pair).
    /// </summary>
    private sealed class DisposeBoth(TestServer one, TestServer two) : IDisposable
    {
        public void Dispose()
        {
            one.Dispose();
            two.Dispose();
        }
    }

    /// <summary>
    /// An <see cref="IActorDocumentFetcher"/> that records each fetch (actor IRI + outcome) then
    /// forwards to an inner fetcher. Used to detect whether the inbound key resolver's fetch runs
    /// (and whether it completes) during a signed inbox request.
    /// </summary>
    /// <summary>
    /// An <see cref="HttpMessageHandler"/> that defers resolution of its inner handler until the first
    /// request. Used to break the A↔B wiring chicken-and-egg (A's fetcher needs B's handler; B's
    /// transport needs A's handler) — both servers exist by the time any request flows.
    /// </summary>
    private sealed class LazyHandler(Func<HttpMessageHandler> innerFactory) : HttpMessageHandler
    {
        private readonly Func<HttpMessageHandler> _innerFactory = innerFactory;
        private HttpMessageHandler? _inner;
        private HttpClient? _client;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            _client ??= new HttpClient(_inner ??= _innerFactory(), disposeHandler: false);
            // Clone the request: the inner pipeline may retry (RetryHandler), and HttpClient
            // forbids sending the same request message more than once.
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
                clone.Content = new ByteArrayContent(content.ReadAsByteArrayAsync().GetAwaiter().GetResult());
                foreach (var header in content.Headers)
                {
                    clone.Content.Headers.TryAddWithoutValidation(header.Key, header.Value);
                }
            }

            return _client.SendAsync(clone, cancellationToken);
        }
    }
}
