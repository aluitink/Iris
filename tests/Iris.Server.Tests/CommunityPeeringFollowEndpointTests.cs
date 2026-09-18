using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Iris.Core;
using Iris.Server.InMemory;
using Iris.Testing;
using KristofferStrube.ActivityStreams;
using Microsoft.AspNetCore.TestHost;
using Xunit;

namespace Iris.Server.Tests;

file static class PeeringIris
{
    public static readonly Iri IrisIri = new("https://a.domain.local/ap/v1/c/iris");
    public static readonly Iri AliceIri = new("https://a.domain.local/ap/v1/u/alice");
    public static readonly Iri PeerIri = new("https://a.domain.local/ap/v1/c/peer");
}

/// <summary>
/// Phase 89 integration test for the <strong>community peering</strong> surface: a community's operator
/// makes the community follow another actor (a person or another community) via
/// <c>POST /local/v1/c/{name}/follow/{targetIri}</c> (owner-only, the same credential seam as the other
/// local community endpoints), and undoes it with <c>?unfollow=true</c>. The community is a Group actor
/// that holds no client key (it cannot sign its own outbox from the browser), so the owner authenticates
/// here and the server authors + records the community's <see cref="Follow"/> in the community's follows
/// set + outbox. Following makes the community's unified feed surface the followed actor's content to its
/// members (the peering behavior, tested at the feed level in
/// <see cref="Services.CommunityFeedPeeringTests"/>).
/// </summary>
[Collection("CommunityPeering")]
public sealed class CommunityPeeringFollowEndpointTests : IAsyncLifetime
{
    private const string AHost = "a.domain.local";
    private const string Community = "iris";
    private const string Alice = "alice";

    private readonly CommunityPeeringSharedHost _fixture;
    private readonly HttpClient _http;
    private readonly InMemoryPersistenceProvider _persistence;
    private readonly string _base = $"https://{AHost}";

    public CommunityPeeringFollowEndpointTests(CommunityPeeringSharedHost fixture)
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

    // --- The community's /following collection lists the followed actor after a follow ----

    [Fact]
    public async Task Follow_RecordsFollowEdge_AppearInFollowingCollection()
    {
        Assert.Equal(HttpStatusCode.NoContent, await FollowAsync(PeeringIris.PeerIri, auth: "alice:alice-password"));

        // The edge is recorded in the community's follows set.
        Assert.Contains(PeeringIris.PeerIri, await _persistence.Communities.GetFollowsAsync(PeeringIris.IrisIri));

        // The community's /following collection lists the followed actor.
        var response = await _http.GetAsync($"{_base}/ap/v1/c/{Community}/following");
        response.EnsureSuccessStatusCode();
        var items = JsonDoc.ItemIdsOf(await response.Content.ReadAsStringAsync());
        Assert.Contains(PeeringIris.PeerIri.Value, items);
    }

    // --- The community-authored Follow is stored in the community's outbox ------------------

    [Fact]
    public async Task Follow_AuthorsFollowActivity_InCommunityOutbox()
    {
        Assert.Equal(HttpStatusCode.NoContent, await FollowAsync(PeeringIris.PeerIri, auth: "alice:alice-password"));

        // The community's outbox carries a Follow authored by the community, to the target.
        var outbox = await _persistence.Activities.GetOutboxAsync(PeeringIris.IrisIri);
        var follow = outbox.OfType<Follow>().FirstOrDefault(f =>
            f.Actor?.FirstOrDefault().ResolveObjectIri() == PeeringIris.IrisIri
            && f.Object?.FirstOrDefault().ResolveObjectIri() == PeeringIris.PeerIri);

        Assert.NotNull(follow);
        Assert.False(string.IsNullOrEmpty(follow!.Id));
    }

    // --- Unfollow removes the edge and the content drops from the feed ---------------------

    [Fact]
    public async Task Unfollow_RemovesFollowEdge_AndFeedDropsContent()
    {
        // A peer post (NOT tagged to iris — it is attributed to peer), so only the peering branch admits it.
        TestSeeder.AddCreateActivity(_persistence, PeeringIris.PeerIri, $"{PeeringIris.PeerIri.Value}/activities/peer-1", "peer post", attributedTo: null);

        Assert.Equal(HttpStatusCode.NoContent, await FollowAsync(PeeringIris.PeerIri, auth: "alice:alice-password"));
        Assert.Contains(PeeringIris.PeerIri, await _persistence.Communities.GetFollowsAsync(PeeringIris.IrisIri));
        Assert.Contains($"{PeeringIris.PeerIri.Value}/activities/peer-1", await FeedActivityIrisAsync(refresh: true));

        // Unfollow: the edge is removed, so the followed actor's content drops out of the feed.
        Assert.Equal(HttpStatusCode.NoContent, await FollowAsync(PeeringIris.PeerIri, auth: "alice:alice-password", unfollow: true));
        Assert.Empty(await _persistence.Communities.GetFollowsAsync(PeeringIris.IrisIri));
        Assert.DoesNotContain($"{PeeringIris.PeerIri.Value}/activities/peer-1", await FeedActivityIrisAsync(refresh: true));
    }

