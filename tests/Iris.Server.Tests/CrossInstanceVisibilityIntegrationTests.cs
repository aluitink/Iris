using System.Net;
using System.Net.Http;
using System.Text.Json;
using Iris.Client;
using Iris.Core;
using Iris.Core.Signing;
using Iris.Server.InMemory;
using Iris.Server.Services;
using Iris.Testing;
using KristofferStrube.ActivityStreams;
using Microsoft.AspNetCore.TestHost;
using Xunit;

namespace Iris.Server.Tests;

/// <summary>
/// Phase 136.18 integration test: privacy and visibility policy alignment. Verifies:
/// (1) a public post (as:Public in to) is visible in the public feed, the author's outbox,
///     and global search on the origin instance;
/// (2) a direct/DM post (specific actor in to, no as:Public, no cc) is stored in the author's
///     outbox on the origin instance and hidden from the origin's public feed + global search for
///     a non-recipient (the S5 read-path filter);
/// (3) a public post federated to a remote instance is visible in the remote instance's object
///     store and resolvable by deep link;
/// (4) a direct/DM post federated to a remote instance is stored on the remote (the S5(b) policy —
///     federation does NOT suppress non-public content on receipt; the content is stored with its
///     <c>to</c>/<c>cc</c> intact and the read-path visibility filter hides it from non-recipients on
///     the receiving instance's public feed + global search, while the named local recipient can
///     still see it).
/// </summary>
[Collection("CrossInstanceVisibility")]
public sealed class CrossInstanceVisibilityIntegrationTests : IAsyncLifetime
{
    internal const string AHost = "vis-a.domain.local";
    internal const string BHost = "vis-b.domain.local";
    internal const string Alice = "alice";
    internal const string Bob = "bob";
    internal const string Carol = "carol";

    private readonly CrossInstanceVisibilitySharedHost _fixture;
    private readonly InMemoryPersistenceProvider _aPersistence;
    private readonly InMemoryPersistenceProvider _bPersistence;
    private KeyPair _aliceKey;
    private KeyPair _carolKey;
    private HttpClient _aHttp;
    private HttpClient _bHttp;

    public CrossInstanceVisibilityIntegrationTests(CrossInstanceVisibilitySharedHost fixture)
    {
        _fixture = fixture;
        _aPersistence = (InMemoryPersistenceProvider)fixture.PersistenceA;
        _bPersistence = (InMemoryPersistenceProvider)fixture.PersistenceB;
        _aliceKey = null!;
        _carolKey = null!;
        _aHttp = null!;
        _bHttp = null!;
    }

    public Task InitializeAsync()
    {
        _fixture.Reset();
        SeedForFixture(_aPersistence, _bPersistence);
        var aliceActorIri = new Iri($"https://{AHost}/ap/v1/u/{Alice}");
        _aPersistence.Keys.TryGetKey(new Iri($"{aliceActorIri.Value}#key-1"), out var aliceKey);
        _aliceKey = (KeyPair)aliceKey!;
        var carolActorIri = new Iri($"https://{BHost}/ap/v1/u/{Carol}");
        _bPersistence.Keys.TryGetKey(new Iri($"{carolActorIri.Value}#key-1"), out var carolKey);
        _carolKey = (KeyPair)carolKey!;
        _aHttp = new HttpClient(_fixture.ServerA.CreateHandler(), disposeHandler: false);
        _bHttp = new HttpClient(_fixture.ServerB.CreateHandler(), disposeHandler: false);
        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        _aHttp.Dispose();
        _bHttp.Dispose();
        return Task.CompletedTask;
    }

    internal static void SeedForFixture(
        InMemoryPersistenceProvider aPersistence, InMemoryPersistenceProvider bPersistence)
    {
        var aliceIri = new Iri($"https://{AHost}/ap/v1/u/{Alice}");
        var bobIri = new Iri($"https://{BHost}/ap/v1/u/{Bob}");
        var carolIri = new Iri($"https://{BHost}/ap/v1/u/{Carol}");
        TestSeeder.SeedPersonWithExistingKey(aPersistence, AHost, Alice, new Iri($"{aliceIri.Value}#key-1"));
        TestSeeder.SeedPersonWithExistingKey(bPersistence, BHost, Bob, new Iri($"{bobIri.Value}#key-1"));
        TestSeeder.SeedPersonWithExistingKey(bPersistence, BHost, Carol, new Iri($"{carolIri.Value}#key-1"));
        // alice (A) follows bob (B) so that public posts from alice federate to bob.
        aPersistence.Follows.RecordFollowAsync(aliceIri, bobIri).GetAwaiter().GetResult();
    }

