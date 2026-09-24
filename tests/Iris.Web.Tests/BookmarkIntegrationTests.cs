using Iris.Client;
using Iris.Client.Auth;
using Iris.Client.Pipeline;
using Iris.Core;
using Iris.Core.Compose;
using Iris.Core.Identity;
using Iris.Core.Signing;
using Iris.Server;
using Iris.Server.InMemory;
using Iris.Server.Security;
using Iris.Server.Stores;
using Iris.Testing;
using Iris.Web;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ActivityObject = KristofferStrube.ActivityStreams.Object;

namespace Iris.Web.Tests;

/// <summary>
/// Integration tests for S111 — <em>bookmarks</em>. They boot the real app (in-memory persistence)
/// in-process via <see cref="WebAppFactory"/> in a <see cref="TestServer"/> and exercise the
/// <c>POST /local/v1/u/{handle}/bookmarks/{target}</c> / <c>GET .../bookmarks</c> / unbookmark
/// endpoints through the client's <see cref="ILocalModerationClient"/> (the same seam the Blazor
/// UI's bookmark button and Profile Bookmarks tab use). The defect S111 QA caught was client-side
/// (the feed bookmark state was not restored on reload, and the Bookmarks tab rendered only a
/// count) — these tests pin the server + client contract the fix depends on: a bookmarked IRI is
/// listed by GET, an unbookmark removes it, and the bookmarked object is still resolvable by its
/// IRI (the tab's post-card rendering path).
/// </summary>
public sealed class BookmarkIntegrationTests : IDisposable
{
    private const string Base = "https://bookmark.test.local";

    private readonly TestServer _server;
    private readonly IServiceProvider _services;
    private readonly Iri _actorIri;
    private readonly KeyPair _actorKey;

