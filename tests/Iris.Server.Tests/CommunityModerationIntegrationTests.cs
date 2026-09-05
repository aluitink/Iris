using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Iris.Client;
using Iris.Core;
using Iris.Server;
using Iris.Server.InMemory;
using Iris.Testing;
using Microsoft.AspNetCore.TestHost;
using Xunit;

namespace Iris.Server.Tests;

/// <summary>
/// Phase 19.5.4 integration test for the <strong>community moderation surface</strong>: a community
/// moderates the actors whose content it surfaces in its unified feed. The moderation is
/// <em>community-scoped</em> (the edges live in the community's own sets, not the person
/// <see cref="ICommunityStore"/> → <see cref="IModerationStore"/>): the community's <c>blocks</c>,
/// <c>flags</c>, and <c>mutes</c> collections are served over the wire (mirroring the person moderation
/// collections, for a <c>Group</c>), a community operator records a community-scoped mute via a
/// Basic-authenticated <c>POST /local/v1/c/{name}/mutes/{target}</c> on the non-AP local-moderation tree
/// (19.0b.2b: a mute is a local, non-federated decision — not on the <c>/ap/v1</c> AP tree; the
/// community's IRI is the credential seam), and a blocked/muted member's content is excluded from the
/// community feed.
/// </summary>
/// <remarks>
/// Topology: a single instance (a.domain.local) hosts the managed community <c>iris</c> (the operator's
/// credential is ("iris", "iris-password") for iris's IRI) with two local members — <c>alice</c> (the
/// instance's Handle actor) and <c>bob</c> — each with a post. The tests exercise: the community document
/// advertising the three moderation collections; the empty collections' shape; recording a mute (204) and
/// reading it back from the community's <c>mutes</c> collection; the mute excluding a member's content
/// from the community feed without severing the membership (soft exclusion); an un-mute restoring it; an
/// unauthenticated mute (401); an unknown community (404); and the block/flag collections (recorded via
/// the store, as a federated Block/Flag would, and read back over the wire).
/// </remarks>
[Collection("CommunityModeration")]
public sealed class CommunityModerationIntegrationTests : IAsyncLifetime
{
    private const string AHost = "a.domain.local";
    private const string Community = "iris";
    private const string Alice = "alice";
    private const string Bob = "bob";

    private static readonly Iri IrisIri = new($"https://{AHost}/ap/v1/c/{Community}");
    private static readonly Iri AliceIri = new($"https://{AHost}/ap/v1/u/{Alice}");
    private static readonly Iri BobIri = new($"https://{AHost}/ap/v1/u/{Bob}");

    private readonly CommunityModerationSharedHost _fixture;
    private readonly HttpClient _http;
    private readonly InMemoryPersistenceProvider _persistence;
    private readonly string _base = $"https://{AHost}";

    public CommunityModerationIntegrationTests(CommunityModerationSharedHost fixture)
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

    // --- The community document advertises the three moderation collections -------------

    [Fact]
    public async Task CommunityDocument_AdvertisesModerationCollections()
    {
        var response = await _http.GetAsync($"{_base}/ap/v1/c/{Community}");
        response.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        // The moderation collection links are Iris extensions, namespaced under the iris: namespace.
        var ns = IrisDocumentExtensions.DefaultNamespaceIri;
        Assert.Equal(
            $"{_base}/ap/v1/c/{Community}/blocks",
            doc.RootElement.GetProperty(ns + CollectionExtensionNames.Blocks).GetString());
        Assert.Equal(
            $"{_base}/ap/v1/c/{Community}/flags",
            doc.RootElement.GetProperty(ns + CollectionExtensionNames.Flags).GetString());
        Assert.Equal(
            $"{_base}/ap/v1/c/{Community}/mutes",
            doc.RootElement.GetProperty(ns + CollectionExtensionNames.Mutes).GetString());
    }

    // --- The moderation collections are empty OrderedCollections before any moderation --