    // --- 1. Public post is visible in public feed, outbox, and search on origin -----------

    [Fact]
    public async Task PublicPost_VisibleInPublicFeed_Outbox_Search_OnOrigin()
    {
        var aliceIri = new Iri($"https://{AHost}/ap/v1/u/{Alice}");
        var noteIri = new Iri($"https://{AHost}/ap/v1/u/{Alice}/notes/vis-pub-{Guid.NewGuid():N}");
        var create = new Create
        {
            Id = noteIri.Value,
            Actor = [new Link { Href = new Uri(aliceIri.Value) }],
            Object = [new Note
            {
                Id = noteIri.Value,
                Content = ["publicvisibility marker"],
                AttributedTo = [new Link { Href = new Uri(aliceIri.Value) }],
                To = [new Link { Href = new Uri(Iri.Public.Value) }],
            }],
        };

        await DeliverDirectlyAsync(aliceIri, _aliceKey,
            new Iri($"https://{AHost}/ap/v1/u/{Alice}/inbox"), create, () => _fixture.ServerA);

        // The note is in alice's outbox.
        var outbox = await _aPersistence.Activities.GetOutboxAsync(aliceIri);
        Assert.Contains(outbox, i => i is IObject { Id: var id } && id == noteIri.Value);

        // The note is in the public feed (GET /ap/v1/public/feed).
        var feedResp = await _aHttp.GetAsync($"https://{AHost}/ap/v1/public/feed?limit=50");
        feedResp.EnsureSuccessStatusCode();
        var feedDoc = JsonDocument.Parse(await feedResp.Content.ReadAsStringAsync());
        var feedItems = GetItemIds(feedDoc.RootElement);
        Assert.Contains(noteIri.Value, feedItems);

        // The note is in global search (GET /ap/v1/search?q=publicvisibility).
        var searchResp = await _aHttp.GetAsync($"https://{AHost}/ap/v1/search?q=publicvisibility");
        searchResp.EnsureSuccessStatusCode();
        var searchDoc = JsonDocument.Parse(await searchResp.Content.ReadAsStringAsync());
        var searchItems = GetItemIds(searchDoc.RootElement);
        Assert.Contains(noteIri.Value, searchItems);
    }

    // --- 2. Direct/DM post: stored in outbox, hidden from public feed + search (S5) --------

    [Fact]
    public async Task DirectPost_StoredInOutbox_HiddenFromPublicFeedAndSearch()
    {
        var aliceIri = new Iri($"https://{AHost}/ap/v1/u/{Alice}");
        var bobIri = new Iri($"https://{BHost}/ap/v1/u/{Bob}");
        var noteIri = new Iri($"https://{AHost}/ap/v1/u/{Alice}/notes/vis-dm-{Guid.NewGuid():N}");

        // A "direct" post: to = [bob] (no as:Public, no cc).
        var create = new Create
        {
            Id = noteIri.Value,
            Actor = [new Link { Href = new Uri(aliceIri.Value) }],
            Object = [new Note
            {
                Id = noteIri.Value,
                Content = ["directvisibility marker"],
                AttributedTo = [new Link { Href = new Uri(aliceIri.Value) }],
                To = [new Link { Href = new Uri(bobIri.Value) }],
            }],
        };

        await DeliverDirectlyAsync(aliceIri, _aliceKey,
            new Iri($"https://{AHost}/ap/v1/u/{Alice}/inbox"), create, () => _fixture.ServerA);

        // The note is stored in alice's outbox (the author's outbox always contains their posts — the
        // author can always see their own DMs; the outbox is owner-scoped, not a public surface).
        var outbox = await _aPersistence.Activities.GetOutboxAsync(aliceIri);
        Assert.Contains(outbox, i => i is IObject { Id: var id } && id == noteIri.Value);

        // FIXED (139.2-s5): an anonymous request to the public feed must NOT surface the direct post —
        // its audience (to=[bob], no as:Public) names a recipient, so it is non-public and is filtered
        // out for a requester who is not bob.
        var feedResp = await _aHttp.GetAsync($"https://{AHost}/ap/v1/public/feed?limit=50");
        feedResp.EnsureSuccessStatusCode();
        var feedDoc = JsonDocument.Parse(await feedResp.Content.ReadAsStringAsync());
        var feedItems = GetItemIds(feedDoc.RootElement);
        Assert.DoesNotContain(noteIri.Value, feedItems);

        // FIXED (139.2-s5): an anonymous request to global search must NOT find the direct post either.
        var searchResp = await _aHttp.GetAsync($"https://{AHost}/ap/v1/search?q=directvisibility");
        searchResp.EnsureSuccessStatusCode();
        var searchDoc = JsonDocument.Parse(await searchResp.Content.ReadAsStringAsync());
        var searchItems = GetItemIds(searchDoc.RootElement);
        Assert.DoesNotContain(noteIri.Value, searchItems);
    }