    public BookmarkIntegrationTests()
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
    public async Task Bookmark_ListsBookmarkedIri_AndUnbookmarkRemovesIt()
    {
        var (client, mod) = BuildClients();

        // Post a note so there is an object IRI to bookmark.
        var note = ComposeNote.Build(_actorIri, "Bookmark me", to: [Public]);
        var posted = await client.PostNoteAsync(_actorIri, note);
        Assert.True(posted.IsSuccess, $"Post should succeed, got HTTP {(int)posted.StatusCode}: {posted.Body}");

        var (maybeObjectIri, _) = await FindCreatedNoteAsync("Bookmark me");
        Iri objectIri = maybeObjectIri ?? throw new InvalidOperationException("No created note found.");

        // Fresh actor has no bookmarks: GET /bookmarks returns an empty list.
        var empty = await mod.GetBookmarksAsync(_actorIri);
        Assert.True(empty.IsSuccess, $"GET bookmarks should succeed, got HTTP {(int)empty.StatusCode}");
        var emptyList = System.Text.Json.JsonSerializer.Deserialize<List<string>>(empty.Body ?? "[]");
        Assert.Empty(emptyList!);

        // Bookmark the note (POST /local/v1/u/alice/bookmarks/{target}).
        var bookmarked = await mod.BookmarkAsync(_actorIri, objectIri!);
        Assert.True(bookmarked.IsSuccess, $"Bookmark should succeed, got HTTP {(int)bookmarked.StatusCode}: {bookmarked.Body}");

        // GET /bookmarks now lists the bookmarked object IRI.
        var listed = await mod.GetBookmarksAsync(_actorIri);
        Assert.True(listed.IsSuccess, $"GET bookmarks should succeed, got HTTP {(int)listed.StatusCode}");
        var iriValues = System.Text.Json.JsonSerializer.Deserialize<List<string>>(listed.Body ?? "[]");
        Assert.NotNull(iriValues);
        Assert.Contains(iriValues, v => string.Equals(v, objectIri!.Value, StringComparison.OrdinalIgnoreCase));

        // The bookmarked object is still resolvable by IRI — the Profile Bookmarks tab fetches each
        // listed IRI to render the post card.
        var resolved = await client.GetObjectAsync(objectIri);
        Assert.NotNull(resolved);
        Assert.Equal("Bookmark me", JoinContent(resolved));

        // Unbookmark (?unbookmark=true on the same route): the IRI drops out of the listing.
        var unbookmarked = await mod.UnbookmarkAsync(_actorIri, objectIri!);
        Assert.True(unbookmarked.IsSuccess, $"Unbookmark should succeed, got HTTP {(int)unbookmarked.StatusCode}: {unbookmarked.Body}");

        var after = await mod.GetBookmarksAsync(_actorIri);
        Assert.True(after.IsSuccess);
        var afterList = System.Text.Json.JsonSerializer.Deserialize<List<string>>(after.Body ?? "[]");
        Assert.NotNull(afterList);
        Assert.DoesNotContain(afterList, v => string.Equals(v, objectIri.Value, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Bookmark_BookmarkTwice_ListsIriOnce()
    {
        var (client, mod) = BuildClients();

        var note = ComposeNote.Build(_actorIri, "Double bookmark", to: [Public]);
        var posted = await client.PostNoteAsync(_actorIri, note);
        Assert.True(posted.IsSuccess);

        var (maybeObjectIri, _) = await FindCreatedNoteAsync("Double bookmark");
        Iri objectIri = maybeObjectIri ?? throw new InvalidOperationException("No created note found.");

        // Bookmarking the same object twice must not duplicate the entry (the UI fires one POST per
        // click; a re-created bar re-seeds from the list, so a duplicate would show twice in the tab).
        Assert.True((await mod.BookmarkAsync(_actorIri, objectIri)).IsSuccess);
        Assert.True((await mod.BookmarkAsync(_actorIri, objectIri)).IsSuccess);

        var listed = await mod.GetBookmarksAsync(_actorIri);
        Assert.True(listed.IsSuccess);
        var iriValues = System.Text.Json.JsonSerializer.Deserialize<List<string>>(listed.Body ?? "[]");
        var matches = iriValues!.Count(v => string.Equals(v, objectIri.Value, StringComparison.OrdinalIgnoreCase));
        Assert.Equal(1, matches);
    }

    [Fact]
    public async Task Bookmarks_Unauthenticated_IsRejected()
    {
        // A client without the actor's local credentials (no Basic auth, no cookie) is rejected: the
        // endpoints are owner-only. The test transport carries neither, so GET /bookmarks must 401.
        var (_, mod) = BuildClients(withLocalCredentials: false);

        var result = await mod.GetBookmarksAsync(_actorIri);
        Assert.False(result.IsSuccess);
        Assert.Equal(401, (int)result.StatusCode);
    }

    // --- Helpers ------------------------------------------------------------------------

    private static readonly Iri Public = new("https://www.w3.org/ns/activitystreams#Public");

    private static string JoinContent(KristofferStrube.ActivityStreams.IObject? obj)
        => obj?.Content is { } content ? string.Join(" ", content) : string.Empty;

    private (IActivityPubClient Client, ILocalModerationClient Mod) BuildClients(bool withLocalCredentials = true)
    {
        var keyStore = new InMemoryKeyStore();
        keyStore.PutKey(_actorKey);
        var keyProvider = new InMemoryKeyProvider(keyStore);
        keyProvider.RegisterKey(_actorIri, _actorKey.KeyId);
        var signer = new HttpSignatureSigner(keyStore);

        var options = new ActivityPubClientOptions { ActorId = _actorIri, EnableRetry = false };
        if (withLocalCredentials)
        {
            options.LocalCredentials = new ProxyCredentials(WebAppFactory.SeedHandle, WebAppFactory.SeedHandle);
        }

        var factory = new ActivityPubClientFactory(keyStore, keyProvider, signer);
        var client = factory.Create(options, new LazyHandler(() => _server.CreateHandler()));
        var mod = factory.CreateLocalModerationClient(options, new LazyHandler(() => _server.CreateHandler()));
        return (client, mod);
    }

    private IPersistenceProvider GetPersistence()
    {
        using var scope = _services.CreateScope();
        return scope.ServiceProvider.GetRequiredService<IPersistenceProvider>();
    }

    /// <summary>
    /// Scans the actor's outbox for the <c>Create</c> whose embedded object's content contains
    /// <paramref name="contentFragment"/>, and returns the stored object (looked up by its IRI) plus the
    /// object IRI.
    /// </summary>
    private async Task<(Iri? ObjectIri, KristofferStrube.ActivityStreams.IObject? Object)> FindCreatedNoteAsync(string contentFragment)
    {
        var persistence = GetPersistence();
        var outbox = await persistence.Activities.GetOutboxAsync(_actorIri);

        foreach (var item in outbox)
        {
            var objects = (item as KristofferStrube.ActivityStreams.Activity)?.Object;
            if (objects is not { })
            {
                continue;
            }

            var embedded = objects.FirstOrDefault() as KristofferStrube.ActivityStreams.IObject;
            if (embedded is null || embedded.Content is null)
            {
                continue;
            }

            var content = string.Join(" ", embedded.Content);
            if (!content.Contains(contentFragment, StringComparison.Ordinal))
            {
                continue;
            }

            Iri? objectIri = embedded.Id is { Length: > 0 } id ? new Iri(id) : null;
            KristofferStrube.ActivityStreams.IObject? stored = null;
            if (objectIri is { } resolvedIri)
            {
                if (await persistence.Objects.TryGetObjectAsync(resolvedIri, out var foundObj) && foundObj is not null)
                {
                    stored = foundObj;
                }
            }

            return (objectIri, stored ?? embedded);
        }

        throw new InvalidOperationException($"No created note containing '{contentFragment}' found in the outbox.");
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
