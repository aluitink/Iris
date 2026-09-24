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

    [Fact]
    public async Task HomeFeed_SourceCommunities_FirstLink_Preserves_Source_And_Excludes_OwnPost()
    {
        // S102 regression: the Home Communities tab (GET /feed?source=communities) showed the
        // user's own personal posts (the Posts-tab content) instead of the followed communities'
        // content. The root cause was that the collection document's `first` link dropped the
        // ?source= param; the Iris WASM client's GetCollectionAsync follows `first` to fetch the
        // actual page, so the follow-up read was served UNFILTERED (the full merged feed) and the
        // own post leaked into the Communities tab. This test pins both halves of the fix:
        //   (1) the `first` link of a ?source=communities response carries ?source=communities, and
        //   (2) the ?source=communities feed excludes the actor's own personal post (even though the
        //       post IS present in the unfiltered feed — proving the source filter, not absence,
        //       is what keeps it out, so following the filtered `first` link cannot leak it in).
        var client = BuildSignedClient(_actorIri, _actorKey);

        // A personal (non-community) note by the actor — attributedTo the actor (no Group), so it
        // belongs in the "people" source only, never "communities".
        var ownNote = ComposeNote.Build(_actorIri, "S102 own personal post", to: [Public]);
        var postedOwn = await client.PostNoteAsync(_actorIri, ownNote);
        Assert.True(postedOwn.IsSuccess, $"Own post should succeed, got HTTP {(int)postedOwn.StatusCode}: {postedOwn.Body}");
        var ownNoteIri = await FindCreatedNoteIriAsync("S102 own personal post");

        // Sanity: the UNFILTERED feed (the back-compat default) DOES surface the own post — so the
        // exclusion below is attributable to the ?source=communities filter, not to the post being
        // absent from the feed entirely.
        var unfilteredBody = await FetchFeedBodyAsync(client, null);
        var unfilteredDoc = (System.Text.Json.JsonElement)System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>(unfilteredBody)!;
        Assert.True(
            FeedContainsNote(unfilteredDoc, ownNoteIri.Value),
            $"the own personal post ({ownNoteIri.Value}) must be present in the UNFILTERED feed (sanity); body: {Truncate(unfilteredBody, 800)}");

        // Fetch the Communities-tab feed (?source=communities), exactly as HomeTimeline.razor does.
        var body = await FetchFeedBodyAsync(client, "source=communities");
        var doc = (System.Text.Json.JsonElement)System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>(body)!;

        // (1) The `first` link must preserve the ?source=communities filter.
        Assert.True(doc.TryGetProperty("first", out var firstEl), $"the ?source=communities feed document must carry a `first` link; body: {Truncate(body, 800)}");
        var firstIri = firstEl.GetString();
        Assert.NotNull(firstIri);
        Assert.True(
            firstIri.Contains("source=communities"),
            $"the `first` link ({firstIri}) must carry ?source=communities so a client that follows it re-fetches the filtered feed (S102)");

        // (2) The ?source=communities feed must exclude the own personal post (a personal note is
        //     "people" source, never "communities").
        Assert.False(
            FeedContainsNote(doc, ownNoteIri.Value),
            $"the actor's own personal post ({ownNoteIri.Value}) must NOT be in the ?source=communities feed (S102: it leaked via the unfiltered `first` follow-up); body: {Truncate(body, 800)}");
    }

    [Fact]
    public async Task HomeFeed_SourcePeople_FirstLink_Preserves_Source_And_Includes_OwnPost()
    {
        // S102 companion: the Posts-tab feed (?source=people) must also carry the filter in its
        // `first` link and still surface the actor's own personal post (regression guard — the
        // filter must not accidentally hide the user's own content from the Posts tab).
        var client = BuildSignedClient(_actorIri, _actorKey);
        var ownNote = ComposeNote.Build(_actorIri, "S102 people-tab own post", to: [Public]);
        var posted = await client.PostNoteAsync(_actorIri, ownNote);
        Assert.True(posted.IsSuccess, $"Own post should succeed, got HTTP {(int)posted.StatusCode}: {posted.Body}");
        var ownNoteIri = await FindCreatedNoteIriAsync("S102 people-tab own post");

        var body = await FetchFeedBodyAsync(client, "source=people");
        var doc = (System.Text.Json.JsonElement)System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>(body)!;

        Assert.True(doc.TryGetProperty("first", out var firstEl), $"the ?source=people feed document must carry a `first` link; body: {Truncate(body, 800)}");
        var firstIri = firstEl.GetString();
        Assert.NotNull(firstIri);
        Assert.True(
            firstIri.Contains("source=people"),
            $"the `first` link ({firstIri}) must carry ?source=people (S102)");

        Assert.True(
            FeedContainsNote(doc, ownNoteIri.Value),
            $"the actor's own personal post ({ownNoteIri.Value}) must be present in the ?source=people feed; body: {Truncate(body, 800)}");
    }

    /// <summary>
    /// True when the note at <paramref name="noteIri"/> appears in the collection page document —
    /// either as the <c>Object</c> of a <c>Create</c> activity (the feed's wire shape: each post is a
    /// <c>Create</c> wrapping the note) or as a bare item. Mirrors the S36 test's Create→Object→Id
    /// check, which is robust to the feed embedding the note inside a Create activity.
    /// </summary>
    private static bool FeedContainsNote(System.Text.Json.JsonElement doc, string noteIri)
    {
        if (!doc.TryGetProperty("orderedItems", out var items) || items.ValueKind != System.Text.Json.JsonValueKind.Array)
        {
            return false;
        }

        foreach (var item in items.EnumerateArray())
        {
            // A bare item whose id is the note.
            if (item.ValueKind == System.Text.Json.JsonValueKind.String)
            {
                if (item.GetString() == noteIri)
                {
                    return true;
                }
                continue;
            }

            if (!item.TryGetProperty("id", out var idProp))
            {
                continue;
            }

            if (idProp.GetString() == noteIri)
            {
                return true;
            }

            // A Create activity: check its `object` (the wrapped note) for the note IRI.
            if (item.TryGetProperty("object", out var objProp))
            {
                var objIri = objProp.ValueKind == System.Text.Json.JsonValueKind.String
                    ? objProp.GetString()
                    : objProp.TryGetProperty("id", out var objId) ? objId.GetString() : null;
                if (objIri == noteIri)
                {
                    return true;
                }
            }
        }

        return false;
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