    // --- 3. Public post federated to remote is visible in remote object store -------------

    [Fact]
    public async Task PublicPost_FederatedToRemote_VisibleInRemoteObjectStore()
    {
        var aliceIri = new Iri($"https://{AHost}/ap/v1/u/{Alice}");
        var bobIri = new Iri($"https://{BHost}/ap/v1/u/{Bob}");
        var noteIri = new Iri($"https://{AHost}/ap/v1/u/{Alice}/notes/vis-fed-pub-{Guid.NewGuid():N}");
        var create = new Create
        {
            Id = noteIri.Value,
            Actor = [new Link { Href = new Uri(aliceIri.Value) }],
            Object = [new Note
            {
                Id = noteIri.Value,
                Content = ["federatedpublicvisibility marker"],
                AttributedTo = [new Link { Href = new Uri(aliceIri.Value) }],
                To = [new Link { Href = new Uri(Iri.Public.Value) }],
            }],
        };

        // Deliver to bob's inbox on B (simulating federation of alice's public post to bob).
        await DeliverDirectlyAsync(aliceIri, _aliceKey,
            new Iri($"https://{BHost}/ap/v1/u/{Bob}/inbox"), create, () => _fixture.ServerB);

        // B stored the embedded note in its object store (the remote instance's production read path).
        // Note: the object-document HTTP endpoint 404s for cross-instance notes (it reconstructs IRIs
        // from its own BaseUri, so a remote host's IRI does not match B's store — a known limitation
        // documented in 136.16). The object store is the authoritative read path.
        Assert.True(
            await _bPersistence.Objects.TryGetObjectAsync(noteIri, out var storedNote),
            "B should have stored the federated public note in its object store.");
        Assert.NotNull(storedNote);
        Assert.Equal(noteIri.Value, storedNote!.Id);
    }

    // --- 4. Direct/DM post federated to remote IS stored (S5(b): no suppression on receipt) -

    [Fact]
    public async Task DirectPost_FederatedToRemote_StoredOnRemote_AudienceIntact()
    {
        var aliceIri = new Iri($"https://{AHost}/ap/v1/u/{Alice}");
        var bobIri = new Iri($"https://{BHost}/ap/v1/u/{Bob}");
        var noteIri = new Iri($"https://{AHost}/ap/v1/u/{Alice}/notes/vis-fed-dm-{Guid.NewGuid():N}");

        // A "direct" post: to = [bob] (no as:Public, no cc).
        var create = new Create
        {
            Id = noteIri.Value,
            Actor = [new Link { Href = new Uri(aliceIri.Value) }],
            Object = [new Note
            {
                Id = noteIri.Value,
                Content = ["federateddirectvisibility marker"],
                AttributedTo = [new Link { Href = new Uri(aliceIri.Value) }],
                To = [new Link { Href = new Uri(bobIri.Value) }],
            }],
        };

        // Deliver to bob's inbox on B.
        await DeliverDirectlyAsync(aliceIri, _aliceKey,
            new Iri($"https://{BHost}/ap/v1/u/{Bob}/inbox"), create, () => _fixture.ServerB);

        // S5(b) policy (pinned here): the direct post IS stored on the remote instance — federation
        // does not suppress non-public content on receipt. The CreateActivityHandler stores the
        // embedded object unconditionally (no inbound audience check), and the inbound path does NOT
        // run the outbound audience rewrite (RewriteOutboundAudienceAsync is outbound-only), so the
        // stored note keeps its original to/cc intact. The read-path visibility filter (S5) is what
        // hides it from non-recipients on B's public feed + search — see the S5(b) read-path tests
        // below.
        Assert.True(
            await _bPersistence.Objects.TryGetObjectAsync(noteIri, out var storedNote),
            "B stored the federated direct post (S5(b): no visibility-based suppression on receipt).");
        Assert.NotNull(storedNote);

        // The stored note's audience is intact: `to` still names bob (the original recipient), and it
        // was NOT clobbered by an outbound-rewrite (no extra followers appended to cc on the inbound
        // storage path).
        var stored = storedNote!;
        Assert.NotNull(stored.To);
        Assert.Contains(stored.To!, e => e.ResolveObjectIri() is { } iri && iri.Value == bobIri.Value);
    }

