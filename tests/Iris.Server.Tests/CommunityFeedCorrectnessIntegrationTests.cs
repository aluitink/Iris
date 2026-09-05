using System.Net;
using System.Text.Json;
using Iris.Core;
using Iris.Server.InMemory;
using Iris.Testing;
using Microsoft.AspNetCore.TestHost;
using Xunit;

namespace Iris.Server.Tests;

/// <summary>
/// Phase 19.5.5 integration test for the <strong>community feed correctness</strong>: the unified
/// community feed (<c>GET /ap/v1/c/{name}/feed</c>) must yield <em>exactly</em> the right activities —
/// the union of the local members' outbox activities, <strong>newest first</strong>, de-duplicated by
/// IRI, paged, with an empty feed for a member-less community and a 404 for an unknown community.
/// </summary>
/// <remarks>
/// Topology: a single instance (a.domain.local) hosts a community <c>iris</c> with two local members
/// (alice, bob) whose outboxes carry posts with different recency. The feed is the <em>merge</em> of the
/// members' outboxes (each already newest-first), so its order is by (outbox position, then member IRI) —
/// a member's newest post ranks above its older posts, and two posts at the same outbox position are
/// ordered by member IRI. The tests pin: the newest-first merge (a member's newest post appears before a
/// different member's older post at the same position); de-duplication of an activity recorded in more
/// than one member's outbox; pagination (page 1 <c>OrderedCollection</c>, page 2
/// <c>OrderedCollectionPage</c> with <c>prev</c>/<c>next</c>); an empty feed; and an unknown community
/// 404. The remote-content half of the feed (content delivered to the community inbox and propagated to a
/// member's outbox) is covered by <see cref="CommunityFollowingIntegrationTests"/>.
/// </remarks>
[Collection("CommunityFeedCorrectness")]
public sealed class CommunityFeedCorrectnessIntegrationTests : IAsyncLifetime
{
    private const string AHost = "a.domain.local";
    private const string Community = "iris";
    private const string Alice = "alice";
    private const string Bob = "bob";

    private readonly CommunityFeedCorrectnessSharedHost _fixture;
    private readonly HttpClient _http;
    private readonly InMemoryPersistenceProvider _persistence;
    private readonly string _base = $"https://{AHost}";

    public CommunityFeedCorrectnessIntegrationTests(CommunityFeedCorrectnessSharedHost fixture)
    {
        _fixture = fixture;
        _persistence = (InMemoryPersistenceProvider)fixture.Persistence;
        _http = new HttpClient(fixture.Server.CreateHandler(), disposeHandler: false);
    }

