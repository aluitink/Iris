using System.Net;
using System.Text.Json;
using Iris.Client;
using Iris.Core;
using Iris.Server.InMemory;
using Iris.Testing;
using KristofferStrube.ActivityStreams;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Iris.Server.Tests.Services;

/// <summary>
/// Integration test for the community feed's **remote-member** support: a community on instance A has a
/// local member (alice) and a remote member (bob on instance B). The community feed endpoint
/// <c>GET /ap/v1/c/{name}/feed</c> merges alice's outbox (read from A's local activity store) with bob's
/// outbox (fetched over the wire from B) into a single newest-first, de-duplicated feed. A remote
/// member whose outbox cannot be fetched contributes nothing — a single broken remote must not fail the
/// whole feed.
/// </summary>
/// <remarks>
/// Topology: instance A (a.domain.local, actor <c>alice</c> + community <c>iris</c>) and instance B
/// (b.domain.local, remote member <c>bob</c>). bob is a member of the community on A (recorded in A's
/// community store) but lives on B (his outbox is served by B's <c>TestServer</c>). A's
/// <see cref="ICommunityFeedService"/> is overridden (via <c>ExtraServices</c>) so its outbound
/// outbox-fetch client routes to B's in-process <c>TestServer</c> (the production registration
/// hardcodes a real <c>HttpClientHandler</c>, which cannot reach an in-process <c>TestServer</c>).
/// alice's outbox on A is seeded with one post; bob's outbox on B with two posts. The merged feed must
/// contain all three (one from local A, two from remote B).
/// </remarks>
public sealed class CommunityFeedRemoteMemberIntegrationTests : IDisposable
{
    private const string AHost = "a.domain.local";
    private const string BHost = "b.domain.local";
    private const string Community = "iris";
    private const string Alice = "alice";
    private const string Bob = "bob";

    private readonly TestServer _a;
    private readonly TestServer _b;
    private readonly InMemoryPersistenceProvider _aPersistence;
    private readonly InMemoryPersistenceProvider _bPersistence;
    private readonly HttpClient _http;
    private readonly Iri _community;
    private readonly Iri _alice;
    private readonly Iri _bob;

    public CommunityFeedRemoteMemberIntegrationTests()
    {
        _aPersistence = new InMemoryPersistenceProvider();
        _bPersistence = new InMemoryPersistenceProvider();

        // A hosts alice (local member) + community iris. B hosts bob (remote member).
        var (aKey, aliceIri, _) = TestSeeder.SeedPersonWithKey(_aPersistence, AHost, Alice);
        var (bKey, bobIri, _) = TestSeeder.SeedPersonWithKey(_bPersistence, BHost, Bob);
        _alice = aliceIri;
        _bob = bobIri;
        _community = TestSeeder.SeedCommunity(_aPersistence, AHost, Community);

        // alice (local) and bob (remote) are both members of the community on A.
        _aPersistence.Communities.AddMemberAsync(_community, _alice).GetAwaiter().GetResult();
        _aPersistence.Communities.AddMemberAsync(_community, _bob).GetAwaiter().GetResult();

        // alice's outbox (on A): one post. bob's outbox (on B): two posts.
        TestSeeder.AddCreateActivity(_aPersistence, _alice, $"{_alice.Value}/activities/a-1", "alice post 1");
        TestSeeder.AddCreateActivity(_bPersistence, _bob, $"{_bob.Value}/activities/b-1", "bob post 1");
        TestSeeder.AddCreateActivity(_bPersistence, _bob, $"{_bob.Value}/activities/b-2", "bob post 2");

        // B hosts bob; its outbox is a local collection endpoint (no outbound fetches needed).
        _b = ActivityPubHostFactory.Create(new ActivityPubHostOptions
        {
            Host = BHost,
            Handle = Bob,
            Persistence = _bPersistence,
        });

        // A hosts alice + community iris. The production ICommunityFeedService registration hardcodes a
        // real HttpClientHandler (which cannot reach an in-process TestServer), so we override it (and
        // the IActorDocumentFetcher it resolves) to route outbound fetches to B's in-process TestServer.
        // alice's key is registered with a B-wired client factory so the outbox fetch is signed as
        // alice (B's signature-validation middleware accepts it).
        var bHandler = _b.CreateHandler();
        var aKeyStore = new InMemoryKeyStore();
        aKeyStore.PutKey(aKey);
        var aKeyProvider = new InMemoryKeyProvider(aKeyStore);
        aKeyProvider.RegisterKey(_alice, new Iri($"{_alice.Value}#key-1"));
        var aSigner = new HttpSignatureSigner(aKeyStore);
        var bWiredClientFactory = new ActivityPubClientFactory(aKeyStore, aKeyProvider, aSigner);

        _a = ActivityPubHostFactory.Create(new ActivityPubHostOptions
        {
            Host = AHost,
            Handle = Alice,
            Persistence = _aPersistence,
            RegisterLocalKey = false,
            ExtraServices = s =>
            {
                // The client signs as alice and reaches B over the wire.
                var bClient = bWiredClientFactory.Create(
                    new ActivityPubClientOptions { ActorId = _alice, EnableRetry = false },
                    bHandler);

                // The document fetcher resolves bob's public key (and bob's outbox link) from B.
                // It must be registered (not just captured) so the CommunityFeedService factory resolves
                // it from DI (the production registration would otherwise be used).
                s.AddSingleton<IActorDocumentFetcher>(sp => new IrisActorDocumentFetcher(
                    bClient,
                    sp.GetRequiredService<RemoteActorCache>()));

                s.AddSingleton<ICommunityFeedService>(sp =>
                {
                    var persistence = sp.GetRequiredService<IPersistenceProvider>();
                    return new CommunityFeedService(
                        persistence,
                        persistence.Communities,
                        sp.GetRequiredService<ILocalActorResolver>(),
                        sp.GetRequiredService<IActorDocumentFetcher>(),
                        bClient,
                        sp.GetRequiredService<IOptions<FeedOptions>>().Value);
                });
            },
        });

        _http = new HttpClient(_a.CreateHandler(), disposeHandler: false);
    }