    [Fact]
    public async Task ModerationCollections_Empty_AreOrderedCollections()
    {
        foreach (var collection in new[] { "blocks", "flags", "mutes" })
        {
            var response = await _http.GetAsync($"{_base}/ap/v1/c/{Community}/{collection}");
            response.EnsureSuccessStatusCode();
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

            Assert.Equal("OrderedCollection", doc.RootElement.GetProperty("type").GetString());
            Assert.Equal($"{_base}/ap/v1/c/{Community}/{collection}", doc.RootElement.GetProperty("id").GetString());
            Assert.Equal(0, doc.RootElement.GetProperty("totalItems").GetInt32());
        }
    }

    // --- An unknown community 404s (read + write) --------------------------------------

    [Fact]
    public async Task ModerationCollections_UnknownCommunity_Return404()
    {
        var read = await _http.GetAsync($"{_base}/ap/v1/c/nobody/mutes");
        Assert.Equal(HttpStatusCode.NotFound, read.StatusCode);

        var write = await MuteAsync("nobody", BobIri, auth: $"{Community}:iris-password");
        Assert.Equal(HttpStatusCode.NotFound, write);
    }

    // --- A community-scoped mute is recorded and appears in the community's mutes ------

    [Fact]
    public async Task Mute_Authenticated_RecordsCommunityMuteEdge()
    {
        var status = await MuteAsync(Community, BobIri, auth: $"{Community}:iris-password");
        Assert.Equal(HttpStatusCode.NoContent, status);

        // The edge is recorded in the community's own moderation sets (not the person store).
        var mutes = await _persistence.Communities.GetMutesAsync(IrisIri);
        Assert.Contains(BobIri, mutes);
        Assert.DoesNotContain(AliceIri, mutes);
    }

    // --- The community's /mutes collection serves the recorded edge ---------------------

    [Fact]
    public async Task Mute_AppearsInCommunityMutesCollection()
    {
        await MuteAsync(Community, BobIri, auth: $"{Community}:iris-password");

        var response = await _http.GetAsync($"{_base}/ap/v1/c/{Community}/mutes");
        response.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        Assert.Equal("OrderedCollection", doc.RootElement.GetProperty("type").GetString());
        Assert.Equal(1, doc.RootElement.GetProperty("totalItems").GetInt32());
        var items = JsonDoc.GetItems(doc.RootElement).Select(e => JsonDoc.ItemId(e)).ToArray();
        Assert.Single(items);
        Assert.Equal(BobIri.Value, items[0]);
    }

    // --- A community mute excludes the member's content from the community feed ---------

    [Fact]
    public async Task Mute_ExcludesMemberContentFromCommunityFeed_WithoutSeveringMembership()
    {
        // Before the mute: bob's post is in the community feed (first read of the page — a cache miss).
        Assert.Contains($"{BobIri.Value}/activities/create-1", await FeedActivityIrisAsync());

        // Mute bob (204): the edge is recorded, the membership is intact, but bob's content is excluded.
        // The post-mutation feed read uses ?refresh=true to bypass the collection-page cache (19.5.5) and
        // observe the fresh (post-mute) feed.
        Assert.Equal(HttpStatusCode.NoContent, await MuteAsync(Community, BobIri, auth: $"{Community}:iris-password"));
        Assert.Contains(BobIri, await _persistence.Communities.GetMutesAsync(IrisIri));
        Assert.True(await _persistence.Communities.IsMemberAsync(IrisIri, BobIri));
        Assert.DoesNotContain($"{BobIri.Value}/activities/create-1", await FeedActivityIrisAsync(refresh: true));

        // Un-mute bob (?unmute=true, 204): the edge is removed and bob's content returns.
        Assert.Equal(HttpStatusCode.NoContent, await MuteAsync(Community, BobIri, auth: $"{Community}:iris-password", unmute: true));
        Assert.DoesNotContain(BobIri, await _persistence.Communities.GetMutesAsync(IrisIri));
        Assert.Contains($"{BobIri.Value}/activities/create-1", await FeedActivityIrisAsync(refresh: true));
    }