    // --- 5. S5(b) federation visibility policy: read-path on the receiving instance ---------
    //
    // The S5(b) decision (pinned by this test family): a non-public post (direct / followers-only)
    // federated to a remote instance is STORED on the remote with its to/cc intact (no suppression
    // on receipt), and the read-path visibility filter (S5) hides it from the remote instance's
    // public feed + global search for any requester who is not a named recipient — while the named
    // local recipient (bob, on B) can still see it. This is the federation counterpart to the S5
    // local read-path filter: the same predicate applies whether the content originated locally or
    // arrived by federation.

    [Fact]
    public async Task FederatedDm_HiddenFromRemotePublicFeed_ForAnonymous()
    {
        var aliceIri = new Iri($"https://{AHost}/ap/v1/u/{Alice}");
        var bobIri = new Iri($"https://{BHost}/ap/v1/u/{Bob}");
        var noteIri = new Iri($"https://{AHost}/ap/v1/u/{Alice}/notes/s5b-dm-feed-{Guid.NewGuid():N}");

        var create = new Create
        {
            Id = noteIri.Value,
            Actor = [new Link { Href = new Uri(aliceIri.Value) }],
            Object = [new Note
            {
                Id = noteIri.Value,
                Content = ["s5b-dm-feed marker"],
                AttributedTo = [new Link { Href = new Uri(aliceIri.Value) }],
                To = [new Link { Href = new Uri(bobIri.Value) }],
            }],
        };

        await DeliverDirectlyAsync(aliceIri, _aliceKey,
            new Iri($"https://{BHost}/ap/v1/u/{Bob}/inbox"), create, () => _fixture.ServerB);

        // The DM is in bob's outbox on B (the CreateActivityHandler records it in the recipient's
        // outbox), so it is a candidate for B's public feed. But an anonymous request to B's public
        // feed must NOT surface it — its audience (to=[bob], no as:Public) names a recipient, so it
        // is non-public and the S5 filter drops it for a requester who is not bob.
        var feedResp = await _bHttp.GetAsync($"https://{BHost}/ap/v1/public/feed?limit=100");
        feedResp.EnsureSuccessStatusCode();
        var feedDoc = JsonDocument.Parse(await feedResp.Content.ReadAsStringAsync());
        var feedItems = GetItemIds(feedDoc.RootElement);
        Assert.DoesNotContain(noteIri.Value, feedItems);
    }

    [Fact]
    public async Task FederatedDm_VisibleInRemotePublicFeed_ToNamedRecipient()
    {
        var aliceIri = new Iri($"https://{AHost}/ap/v1/u/{Alice}");
        var bobIri = new Iri($"https://{BHost}/ap/v1/u/{Bob}");
        var noteIri = new Iri($"https://{AHost}/ap/v1/u/{Alice}/notes/s5b-dm-recv-{Guid.NewGuid():N}");

        var create = new Create
        {
            Id = noteIri.Value,
            Actor = [new Link { Href = new Uri(aliceIri.Value) }],
            Object = [new Note
            {
                Id = noteIri.Value,
                Content = ["s5b-dm-recv marker"],
                AttributedTo = [new Link { Href = new Uri(aliceIri.Value) }],
                To = [new Link { Href = new Uri(bobIri.Value) }],
            }],
        };

        await DeliverDirectlyAsync(aliceIri, _aliceKey,
            new Iri($"https://{BHost}/ap/v1/u/{Bob}/inbox"), create, () => _fixture.ServerB);

        // bob is the named recipient (to=[bob]) and a local actor on B. When bob requests B's public
        // feed (signed, so the requester resolves to bob), the S5 filter keeps the DM — the
        // recipient can see content addressed to them, even when it arrived by federation.
        var feed = new PublicFeedService(_bPersistence);
        var items = await feed.GetPublicFeedAsync(100, requesterIri: bobIri);
        var ids = items
            .Where(i => i is IObject { Id: { } id })
            .Select(i => (i as IObject)!.Id!)
            .ToList();
        Assert.Contains(noteIri.Value, ids);
    }

