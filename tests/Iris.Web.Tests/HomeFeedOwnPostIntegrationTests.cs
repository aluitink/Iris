using Iris.Client;
using Iris.Client.Auth;
using Iris.Client.Pipeline;
using Iris.Core;
using Iris.Core.Compose;
using Iris.Core.Identity;
using Iris.Core.Signing;
using Iris.Server;
using Iris.Server.Security;
using Iris.Server.Stores;
using Iris.Testing;
using Iris.Web;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;
using Note = KristofferStrube.ActivityStreams.Note;
using IObject = KristofferStrube.ActivityStreams.IObject;
using Activity = KristofferStrube.ActivityStreams.Activity;

namespace Iris.Web.Tests;

/// <summary>
/// S36 regression: the home-timeline follow feed (<c>GET /ap/v1/u/{handle}/feed?source=people</c>), served
/// by the real follow-feed endpoint through the full endpoint pipeline
/// (requester resolution + owner gate, <c>FeedService</c>, <c>EnrichCollectionItemsAsync</c>, and
/// <c>BuildCollectionPageDocument</c>), must surface the actor's <em>own</em> content <c>Create</c>.
/// <para>
/// The in-memory <c>FeedService</c> unit tests and the EF-store contract test both prove the service
/// returns the own <c>Create</c>; this test pins the layer they do not cover — the endpoint's
/// enrich/paginate/serialize path — which is where the live home feed (S36) dropped every note
/// <c>Create</c> while keeping an <c>Announce</c> and a community <c>Create</c>.
/// </para>
/// </summary>
public sealed class HomeFeedOwnPostIntegrationTests : IDisposable
{
    private const string Base = "https://homefeed.test.local";

    private readonly TestServer _server;
    private readonly IServiceProvider _services;
    private readonly Iri _actorIri;
    private readonly KeyPair _actorKey;

    public HomeFeedOwnPostIntegrationTests()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        WebAppFactory.ConfigureServices(builder, Base);
        var services = builder.Services;

        services.AddSingleton<IActorDocumentFetcher>(sp =>
            new LocalActorDocumentFetcher(sp.GetRequiredService<IPersistenceProvider>()));

        var webHostBuilder = new WebHostBuilder()
            .UseTestServer()
            .ConfigureServices(s =>
            {
                foreach (var descriptor in services)
                {
                    s.Add(descriptor);
                }
            })
            .Configure(webApp =>
            {
                webApp.UseRouting();
                webApp.UseAntiforgery();
                webApp.UseSignatureValidation();
                webApp.UseAuthentication();
                webApp.UseAuthorization();
                webApp.UseStaticFiles();
                webApp.UseEndpoints(endpoints =>
                {
                    WebAppFactory.MapAuthEndpoints(endpoints);
                    endpoints.MapActivityPubEndpoints();
                });
            });

        _server = new TestServer(webHostBuilder);
        WebAppFactory.InitializePersistence(_server.Services, builder.Configuration, Base);
        _services = _server.Services;

        var diKeyStore = _services.GetRequiredService<IKeyStore>();
        _actorIri = new Iri($"{Base}/ap/v1/u/alice");

        var keyId = new Iri($"{_actorIri.Value}#key-1");
        if (!diKeyStore.TryGetKey(keyId, out var existing) || existing is null)
        {
            throw new InvalidOperationException("Seeded actor key not found.");
        }