    // --- A community block excludes the member's content (hard exclusion) ---------------

    [Fact]
    public async Task Block_ExcludesMemberContentFromCommunityFeed()
    {
        // A block is recorded in the community's blocks set (as a federated Block of a local member would
        // be). The blocked member's content is excluded from the community feed (the post-mutation read
        // uses ?refresh=true to bypass the collection-page cache, 19.5.5).
        await _persistence.Communities.AddBlockAsync(IrisIri, AliceIri);

        var blocks = await _persistence.Communities.GetBlocksAsync(IrisIri);
        Assert.Contains(AliceIri, blocks);
        Assert.DoesNotContain($"{AliceIri.Value}/activities/create-1", await FeedActivityIrisAsync(refresh: true));
        // bob's content is unaffected (the block is scoped to alice).
        Assert.Contains($"{BobIri.Value}/activities/create-1", await FeedActivityIrisAsync(refresh: true));

        // Un-blocking removes the edge and restores alice's content.
        await _persistence.Communities.RemoveBlockAsync(IrisIri, AliceIri);
        Assert.Contains($"{AliceIri.Value}/activities/create-1", await FeedActivityIrisAsync(refresh: true));
    }

    // --- A community flag does NOT exclude content (a report, not a filter) -------------

    [Fact]
    public async Task Flag_IsRecordedAndReadBack_ButDoesNotExcludeContent()
    {
        // A flag is a moderation report: it is recorded + surfaced in the flags collection, but the
        // flagged member's content is NOT excluded from the community feed (only blocks and mutes filter).
        await _persistence.Communities.AddFlagAsync(IrisIri, BobIri);

        var flags = await _persistence.Communities.GetFlagsAsync(IrisIri);
        Assert.Contains(BobIri, flags);

        // The flags collection serves the edge over the wire.
        var response = await _http.GetAsync($"{_base}/ap/v1/c/{Community}/flags");
        response.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var items = JsonDoc.GetItems(doc.RootElement).Select(e => JsonDoc.ItemId(e)).ToArray();
        Assert.Single(items);
        Assert.Equal(BobIri.Value, items[0]);

        // bob's content is still in the feed (a flag is not a content exclusion).
        Assert.Contains($"{BobIri.Value}/activities/create-1", await FeedActivityIrisAsync());
    }

    // --- An unauthenticated community mute is rejected (401) ----------------------------

    [Fact]
    public async Task Mute_Unauthenticated_IsRejected()
    {
        var status = await MuteAsync(Community, BobIri, auth: null);
        Assert.Equal(HttpStatusCode.Unauthorized, status);
        Assert.DoesNotContain(BobIri, await _persistence.Communities.GetMutesAsync(IrisIri));
    }

    // --- An un-mute of a non-existent mute is a no-op (204) -----------------------------

    [Fact]
    public async Task Unmute_NonExistent_IsNoOp()
    {
        var status = await MuteAsync(Community, BobIri, auth: $"{Community}:iris-password", unmute: true);
        Assert.Equal(HttpStatusCode.NoContent, status);
        Assert.Empty(await _persistence.Communities.GetMutesAsync(IrisIri));
    }

    // --- Community moderation is scoped to the community (not the person store) ---------

    [Fact]
    public async Task CommunityModeration_IsScopedToTheCommunity_NotThePersonStore()
    {
        // A community-scoped mute/block/flag must live in the community's own sets, NOT the person
        // moderation store (which is keyed by an actor IRI). The person store must remain empty.
        await _persistence.Communities.AddMuteAsync(IrisIri, BobIri);
        await _persistence.Communities.AddBlockAsync(IrisIri, AliceIri);

        Assert.DoesNotContain(BobIri, await _persistence.Moderation.GetMutesAsync(AliceIri));
        Assert.DoesNotContain(AliceIri, await _persistence.Moderation.GetBlocksAsync(AliceIri));
        Assert.DoesNotContain(AliceIri, await _persistence.Moderation.GetBlocksAsync(IrisIri));
    }