    [Fact]
    public async Task FederatedDm_NotFoundInRemoteGlobalSearch_ForAnonymous()
    {
        var aliceIri = new Iri($"https://{AHost}/ap/v1/u/{Alice}");
        var bobIri = new Iri($"https://{BHost}/ap/v1/u/{Bob}");
        var noteIri = new Iri($"https://{AHost}/ap/v1/u/{Alice}/notes/s5b-dm-search-{Guid.NewGuid():N}");

        var create = new Create
        {
            Id = noteIri.Value,
            Actor = [new Link { Href = new Uri(aliceIri.Value) }],
            Object = [new Note
            {
                Id = noteIri.Value,
                Content = ["s5b-dm-search marker"],
                AttributedTo = [new Link { Href = new Uri(aliceIri.Value) }],
                To = [new Link { Href = new Uri(bobIri.Value) }],
            }],
        };

        await DeliverDirectlyAsync(aliceIri, _aliceKey,
            new Iri($"https://{BHost}/ap/v1/u/{Bob}/inbox"), create, () => _fixture.ServerB);

        // An anonymous request to B's global search must NOT find the DM — the S5 filter hides
        // non-public content from anonymous requesters on the search surface too.
        var searchResp = await _bHttp.GetAsync($"https://{BHost}/ap/v1/search?q=s5b-dm-search");
        searchResp.EnsureSuccessStatusCode();
        var searchDoc = JsonDocument.Parse(await searchResp.Content.ReadAsStringAsync());
        var searchItems = GetItemIds(searchDoc.RootElement);
        Assert.DoesNotContain(noteIri.Value, searchItems);
    }

    [Fact]
    public async Task FederatedDm_FoundInRemoteGlobalSearch_ByNamedRecipient()
    {
        var aliceIri = new Iri($"https://{AHost}/ap/v1/u/{Alice}");
        var bobIri = new Iri($"https://{BHost}/ap/v1/u/{Bob}");
        var noteIri = new Iri($"https://{AHost}/ap/v1/u/{Alice}/notes/s5b-dm-srch-{Guid.NewGuid():N}");

        var create = new Create
        {
            Id = noteIri.Value,
            Actor = [new Link { Href = new Uri(aliceIri.Value) }],
            Object = [new Note
            {
                Id = noteIri.Value,
                Content = ["s5b-dm-srch marker"],
                AttributedTo = [new Link { Href = new Uri(aliceIri.Value) }],
                To = [new Link { Href = new Uri(bobIri.Value) }],
            }],
        };

        await DeliverDirectlyAsync(aliceIri, _aliceKey,
            new Iri($"https://{BHost}/ap/v1/u/{Bob}/inbox"), create, () => _fixture.ServerB);

        // bob is the named recipient and a local actor on B. When bob searches B's global search
        // (signed, so the requester resolves to bob), the S5 filter keeps the DM — the recipient can
        // find content addressed to them, even when it arrived by federation.
        var search = new GlobalSearchService(_bPersistence);
        var items = await search.SearchAsync("s5b-dm-srch", requesterIri: bobIri);
        var ids = items
            .Where(i => i is IObject { Id: { } id })
            .Select(i => (i as IObject)!.Id!)
            .ToList();
        Assert.Contains(noteIri.Value, ids);
    }

    // --- 6. S5b object-document gate: federated-in non-public content is gated on the
    //        receiving instance (139.2-s5a/s5b — the same privacy boundary applies whether
    //        content originated locally or arrived by federation).
    //
    // A non-public (DM / followers-only) post federated to a remote instance is stored on the
    // remote with its to/cc intact (no suppression on receipt), and the read-path visibility
    // filter (S5) hides it from the remote instance's object-document endpoint for any requester
    // who is not a named recipient — while the named local recipient (bob, on B) and the author
    // (alice, on A, signed) can still see it. A 404 (not 403) hides the object's existence.