    // --- Unfollowing an unfollowed actor is a 404 (nothing to undo) -------------------------

    [Fact]
    public async Task Unfollow_NotFollowing_IsNotFound()
    {
        var status = await FollowAsync(PeeringIris.PeerIri, auth: "alice:alice-password", unfollow: true);
        Assert.Equal(HttpStatusCode.NotFound, status);
        Assert.Empty(await _persistence.Communities.GetFollowsAsync(PeeringIris.IrisIri));
    }

    // --- A non-owner (unauthenticated) is rejected (403) ------------------------------------

    [Fact]
    public async Task Follow_Unauthenticated_IsRejected()
    {
        var status = await FollowAsync(PeeringIris.PeerIri, auth: null);
        Assert.Equal(HttpStatusCode.Forbidden, status);
        Assert.Empty(await _persistence.Communities.GetFollowsAsync(PeeringIris.IrisIri));
    }

    // --- A community cannot follow itself (400) ---------------------------------------------

    [Fact]
    public async Task Follow_Self_IsBadRequest()
    {
        var status = await FollowAsync(PeeringIris.IrisIri, auth: "alice:alice-password");
        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.DoesNotContain(PeeringIris.IrisIri, await _persistence.Communities.GetFollowsAsync(PeeringIris.IrisIri));
    }

    // --- An unknown community 404s (write) ---------------------------------------------------

    [Fact]
    public async Task Follow_UnknownCommunity_IsNotFound()
    {
        var status = await FollowAsync(PeeringIris.PeerIri, communityName: "nobody", auth: "alice:alice-password");
        Assert.Equal(HttpStatusCode.NotFound, status);
    }

    // --- Helpers --------------------------------------------------------------------------

    /// <summary>
    /// Issues a raw Basic-authenticated community-follow POST to
    /// <c>/local/v1/c/{name}/follow/{targetIri}</c> (or <c>?unfollow=true</c>). The write targets the
    /// non-AP local tree (a follow the community initiates is a local operator action; the community
    /// holds no client key, so it cannot sign its own outbox). <paramref name="auth"/> is "user:pass" or
    /// null (no auth).
    /// </summary>
    private async Task<HttpStatusCode> FollowAsync(
        Iri targetIri, string? auth, bool unfollow = false, string communityName = Community)
    {
        var url = $"{_base}/local/v1/c/{communityName}/follow/{targetIri.Value.TrimStart('/')}"
            + (unfollow ? "?unfollow=true" : string.Empty);
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
    /// Reads the community's unified feed over the wire and returns the IRIs of the activities it
    /// contains. The community feed is served through the local collection-page response cache, so a
    /// read that must observe a just-made change (a follow/unfollow) passes <paramref name="refresh"/>=true.
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
    /// Seeds a community <c>iris</c> whose <c>AttributedTo</c> is <c>alice</c> (so the credential
    /// validator recognizes alice as the community's owner), plus a peer community.
    /// </summary>
    internal static void SeedForFixture(InMemoryPersistenceProvider persistence)
    {
        var aliceIri = TestSeeder.SeedPerson(persistence, AHost, Alice);
        TestSeeder.SeedCommunity(persistence, AHost, Community);
        var peerIri = TestSeeder.SeedCommunity(persistence, AHost, "peer");

        // Set iris's AttributedTo to alice (the owner) so VerifyCommunityCreatorAsync passes for alice.
        if (persistence.Communities.TryGetCommunityAsync(PeeringIris.IrisIri, out var iris).GetAwaiter().GetResult() && iris is not null)
        {
            iris.AttributedTo = [new Link { Href = aliceIri.Uri }];
            persistence.Communities.PutCommunityAsync(iris).GetAwaiter().GetResult();
        }

        _ = peerIri;
    }
}

/// <summary>
/// Shared-host fixture for <see cref="CommunityPeeringFollowEndpointTests"/> (single instance,
/// a.domain.local, with the community owner alice's Basic-auth credential).
/// </summary>
public sealed class CommunityPeeringSharedHost : SharedHostFixture
{
    public CommunityPeeringSharedHost()
        : base(new ActivityPubHostOptions
        {
            Host = "a.domain.local",
            Handle = "alice",
            Persistence = CreatePersistence(),
            CredentialValidator = new BasicAuthCredentialValidator(
                (iri, username, password) => ValueTask.FromResult(
                    iri == PeeringIris.AliceIri
                    && username == "alice"
                    && password == "alice-password")),
        })
    {
    }

    private static InMemoryPersistenceProvider CreatePersistence()
    {
        var persistence = new InMemoryPersistenceProvider();
        CommunityPeeringFollowEndpointTests.SeedForFixture(persistence);
        return persistence;
    }
}

/// <summary>
/// xunit collection definition for the community-peering shared-host fixture.
/// </summary>
[CollectionDefinition("CommunityPeering")]
public sealed class CommunityPeeringCollection : ICollectionFixture<CommunityPeeringSharedHost>
{
}