    // --- The community feed is cached and ?refresh=true bypasses it (19.5.5) -------------

    [Fact]
    public async Task CommunityFeed_IsServedFromThePageCache_WithRefreshBypassAndCacheControl()
    {
        // Phase 19.5.5: the community's collections (members, feed, following/followers, and the
        // moderation collections) are served through the local collection-page response cache, exactly
        // as the actor collections are. A plain read of a page caches it; a second read within the TTL
        // returns the cached page even after the underlying state changes; and ?refresh=true bypasses
        // the cache (re-rendering from the store). The cache also advertises Cache-Control on every
        // response: a plain read is cacheable (max-age=60, stale-while-revalidate=300) and a refresh
        // read is not (no-cache).
        var bobPost = $"{BobIri.Value}/activities/create-1";

        // A plain (uncached) first read of the feed is a cache miss: it renders from the store, caches
        // page 1, and advertises the cacheable Cache-Control.
        var first = await _http.GetAsync($"{_base}/ap/v1/c/{Community}/feed?limit=10");
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal("max-age=60, stale-while-revalidate=300", CacheControlOf(first));
        Assert.Contains(bobPost, (await FeedActivityIrisAsync()).ToList());

        // A second plain read of the same page is a cache hit (served from the cache, still cacheable).
        var second = await _http.GetAsync($"{_base}/ap/v1/c/{Community}/feed?limit=10");
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        Assert.Equal("max-age=60, stale-while-revalidate=300", CacheControlOf(second));

        // Now mute bob (the store changes), but the cache does NOT know about it.
        Assert.Equal(HttpStatusCode.NoContent, await MuteAsync(Community, BobIri, auth: $"{Community}:iris-password"));

        // A plain third read is still a cache hit within the TTL: it serves the STALE page-1 that was
        // cached before the mute (bob's post is still there), because a plain read never re-renders.
        // This is the documented cache behaviour: a change is invisible to a plain reader until the page
        // expires or a refresh bypasses the cache.
        var third = await _http.GetAsync($"{_base}/ap/v1/c/{Community}/feed?limit=10");
        Assert.Equal(HttpStatusCode.OK, third.StatusCode);
        Assert.Contains(bobPost, JsonDoc.ItemIdsOf(await third.Content.ReadAsStringAsync()));

        // A ?refresh=true read bypasses the cache and re-renders from the store: bob is now muted, so his
        // post is excluded — and the response advertises no-cache (it must not be reused by a client).
        var refresh = await _http.GetAsync($"{_base}/ap/v1/c/{Community}/feed?limit=10&refresh=true");
        Assert.Equal(HttpStatusCode.OK, refresh.StatusCode);
        Assert.Equal("no-cache", CacheControlOf(refresh));
        Assert.DoesNotContain(bobPost, JsonDoc.ItemIdsOf(await refresh.Content.ReadAsStringAsync()));

        // The refresh write-back replaced the stale cache entry: a subsequent plain read now serves the
        // fresh (post-mute) feed, not the stale pre-mute page.
        var after = await _http.GetAsync($"{_base}/ap/v1/c/{Community}/feed?limit=10");
        Assert.Equal(HttpStatusCode.OK, after.StatusCode);
        Assert.DoesNotContain(bobPost, JsonDoc.ItemIdsOf(await after.Content.ReadAsStringAsync()));
    }

    // --- Helpers ------------------------------------------------------------------------

    /// <summary>
    /// Reads the community's unified feed over the wire and returns the IRIs of the activities it
    /// contains (the feed's items are the members' outbox <c>Create</c>s; each item's IRI is read from its
    /// <c>id</c>). The community feed is served through the local collection-page response cache (19.5.5),
    /// so a read that must observe a just-made change (a mute/block/unmute) passes
    /// <paramref name="refresh"/>=true to bypass the cache (<c>?refresh=true</c>); the first read of a
    /// page is a cache miss and needs no bypass.
    /// </summary>
    private async Task<IReadOnlyList<string>> FeedActivityIrisAsync(bool refresh = false)
    {
        var url = $"{_base}/ap/v1/c/{Community}/feed?limit=10"
            + (refresh ? "&refresh=true" : string.Empty);
        var response = await _http.GetAsync(url);
        response.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return JsonDoc.GetItems(doc.RootElement).Select(e => JsonDoc.ItemId(e)).ToList();
    }