        _actorKey = (KeyPair)existing;
    }

    public void Dispose()
    {
        _server.Dispose();
    }

    [Fact]
    public async Task HomeFeed_Surfaces_OwnNoteCreate_WithSourcePeople()
    {
        var client = BuildSignedClient(_actorIri, _actorKey);
        var note = ComposeNote.Build(_actorIri, "S36 home feed own post", to: [Public]);

        var posted = await client.PostNoteAsync(_actorIri, note);
        Assert.True(posted.IsSuccess, $"Post should succeed, got HTTP {(int)posted.StatusCode}: {posted.Body}");

        var noteIri = await FindCreatedNoteIriAsync("S36 home feed own post");

        // The exact request the home timeline makes (HomeTimeline.razor): the actor's feed with the
        // "people" source filter, signed as the actor (so the owner gate + visibility filter pass).
        var body = await FetchFeedBodyAsync(client, "source=people");

        var doc = Iris.Core.ActivityJson.Deserialize<IObject>(body)!;
        var items = Iris.Core.Collections.CollectionPageFactory.ResolveCollectionItems((KristofferStrube.ActivityStreams.Collection)doc);

        // The note's Create must be present in the served feed (S36: it was absent on the live host).
        var noteCreateIri = items
            .OfType<Activity>()
            .Where(a => a.Type?.FirstOrDefault() == "Create")
            .SelectMany(a => a.Object ?? [])
            .OfType<IObject>()
            .Any(o => o.Id == noteIri.Value);

        Assert.True(
            noteCreateIri,
            $"the own note Create ({noteIri.Value}) must be present in the home feed; the feed had {items.Count} items: {Truncate(body, 1200)}");
    }

    [Fact]
    public async Task HomeFeed_Surfaces_OwnNoteCreate_WithoutSourceFilter()
    {
        var client = BuildSignedClient(_actorIri, _actorKey);
        var note = ComposeNote.Build(_actorIri, "S36 no-source own post", to: [Public]);

        var posted = await client.PostNoteAsync(_actorIri, note);
        Assert.True(posted.IsSuccess, $"Post should succeed, got HTTP {(int)posted.StatusCode}: {posted.Body}");

        var noteIri = await FindCreatedNoteIriAsync("S36 no-source own post");

        // The unfiltered feed (the back-compat default) must also surface the own Create.
        var body = await FetchFeedBodyAsync(client, null);

        var doc = Iris.Core.ActivityJson.Deserialize<IObject>(body)!;
        var items = Iris.Core.Collections.CollectionPageFactory.ResolveCollectionItems((KristofferStrube.ActivityStreams.Collection)doc);

        var noteCreateIri = items
            .OfType<Activity>()
            .Where(a => a.Type?.FirstOrDefault() == "Create")
            .SelectMany(a => a.Object ?? [])
            .OfType<IObject>()
            .Any(o => o.Id == noteIri.Value);

        Assert.True(
            noteCreateIri,
            $"the own note Create ({noteIri.Value}) must be present in the home feed; the feed had {items.Count} items: {Truncate(body, 1200)}");
    }

    /// <summary>
    /// Issues a signed <c>GET /ap/v1/u/alice/feed</c> (the follow feed is owner-gated, so the request
    /// must be signed as the actor — an anonymous request is 403'd) and returns the response body.
    /// </summary>
    private async Task<string> FetchFeedBodyAsync(IActivityPubClient client, string? query)
    {
        var url = query is null ? $"{Base}/ap/v1/u/alice/feed" : $"{Base}/ap/v1/u/alice/feed?{query}";
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Accept.ParseAdd("application/activity+json");
        var response = await client.SendAsync(request);
        Assert.True(
            response.StatusCode == System.Net.HttpStatusCode.OK,
            $"the signed owner's follow-feed GET should be 200, got {(int)response.StatusCode}: {Truncate(await response.Content.ReadAsStringAsync(), 300)}");
        return await response.Content.ReadAsStringAsync();
    }

    // --- Helpers ------------------------------------------------------------------------

    private static readonly Iri Public = new("https://www.w3.org/ns/activitystreams#Public");

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max] + "…";

    private IPersistenceProvider GetPersistence()
    {
        using var scope = _services.CreateScope();
        return scope.ServiceProvider.GetRequiredService<IPersistenceProvider>();
    }

    private async Task<Iri> FindCreatedNoteIriAsync(string contentFragment)
    {
        var persistence = GetPersistence();
        var outbox = await persistence.Activities.GetOutboxAsync(_actorIri);

        foreach (var item in outbox)
        {
            var objects = (item as Activity)?.Object;
            if (objects is not { })
            {
                continue;
            }

            var embedded = objects.FirstOrDefault() as IObject;
            if (embedded is null || embedded.Content is null)
            {
                continue;
            }

            var content = string.Join(" ", embedded.Content);
            if (content.Contains(contentFragment, StringComparison.Ordinal)
                && embedded.Id is { Length: > 0 } id)
            {
                return new Iri(id);
            }
        }

        throw new InvalidOperationException($"No created note containing '{contentFragment}' found in the outbox.");
    }

    private IActivityPubClient BuildSignedClient(Iri actorIri, KeyPair key)
    {
        var keyStore = new InMemoryKeyStore();
        keyStore.PutKey(key);
        var keyProvider = new InMemoryKeyProvider(keyStore);
        keyProvider.RegisterKey(actorIri, key.KeyId);
        var signer = new HttpSignatureSigner(keyStore);

        var factory = new ActivityPubClientFactory(keyStore, keyProvider, signer);
        return factory.Create(
            new ActivityPubClientOptions { ActorId = actorIri, EnableRetry = false },
            new LazyHandler(() => _server.CreateHandler()));
    }

    private sealed class LocalActorDocumentFetcher(IPersistenceProvider persistence)
        : IActorDocumentFetcher
    {
        public async Task<KristofferStrube.ActivityStreams.Actor?> GetActorAsync(
            Iri actorIri, CancellationToken ct = default)
        {
            if (await persistence.Actors.TryGetActorAsync(actorIri, out var actor, ct) && actor is not null)
            {
                return actor;
            }

            if (actorIri.Value.Contains("/c/"))
            {
                if (await persistence.Communities.TryGetCommunityAsync(actorIri, out var community, ct)
                    && community is not null)
                {
                    return community;
                }
            }

            return null;
        }
    }
}