    public void Dispose()
    {
        _http.Dispose();
        _a.Dispose();
        _b.Dispose();
    }

    // --- The feed merges local + remote member outboxes ---------------------------

    [Fact]
    public async Task Feed_MergesLocalAndRemoteMemberOutboxes()
    {
        var response = await _http.GetAsync($"https://{AHost}/ap/v1/c/{Community}/feed?limit=10");
        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(json);

        var items = JsonDoc.GetItems(doc.RootElement).Select(e => JsonDoc.ItemId(e)).ToArray();

        // alice (local, 1 post) + bob (remote, 2 posts) = 3 items total.
        Assert.Equal(3, items.Length);

        // All three posts are present: alice's post from A, bob's two posts from B.
        Assert.Contains($"{_alice.Value}/activities/a-1", items);
        Assert.Contains($"{_bob.Value}/activities/b-1", items);
        Assert.Contains($"{_bob.Value}/activities/b-2", items);

        // The feed is newest-first, sorted by (object date, then member IRI). All three posts share
        // the same default date (TestSeeder.AddCreateActivity sets no explicit date), so the sort
        // falls back to the member IRI (ascending): alice < bob, and within bob b-2 < b-1.
        Assert.Equal($"{_alice.Value}/activities/a-1", items[0]);
        Assert.Equal($"{_bob.Value}/activities/b-2", items[1]);
        Assert.Equal($"{_bob.Value}/activities/b-1", items[2]);
    }

    [Fact]
    public async Task Feed_OnlyLocalMember_RemoteNotMember()
    {
        // A second community with only alice as a member (bob is NOT a member).
        var community2 = TestSeeder.SeedCommunity(_aPersistence, AHost, "solo");
        await _aPersistence.Communities.AddMemberAsync(community2, _alice);

        var response = await _http.GetAsync($"https://{AHost}/ap/v1/c/solo/feed?limit=10");
        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(json);

        var items = JsonDoc.GetItems(doc.RootElement).Select(e => JsonDoc.ItemId(e)).ToArray();

        // Only alice's post (bob is not a member of "solo").
        Assert.Single(items);
        Assert.Equal($"{_alice.Value}/activities/a-1", items[0]);
    }

    [Fact]
    public async Task Feed_RemoteMemberOutboxUnavailable_ContributesNothing()
    {
        // A third community with alice (local) + dave (remote, on an unreachable host).
        var daveIri = new Iri($"https://unreachable.domain.local/ap/v1/u/dave");
        var community3 = TestSeeder.SeedCommunity(_aPersistence, AHost, "mixed");
        await _aPersistence.Communities.AddMemberAsync(community3, _alice);
        await _aPersistence.Communities.AddMemberAsync(community3, daveIri);

        var response = await _http.GetAsync($"https://{AHost}/ap/v1/c/mixed/feed?limit=10");
        // The feed must still return 200 (a broken remote must not fail the whole feed).
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(json);

        var items = JsonDoc.GetItems(doc.RootElement).Select(e => JsonDoc.ItemId(e)).ToArray();

        // alice's post is present; dave's (unreachable) outbox contributes nothing.
        Assert.Single(items);
        Assert.Equal($"{_alice.Value}/activities/a-1", items[0]);
    }
}