    /// <summary>
    /// Issues a raw Basic-authenticated community-mute POST (a mute is not a signed inbox delivery — it
    /// is a Basic-authenticated POST to the community's own instance; the community's IRI is the
    /// credential seam). The write targets the non-AP local tree
    /// (<c>/local/v1/c/{name}/mutes/{target}</c>), not the <c>/ap/v1</c> AP tree (19.0b.2b AP-native
    /// rework: a mute is a local, non-federated moderation decision). <paramref name="auth"/> is
    /// "user:pass" or null (no auth).
    /// </summary>
    private async Task<HttpStatusCode> MuteAsync(string name, Iri targetIri, string? auth, bool unmute = false)
    {
        var url = $"{_base}/local/v1/c/{name}/mutes/{targetIri.Value.TrimStart('/')}"
            + (unmute ? "?unmute=true" : string.Empty);
        using var request = new HttpRequestMessage(HttpMethod.Post, url);
        if (auth is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue(
                "Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes(auth)));
        }

        using var response = await _http.SendAsync(request);
        return response.StatusCode;
    }

    /// <summary>
    /// Reads the <c>Cache-Control</c> header value of a response (empty string when absent).
    /// </summary>
    private static string CacheControlOf(HttpResponseMessage response)
        => string.Join(", ", response.Headers.GetValues("Cache-Control") ?? []);

    /// <summary>
    /// Seeds the persistence provider: a community <c>iris</c> with two local members (alice, bob),
    /// each with a post, via the shared <see cref="TestSeeder"/>.
    /// </summary>
    internal static void SeedForFixture(InMemoryPersistenceProvider persistence)
    {
        var aliceIri = TestSeeder.SeedPerson(persistence, AHost, Alice);
        var bobIri = TestSeeder.SeedPerson(persistence, AHost, Bob);
        var irisIri = TestSeeder.SeedCommunity(persistence, AHost, Community);
        TestSeeder.AddMember(persistence, irisIri, aliceIri);
        TestSeeder.AddMember(persistence, irisIri, bobIri);
        TestSeeder.AddCreateActivity(persistence, aliceIri, $"{aliceIri.Value}/activities/create-1", "a GARDEN post");
        TestSeeder.AddCreateActivity(persistence, bobIri, $"{bobIri.Value}/activities/create-1", "about weather");
    }
}

/// <summary>
/// Shared-host fixture for <see cref="CommunityModerationIntegrationTests"/> (single instance,
/// a.domain.local, with the community operator's Basic-auth credential). Built once per xunit
/// collection; the test class resets + reseeds before each method for isolation.
/// </summary>
public sealed class CommunityModerationSharedHost : SharedHostFixture
{
    public CommunityModerationSharedHost()
        : base(new ActivityPubHostOptions
        {
            Host = "a.domain.local",
            Handle = "alice",
            Persistence = CreatePersistence(),
            CredentialValidator = new BasicAuthCredentialValidator(
                (iri, username, password) => ValueTask.FromResult(
                    iri == new Iri("https://a.domain.local/ap/v1/c/iris")
                    && username == "iris"
                    && password == "iris-password")),
        })
    {
    }

    private static InMemoryPersistenceProvider CreatePersistence()
    {
        var persistence = new InMemoryPersistenceProvider();
        CommunityModerationIntegrationTests.SeedForFixture(persistence);
        return persistence;
    }
}

/// <summary>
/// xunit collection definition for the community-moderation shared-host fixture.
/// </summary>
[CollectionDefinition("CommunityModeration")]
public sealed class CommunityModerationCollection : ICollectionFixture<CommunityModerationSharedHost>
{
}
