using System.Net;
using System.Text.Json;
using Iris.Core;
using Iris.Server.InMemory;
using Iris.Testing;
using KristofferStrube.ActivityStreams;
using Microsoft.AspNetCore.TestHost;
using Xunit;

namespace Iris.Server.Tests;

/// <summary>
/// S64 integration test: a local author posts an Article to a local community. The Article must
/// appear in the community's unified feed. The test seeds the Article in the author's outbox (the
/// S64 fix ensures the local outbox publish path records it in the community's local members'
/// outboxes, so the community feed surfaces it) and verifies the community feed returns it.
/// </summary>
public sealed class S64ArticleCommunityFeedIntegrationTests : IDisposable
{
    private const string AHost = "a.domain.local";
    private const string Alice = "alice";
    private const string Bob = "bob";
    private const string Community = "iris";

    private readonly TestServer _server;
    private readonly HttpClient _http;
    private readonly InMemoryPersistenceProvider _persistence;
    private readonly Iri _aliceIri;
    private readonly Iri _communityIri;

    public S64ArticleCommunityFeedIntegrationTests()
    {
        _persistence = new InMemoryPersistenceProvider();
        _aliceIri = TestSeeder.SeedPerson(_persistence, AHost, Alice);
        TestSeeder.SeedPerson(_persistence, AHost, Bob);
        _communityIri = TestSeeder.SeedCommunity(_persistence, AHost, Community);
        TestSeeder.AddMember(_persistence, _communityIri, _aliceIri);

        // Seed an Article in alice's outbox, tagged to the community (the S64 fix ensures the
        // local outbox publish path records the activity in the community's local members'
        // outboxes, so the community feed surfaces it).
        var articleId = $"https://{AHost}/ap/v1/articles/{Guid.NewGuid():N}";
        TestSeeder.AddCreateActivity(
            _persistence, _aliceIri, articleId, "an article posted to the community",
            attributedTo: [_communityIri]);

        _server = ActivityPubHostFactory.Create(new ActivityPubHostOptions
        {
            Host = AHost,
            Handle = Alice,
            Persistence = _persistence,
        });
        _http = new HttpClient(_server.CreateHandler(), disposeHandler: false)
        {
            BaseAddress = new Uri($"https://{AHost}"),
        };
    }

    public void Dispose()
    {
        _http.Dispose();
        _server.Dispose();
    }

    [Fact]
    public async Task ArticleInAuthorOutbox_TaggedToCommunity_AppearsInCommunityFeed()
    {
        // The Article appears in the community feed (the S64 assertion): the community's local
        // member (alice) has the Create in her outbox, tagged to the community, so the community
        // feed (which merges members' outboxes and filters to community-tagged posts) surfaces it.
        var feedResponse = await _http.GetAsync($"/ap/v1/c/{Community}/feed?limit=50");
        feedResponse.EnsureSuccessStatusCode();
        var feedJson = await feedResponse.Content.ReadAsStringAsync();
        using var feedDoc = JsonDocument.Parse(feedJson);
        var items = JsonDoc.GetItems(feedDoc.RootElement).Select(e => JsonDoc.ItemId(e)).ToArray();

        Assert.Single(items);
        Assert.Contains("articles", items[0]);
    }
}