    [Fact]
    public async Task FederatedDm_ObjectDocument_404_ForAnonymous()
    {
        var aliceIri = new Iri($"https://{AHost}/ap/v1/u/{Alice}");
        var bobIri = new Iri($"https://{BHost}/ap/v1/u/{Bob}");
        var noteIri = new Iri($"https://{AHost}/ap/v1/u/{Alice}/notes/s5b-objdoc-anon-{Guid.NewGuid():N}");

        var create = new Create
        {
            Id = noteIri.Value,
            Actor = [new Link { Href = new Uri(aliceIri.Value) }],
            Object = [new Note
            {
                Id = noteIri.Value,
                Content = ["s5b-objdoc-anon marker"],
                AttributedTo = [new Link { Href = new Uri(aliceIri.Value) }],
                To = [new Link { Href = new Uri(bobIri.Value) }],
            }],
        };

        await DeliverDirectlyAsync(aliceIri, _aliceKey,
            new Iri($"https://{BHost}/ap/v1/u/{Bob}/inbox"), create, () => _fixture.ServerB);

        // The DM is stored on B (S5b: no suppression on receipt). But an anonymous request to B's
        // object-document endpoint (via ?iri= for the foreign IRI) must NOT serve it — its audience
        // (to=[bob], no as:Public) names a recipient, so it is non-public and the S5 filter hides it
        // from a requester who is not a named recipient. A 404 hides the object's existence.
        var resp = await _bHttp.GetAsync(
            $"https://{BHost}/ap/v1/object?iri={Uri.EscapeDataString(noteIri.Value)}");
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    [Fact]
    public async Task FederatedDm_ObjectDocument_200_ForNamedRecipient()
    {
        var aliceIri = new Iri($"https://{AHost}/ap/v1/u/{Alice}");
        var bobIri = new Iri($"https://{BHost}/ap/v1/u/{Bob}");
        var noteIri = new Iri($"https://{AHost}/ap/v1/u/{Alice}/notes/s5b-objdoc-recv-{Guid.NewGuid():N}");

        var create = new Create
        {
            Id = noteIri.Value,
            Actor = [new Link { Href = new Uri(aliceIri.Value) }],
            Object = [new Note
            {
                Id = noteIri.Value,
                Content = ["s5b-objdoc-recv marker"],
                AttributedTo = [new Link { Href = new Uri(aliceIri.Value) }],
                To = [new Link { Href = new Uri(bobIri.Value) }],
            }],
        };

        await DeliverDirectlyAsync(aliceIri, _aliceKey,
            new Iri($"https://{BHost}/ap/v1/u/{Bob}/inbox"), create, () => _fixture.ServerB);

        // bob is the named recipient (to=[bob]) and a local actor on B. When bob requests B's
        // object-document endpoint (signed, so the requester resolves to bob), the S5 filter keeps
        // the DM — the recipient can see content addressed to them, even when it arrived by
        // federation. The ?iri= param is required because the note's IRI is on A's host (foreign),
        // so path-based reconstruction on B would not match the stored IRI. The object is served (200).
        var bobKey = _bobKeyForTest();
        var keyStore = new InMemoryKeyStore();
        keyStore.PutKey(bobKey);
        var keyProvider = new InMemoryKeyProvider(keyStore);
        keyProvider.RegisterKey(bobIri, bobKey.KeyId);
        var signer = new HttpSignatureSigner(keyStore);
        var factory = new ActivityPubClientFactory(keyStore, keyProvider, signer);
        var bobClient = factory.Create(
            new ActivityPubClientOptions { ActorId = bobIri, EnableRetry = false },
            new LazyHandler(() => _fixture.ServerB.CreateHandler()));

        using var req = new HttpRequestMessage(
            HttpMethod.Get,
            $"https://{BHost}/ap/v1/object?iri={Uri.EscapeDataString(noteIri.Value)}");
        using var resp = await bobClient.SendAsync(req);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
    }

    [Fact]
    public async Task FederatedDm_ObjectDocument_404_ForNonRecipientLocalActor()
    {
        var aliceIri = new Iri($"https://{AHost}/ap/v1/u/{Alice}");
        var bobIri = new Iri($"https://{BHost}/ap/v1/u/{Bob}");
        var carolIri = new Iri($"https://{BHost}/ap/v1/u/{Carol}");
        var noteIri = new Iri($"https://{AHost}/ap/v1/u/{Alice}/notes/s5b-objdoc-nonrec-{Guid.NewGuid():N}");

        var create = new Create
        {
            Id = noteIri.Value,
            Actor = [new Link { Href = new Uri(aliceIri.Value) }],
            Object = [new Note
            {
                Id = noteIri.Value,
                Content = ["s5b-objdoc-nonrec marker"],
                AttributedTo = [new Link { Href = new Uri(aliceIri.Value) }],
                To = [new Link { Href = new Uri(bobIri.Value) }],
            }],
        };

        await DeliverDirectlyAsync(aliceIri, _aliceKey,
            new Iri($"https://{BHost}/ap/v1/u/{Bob}/inbox"), create, () => _fixture.ServerB);

        // carol is a local actor on B but NOT a named recipient (to=[bob], carol is not in to/cc
        // and is not the author). When carol requests B's object-document endpoint (signed, so the
        // requester resolves to carol), the S5 filter hides the DM — carol is not a recipient and
        // not the author. A 404 hides the object's existence. The ?iri= param is required because
        // the note's IRI is on A's host (foreign), so path-based reconstruction on B would not
        // match the stored IRI.
        var keyStore = new InMemoryKeyStore();
        keyStore.PutKey(_carolKey);
        var keyProvider = new InMemoryKeyProvider(keyStore);
        keyProvider.RegisterKey(carolIri, _carolKey.KeyId);
        var signer = new HttpSignatureSigner(keyStore);
        var factory = new ActivityPubClientFactory(keyStore, keyProvider, signer);
        var carolClient = factory.Create(
            new ActivityPubClientOptions { ActorId = carolIri, EnableRetry = false },
            new LazyHandler(() => _fixture.ServerB.CreateHandler()));

        using var req = new HttpRequestMessage(
            HttpMethod.Get,
            $"https://{BHost}/ap/v1/object?iri={Uri.EscapeDataString(noteIri.Value)}");
        using var resp = await carolClient.SendAsync(req);
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    // --- Helpers --------------------------------------------------------------------------

    private KeyPair _bobKeyForTest()
    {
        var bobIri = new Iri($"https://{BHost}/ap/v1/u/{Bob}");
        _bPersistence.Keys.TryGetKey(new Iri($"{bobIri.Value}#key-1"), out var k);
        return (KeyPair)k!;
    }

    private static async Task<bool> DeliverDirectlyAsync(
        Iri actorIri, KeyPair key, Iri inbox, Activity activity, Func<TestServer> target)
    {
        var keyStore = new InMemoryKeyStore();
        keyStore.PutKey(key);
        var keyProvider = new InMemoryKeyProvider(keyStore);
        keyProvider.RegisterKey(actorIri, key.KeyId);
        var signer = new HttpSignatureSigner(keyStore);
        var factory = new ActivityPubClientFactory(keyStore, keyProvider, signer);
        var client = factory.Create(
            new ActivityPubClientOptions { ActorId = actorIri, EnableRetry = false },
            new LazyHandler(() => target().CreateHandler()));

        var result = await client.DeliverAsync(inbox, activity, CancellationToken.None);
        return result.IsSuccess;
    }

    private static List<string> GetItemIds(JsonElement root)
    {
        var ids = new List<string>();
        // Prefer `orderedItems` (the canonical AS2.0 form, 139.1 F-7); fall back to `items`.
        var items = root.TryGetProperty("orderedItems", out var ordered)
            ? ordered
            : root.TryGetProperty("items", out var plain) ? plain : default;
        if (items.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in items.EnumerateArray())
            {
                if (item.TryGetProperty("id", out var id))
                {
                    ids.Add(id.GetString()!);
                }
            }
        }
        return ids;
    }
}

/// <summary>
/// Shared two-host fixture for the visibility tests.
/// </summary>
public sealed class CrossInstanceVisibilitySharedHost : SharedTwoHostFixture
{
    public CrossInstanceVisibilitySharedHost()
        : base(BuildOptions(out _, out _))
    {
    }

