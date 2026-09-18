using System.Net;
using System.Text.Json;
using Iris.Client;
using Iris.Core;
using Iris.Server.InMemory;
using Iris.Testing;
using KristofferStrube.ActivityStreams;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Iris.Server.Tests;

/// <summary>
/// 40.3 integration test for the <strong>community feed filtering</strong>: the community feed
/// (<c>GET /ap/v1/c/{name}/feed</c>) must show only community-tagged posts (the note's
/// <c>attributedTo</c> carries the community IRI), not all member outbox posts. A member's
/// personal post (no community in <c>attributedTo</c>) is excluded; a member's community post
/// (community in <c>attributedTo</c>) is included.
/// </summary>
public sealed class CommunityFeedFilteringIntegrationTests : IDisposable
{
    private const string AHost = "a.domain.local";
    private const string Community = "iris";
    private const string Alice = "alice";
    private const string Bob = "bob";

    private readonly TestServer _server;
    private readonly HttpClient _http;
    private readonly InMemoryPersistenceProvider _persistence;
    private readonly Iri _communityIri;
    private readonly Iri _aliceIri;
    private readonly Iri _bobIri;

    public CommunityFeedFilteringIntegrationTests()
    {
        _persistence = new InMemoryPersistenceProvider();
        _aliceIri = TestSeeder.SeedPerson(_persistence, AHost, Alice);
        _bobIri = TestSeeder.SeedPerson(_persistence, AHost, Bob);
        _communityIri = TestSeeder.SeedCommunity(_persistence, AHost, Community);
        TestSeeder.AddMember(_persistence, _communityIri, _aliceIri);
        TestSeeder.AddMember(_persistence, _communityIri, _bobIri);

        // alice: one community-tagged post + one personal post.
        TestSeeder.AddCreateActivity(
            _persistence, _aliceIri, $"{_aliceIri.Value}/activities/community-1",
            "alice community post", new[] { _communityIri });
        TestSeeder.AddCreateActivity(
            _persistence, _aliceIri, $"{_aliceIri.Value}/activities/personal-1",
            "alice personal post");

        // bob: one community-tagged post + one personal post.
        TestSeeder.AddCreateActivity(
            _persistence, _bobIri, $"{_bobIri.Value}/activities/community-1",
            "bob community post", new[] { _communityIri });
        TestSeeder.AddCreateActivity(
            _persistence, _bobIri, $"{_bobIri.Value}/activities/personal-1",
            "bob personal post");

        _server = StartServer(_persistence);
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
    public async Task CommunityFeed_ShowsOnlyCommunityTaggedPosts()
    {
        var response = await _http.GetAsync($"/ap/v1/c/{Community}/feed?limit=50");
        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(json);

        var items = JsonDoc.GetItems(doc.RootElement).Select(e => JsonDoc.ItemId(e)).ToArray();

        // Community-tagged posts are present.
        Assert.Contains($"{_aliceIri.Value}/activities/community-1", items);
        Assert.Contains($"{_bobIri.Value}/activities/community-1", items);

        // Personal posts are NOT present.
        Assert.DoesNotContain($"{_aliceIri.Value}/activities/personal-1", items);
        Assert.DoesNotContain($"{_bobIri.Value}/activities/personal-1", items);

        // Exactly 2 items (the two community-tagged posts).
        Assert.Equal(2, items.Length);
    }

    [Fact]
    public async Task CommunityFeed_EmptyWhenNoCommunityTaggedPosts()
    {
        // Create a new community with members who only have personal posts.
        var soloIri = TestSeeder.SeedCommunity(_persistence, AHost, "solo");
        TestSeeder.AddMember(_persistence, soloIri, _aliceIri);

        // alice's personal post is NOT tagged to "solo".
        var response = await _http.GetAsync($"/ap/v1/c/solo/feed?limit=50");
        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(json);

        var items = JsonDoc.GetItems(doc.RootElement);

        Assert.Empty(items);
    }

    private static TestServer StartServer(InMemoryPersistenceProvider persistence)
    {
        var instanceActorIri = new Iri($"https://{AHost}/ap/v1/u/{Alice}");
        var keyStore = new InMemoryKeyStore();
        var key = KeyPairGenerator.GenerateRsa(new Iri($"{instanceActorIri.Value}#key-1"));
        keyStore.PutKey(key);
        var keyProvider = new InMemoryKeyProvider(keyStore);
        keyProvider.RegisterKey(instanceActorIri, key.KeyId);
        var signer = new HttpSignatureSigner(keyStore);

        var builder = new Microsoft.AspNetCore.Hosting.WebHostBuilder()
            .ConfigureLogging(l => l.ClearProviders())
            .ConfigureServices(s =>
            {
                s.AddRouting();
                s.AddActivityPubServer(opts =>
                {
                    opts.BaseUri = new Iri($"https://{AHost}");
                    opts.InstanceName = "iris-test";
                    opts.InstanceActorId = instanceActorIri;
                });
                s.AddInMemoryPersistence();
                s.AddSingleton<IPersistenceProvider>(persistence);
                s.AddSingleton<IKeyStore>(keyStore);
                s.AddSingleton<IKeyProvider>(keyProvider);
                s.AddSingleton<ISignatureSigner>(signer);
            })
            .Configure(webApp =>
            {
                webApp.UseRouting();
                webApp.UseSignatureValidation();
                webApp.UseEndpoints(endpoints => endpoints.MapActivityPubEndpoints());
            });

        return new TestServer(builder);
    }
}