    /// <inheritdoc/>
    public Task InitializeAsync()
    {
        _fixture.Reset();
        SeedForFixture(_persistence);
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public Task DisposeAsync()
    {
        _http.Dispose();
        return Task.CompletedTask;
    }

    // --- The feed is the newest-first merge of the members' outboxes ---------------

    [Fact]
    public async Task Feed_MergesMemberOutboxes_NewestFirst()
    {
        var response = await _http.GetAsync($"{_base}/ap/v1/c/{Community}/feed?limit=10");
        response.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        Assert.Equal("OrderedCollection", doc.RootElement.GetProperty("type").GetString());
        Assert.Equal(4, doc.RootElement.GetProperty("totalItems").GetInt32());

        // The feed is merged newest-first (outbox position, then member IRI). alice's outbox is
        // [create-2 (pos 0), create-1 (pos 1)]; bob's is [create-2 (pos 0), create-1 (pos 1)]. Merged:
        //   pos 0: alice create-2, bob create-2   (newest of each member)
        //   pos 1: alice create-1, bob create-1
        var items = JsonDoc.GetItems(doc.RootElement).Select(e => JsonDoc.ItemId(e)).ToArray();
        Assert.Equal(4, items.Length);
        Assert.Equal($"https://{AHost}/ap/v1/u/{Alice}/activities/create-2", items[0]);
        Assert.Equal($"https://{AHost}/ap/v1/u/{Bob}/activities/create-2", items[1]);
        Assert.Equal($"https://{AHost}/ap/v1/u/{Alice}/activities/create-1", items[2]);
        Assert.Equal($"https://{AHost}/ap/v1/u/{Bob}/activities/create-1", items[3]);
    }

    // --- De-duplication: an activity in two members' outboxes appears once ---------

    [Fact]
    public async Task Feed_DeduplicatesActivity_RecordedInTwoMembersOutboxes()
    {
        // Record the same activity (an IRI) in BOTH alice's and bob's outboxes. The feed must list it
        // exactly once (de-duplicated by IRI, keeping the first/newest occurrence).
        var aliceIri = new Iri($"https://{AHost}/ap/v1/u/{Alice}");
        var bobIri = new Iri($"https://{AHost}/ap/v1/u/{Bob}");
        var sharedId = $"https://{AHost}/ap/v1/shared/create-1";
        TestSeeder.AddCreateActivity(_persistence, aliceIri, sharedId, "a shared post");
        TestSeeder.AddCreateActivity(_persistence, bobIri, sharedId, "a shared post");

        var response = await _http.GetAsync($"{_base}/ap/v1/c/{Community}/feed?limit=10");
        response.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        var items = JsonDoc.GetItems(doc.RootElement).Select(e => JsonDoc.ItemId(e)).ToArray();
        // The shared post appears exactly once (not twice).
        Assert.Equal(1, items.Count(id => id == sharedId));
        // totalItems reflects the de-duplicated count (alice 2 + bob 2, minus the 1 shared duplicate).
        Assert.Equal(5, doc.RootElement.GetProperty("totalItems").GetInt32());
    }

    // --- Pagination ------------------------------------------------------------------

    [Fact]
    public async Task Feed_Page2_IsOrderedCollectionPage_WithPrevAndNext()
    {
        // 4 items, limit=2, page=2 → page 2 holds items 3 and 4 (pos 1: alice create-1, bob create-1).
        var response = await _http.GetAsync($"{_base}/ap/v1/c/{Community}/feed?limit=2&page=2");
        response.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        Assert.Equal("OrderedCollectionPage", doc.RootElement.GetProperty("type").GetString());
        var items = JsonDoc.GetItems(doc.RootElement).Select(e => JsonDoc.ItemId(e)).ToArray();
        Assert.Equal(2, items.Length);
        Assert.Equal($"https://{AHost}/ap/v1/u/{Alice}/activities/create-1", items[0]);
        Assert.Equal($"https://{AHost}/ap/v1/u/{Bob}/activities/create-1", items[1]);

        Assert.Equal(
            $"{_base}/ap/v1/c/{Community}/feed",
            doc.RootElement.GetProperty("partOf").GetString());
        Assert.Equal(
            $"{_base}/ap/v1/c/{Community}/feed/?page=1",
            doc.RootElement.GetProperty("prev").GetString());
        // Page 2 of a 2-page collection is the last page: no `next`.
        Assert.False(doc.RootElement.TryGetProperty("next", out _));
        Assert.Equal(4, doc.RootElement.GetProperty("totalItems").GetInt32());
    }

    // --- An unknown community 404s ---------------------------------------------------

    [Fact]
    public async Task Feed_UnknownCommunity_Returns404()
    {
        var response = await _http.GetAsync($"{_base}/ap/v1/c/nobody/feed");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // --- Helpers ------------------------------------------------------------------

    /// <summary>
    /// Seeds: community <c>iris</c> with members alice (2 posts, oldest→newest: GARDEN create-1,
    /// FEDERAL create-2) and bob (2 posts, oldest→newest: weather create-1, federation create-2), via the
    /// shared <see cref="TestSeeder"/>. Outbox order is insertion order with newest at index 0, so each
    /// member's outbox is newest-first.
    /// </summary>
    internal static void SeedForFixture(InMemoryPersistenceProvider persistence)
    {
        var communityIri = TestSeeder.SeedCommunity(persistence, AHost, Community);
        var aliceIri = TestSeeder.SeedPerson(persistence, AHost, Alice);
        var bobIri = TestSeeder.SeedPerson(persistence, AHost, Bob);
        TestSeeder.AddMember(persistence, communityIri, aliceIri);
        TestSeeder.AddMember(persistence, communityIri, bobIri);

        // alice: 2 posts, added oldest→newest (GARDEN create-1, FEDERAL create-2) so the outbox is
        // newest first (FEDERAL create-2, GARDEN create-1).
        TestSeeder.AddCreateActivity(persistence, aliceIri, $"{aliceIri.Value}/activities/create-1", "a GARDEN post");
        TestSeeder.AddCreateActivity(persistence, aliceIri, $"{aliceIri.Value}/activities/create-2", "a FEDERAL post");

        // bob: 2 posts, added oldest→newest (weather create-1, federation create-2) so the outbox is
        // newest first (federation create-2, weather create-1).
        TestSeeder.AddCreateActivity(persistence, bobIri, $"{bobIri.Value}/activities/create-1", "about weather");
        TestSeeder.AddCreateActivity(persistence, bobIri, $"{bobIri.Value}/activities/create-2", "about federation");
    }
}

/// <summary>
/// Shared-host fixture for <see cref="CommunityFeedCorrectnessIntegrationTests"/> (single instance,
/// a.domain.local). Built once per xunit collection; the test class resets + reseeds before
/// each method for isolation.
/// </summary>
public sealed class CommunityFeedCorrectnessSharedHost : SharedHostFixture
{
    public CommunityFeedCorrectnessSharedHost()
        : base(new ActivityPubHostOptions
        {
            Host = "a.domain.local",
            Handle = "alice",
            Persistence = CreatePersistence(),
        })
    {
    }

    private static InMemoryPersistenceProvider CreatePersistence()
    {
        var persistence = new InMemoryPersistenceProvider();
        CommunityFeedCorrectnessIntegrationTests.SeedForFixture(persistence);
        return persistence;
    }
}

/// <summary>
/// xunit collection definition for the community-feed-correctness shared-host fixture.
/// </summary>
[CollectionDefinition("CommunityFeedCorrectness")]
public sealed class CommunityFeedCorrectnessCollection : ICollectionFixture<CommunityFeedCorrectnessSharedHost>
{
}