    private static (ActivityPubHostOptions A, ActivityPubHostOptions B) BuildOptions(
        out InMemoryPersistenceProvider aPersistence, out InMemoryPersistenceProvider bPersistence)
    {
        aPersistence = new InMemoryPersistenceProvider();
        bPersistence = new InMemoryPersistenceProvider();

        var alice = TestSeeder.SeedPersonWithKey(
            aPersistence, CrossInstanceVisibilityIntegrationTests.AHost,
            CrossInstanceVisibilityIntegrationTests.Alice);
        var bob = TestSeeder.SeedPersonWithKey(
            bPersistence, CrossInstanceVisibilityIntegrationTests.BHost,
            CrossInstanceVisibilityIntegrationTests.Bob);
        TestSeeder.SeedPersonWithKey(
            bPersistence, CrossInstanceVisibilityIntegrationTests.BHost,
            CrossInstanceVisibilityIntegrationTests.Carol);

        var serverARef = SharedHostFixture.ServerRefFor(aPersistence);
        var serverBRef = SharedHostFixture.ServerRefFor(bPersistence);

        var optionsA = new ActivityPubHostOptions
        {
            Host = CrossInstanceVisibilityIntegrationTests.AHost,
            Handle = CrossInstanceVisibilityIntegrationTests.Alice,
            Persistence = aPersistence,
            IdentityKeys = BuildIdentity(alice.Key, alice.ActorIri, alice.KeyId),
            Fetcher = new VisibilityRoutingFetcher(
                CrossInstanceVisibilityIntegrationTests.AHost,
                () => serverARef().CreateHandler(),
                CrossInstanceVisibilityIntegrationTests.BHost,
                () => serverBRef().CreateHandler(),
                alice.Key, alice.ActorIri),
            DeliveryTransport = () => new LazyHandler(() => serverBRef().CreateHandler()),
        };

        var optionsB = new ActivityPubHostOptions
        {
            Host = CrossInstanceVisibilityIntegrationTests.BHost,
            Handle = CrossInstanceVisibilityIntegrationTests.Bob,
            Persistence = bPersistence,
            IdentityKeys = BuildIdentity(bob.Key, bob.ActorIri, bob.KeyId),
            Fetcher = new VisibilityRoutingFetcher(
                CrossInstanceVisibilityIntegrationTests.AHost,
                () => serverARef().CreateHandler(),
                CrossInstanceVisibilityIntegrationTests.BHost,
                () => serverBRef().CreateHandler(),
                bob.Key, bob.ActorIri),
            DeliveryTransport = () => new LazyHandler(() => serverARef().CreateHandler()),
        };

        return (optionsA, optionsB);
    }

    private static IdentityKeys BuildIdentity(KeyPair key, Iri actorIri, Iri keyId)
    {
        var store = new InMemoryKeyStore();
        store.PutKey(key);
        var provider = new InMemoryKeyProvider(store);
        provider.RegisterKey(actorIri, keyId);
        var signer = new HttpSignatureSigner(store);
        return new IdentityKeys(store, provider, signer);
    }
}

/// <summary>
/// An <see cref="IActorDocumentFetcher"/> that routes to the correct instance based on the
/// actor IRI's host.
/// </summary>
file sealed class VisibilityRoutingFetcher : IActorDocumentFetcher
{
    private readonly Dictionary<string, IActorDocumentFetcher> _fetchers;

    public VisibilityRoutingFetcher(
        string aHost, Func<HttpMessageHandler> aHandlerFactory,
        string bHost, Func<HttpMessageHandler> bHandlerFactory,
        KeyPair signingKey, Iri signingActor)
    {
        _fetchers = new Dictionary<string, IActorDocumentFetcher>(StringComparer.OrdinalIgnoreCase)
        {
            [aHost] = BuildFetcherFor(aHost, aHandlerFactory, signingKey, signingActor),
            [bHost] = BuildFetcherFor(bHost, bHandlerFactory, signingKey, signingActor),
        };
    }

    public Task<Actor?> GetActorAsync(Iri actorIri, CancellationToken ct = default)
    {
        var host = new Uri(actorIri.Value).Host;
        if (_fetchers.TryGetValue(host, out var fetcher))
        {
            return fetcher.GetActorAsync(actorIri, ct);
        }

        return Task.FromResult<Actor?>(null);
    }

    private static IActorDocumentFetcher BuildFetcherFor(
        string host, Func<HttpMessageHandler> handlerFactory, KeyPair key, Iri actorIri)
    {
        var keyStore = new InMemoryKeyStore();
        keyStore.PutKey(key);
        var keyProvider = new InMemoryKeyProvider(keyStore);
        keyProvider.RegisterKey(actorIri, key.KeyId);
        var signer = new HttpSignatureSigner(keyStore);
        var factory = new ActivityPubClientFactory(keyStore, keyProvider, signer);
        var client = factory.Create(
            new ActivityPubClientOptions { ActorId = actorIri, EnableRetry = false },
            new LazyHandler(handlerFactory));
        return new IrisActorDocumentFetcher(client, new RemoteActorCache());
    }
}

/// <summary>xUnit collection definition.</summary>
[CollectionDefinition("CrossInstanceVisibility")]
public sealed class CrossInstanceVisibilityCollection : ICollectionFixture<CrossInstanceVisibilitySharedHost>
{
}
