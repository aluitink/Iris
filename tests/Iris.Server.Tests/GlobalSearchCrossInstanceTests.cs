using Iris.Core;
using Iris.Server.InMemory;
using Iris.Server.Services;
using KristofferStrube.ActivityStreams;
using Xunit;

namespace Iris.Server.Tests;

/// <summary>
/// S96 (cross-instance post search): the global search's content results include the requester's
/// followed <em>remote</em> posts that match the query (walked from the follows' outboxes by the
/// followed-feed service), in addition to the instance's local content. Anonymous requests and local
/// authors are local-only. A post already in the local store is not double-counted.
/// </summary>
public sealed class GlobalSearchCrossInstanceTests
{
    private const string LocalHost = "a.domain.local";
    private const string RemoteHost = "remote.example";
    private static readonly Iri LocalUser = new($"https://{LocalHost}/ap/v1/u/alice");
    private static readonly Iri RemoteAuthor = new($"https://{RemoteHost}/u/remoteuser");

    /// <summary>A fake followed-feed service that returns a fixed set of feed items (the remote follows'
    /// outbox content), ignoring the query/type filters (the search applies those itself after the walk).</summary>
    private sealed class FakeFollowFeed : IFollowFeedService
    {
        public FakeFollowFeed(IReadOnlyList<IObjectOrLink> items)
        {
            Items = items;
        }

        public IReadOnlyList<IObjectOrLink> Items { get; }

        public Task<IReadOnlyList<IObjectOrLink>> GetFeedAsync(
            Iri actorIri,
            string? query = null,
            string? activityType = null,
            int? threadDepth = null,
            Iri? requesterIri = null,
            string? source = null,
            bool bypassCache = false,
            CancellationToken ct = default)
        {
            return Task.FromResult(Items);
        }
    }

    private static async Task<InMemoryPersistenceProvider> SeedLocalAsync()
    {
        var p = new InMemoryPersistenceProvider();
        await p.Actors.PutActorAsync(new Person
        {
            Id = LocalUser.Value,
            PreferredUsername = "alice",
            Name = ["Alice"],
        });
        // A local post the search should always find (the local content pass).
        await p.Objects.PutObjectAsync(LocalNote("local-note", "a local post about kittens"));
        return p;
    }

    private static Note LocalNote(string tag, string content) => new()
    {
        Id = $"https://{LocalHost}/ap/v1/u/alice/notes/{tag}",
        Content = [content],
        AttributedTo = [new Link { Href = new Uri(LocalUser.Value) }],
        To = [new Link { Href = new Uri(Iri.Public.Value) }],
    };

    /// <summary>A remote post (a Create of a note by a remote author) as it would appear in the followed feed.</summary>
    private static IObjectOrLink RemoteCreate(string iri, string content, IObjectOrLink author) => new Create
    {
        Id = iri,
        Actor = [author],
        Object = [new Note
        {
            Id = iri,
            Content = [content],
            AttributedTo = [author],
            To = [new Link { Href = new Uri(Iri.Public.Value) }],
        }],
    };

    private static string[] Ids(IReadOnlyList<IObjectOrLink> items) =>
        items.Where(i => i is IObject { Id: { } id }).Select(i => (i as IObject)!.Id!).ToArray();

    [Fact]
    public async Task SignedIn_Requester_SeesLocalPlusFollowedRemotePosts()
    {
        var p = await SeedLocalAsync();
        var remoteNoteIri = $"https://{RemoteHost}/post/42";
        var feed = new FakeFollowFeed([
            RemoteCreate(remoteNoteIri, "a remote post about kittens", new Link { Href = new Uri(RemoteAuthor.Value) }),
        ]);
        var search = new GlobalSearchService(p, new Iri($"https://{LocalHost}"), feed);

        var ids = Ids(await search.SearchAsync("kittens", requesterIri: LocalUser));

        Assert.Contains(ids, id => id.Contains("local-note"));   // the local content pass
        Assert.Contains(ids, id => id == remoteNoteIri);          // the cross-instance (followed remote) post
    }

    [Fact]
    public async Task Anonymous_Requester_IsLocalOnly_NoCrossInstancePass()
    {
        var p = await SeedLocalAsync();
        var remoteNoteIri = $"https://{RemoteHost}/post/42";
        var feed = new FakeFollowFeed([
            RemoteCreate(remoteNoteIri, "a remote post about kittens", new Link { Href = new Uri(RemoteAuthor.Value) }),
        ]);
        var search = new GlobalSearchService(p, new Iri($"https://{LocalHost}"), feed);

        // Anonymous: there is no requester whose follows to walk, so only the local post matches.
        var ids = Ids(await search.SearchAsync("kittens", requesterIri: null));

        Assert.Contains(ids, id => id.Contains("local-note"));
        Assert.DoesNotContain(ids, id => id == remoteNoteIri);
    }

    [Fact]
    public async Task RemotePostAlreadyInLocalStore_IsNotDoubleCounted()
    {
        var p = await SeedLocalAsync();
        // A remote post that has ALSO been delivered into the local object store (S25 delivered content).
        var remoteNoteIri = $"https://{RemoteHost}/post/42";
        await p.Objects.PutObjectAsync(new Note
        {
            Id = remoteNoteIri,
            Content = ["a remote post about kittens"],
            AttributedTo = [new Link { Href = new Uri(RemoteAuthor.Value) }],
            To = [new Link { Href = new Uri(Iri.Public.Value) }],
        });
        var feed = new FakeFollowFeed([
            RemoteCreate(remoteNoteIri, "a remote post about kittens", new Link { Href = new Uri(RemoteAuthor.Value) }),
        ]);
        var search = new GlobalSearchService(p, new Iri($"https://{LocalHost}"), feed);

        var ids = Ids(await search.SearchAsync("kittens", requesterIri: LocalUser));

        // The remote note appears exactly once (the local-store copy is preferred; the feed copy is deduped).
        Assert.Single(ids, id => id == remoteNoteIri);
        Assert.Contains(ids, id => id.Contains("local-note"));
    }

    [Fact]
    public async Task LocalAuthorFeedItem_IsNotAddedOnlyRemoteSurfaceIsNew()
    {
        var p = await SeedLocalAsync();
        // A feed item whose author is LOCAL (on the instance base) — e.g. the requester's own post or a
        // local follow. It is already covered by the local content pass (or is the requester's own
        // outbox), so the cross-instance pass must not add a duplicate / second surface for it.
        var localAuthorPostIri = $"https://{LocalHost}/ap/v1/u/bob/notes/only-in-feed";
        var feed = new FakeFollowFeed([
            RemoteCreate(localAuthorPostIri, "a local-author post about kittens", new Link { Href = new Uri($"https://{LocalHost}/ap/v1/u/bob") }),
        ]);
        var search = new GlobalSearchService(p, new Iri($"https://{LocalHost}"), feed);

        // The feed item's author is local (IRI on the instance base) and its IRI is not in the local store,
        // but it is NOT a remote post — the cross-instance pass only adds REMOTE posts, so it is dropped.
        var ids = Ids(await search.SearchAsync("kittens", requesterIri: LocalUser));

        Assert.Contains(ids, id => id.Contains("local-note"));
        Assert.DoesNotContain(ids, id => id == localAuthorPostIri);
    }

    [Fact]
    public async Task NoFollowFeedService_IsLocalOnly()
    {
        var p = await SeedLocalAsync();
        // No followed-feed service (a host/unit test that builds the search with persistence only) →
        // local-only (the pre-S96 behavior), even for a signed-in requester.
        var search = new GlobalSearchService(p, new Iri($"https://{LocalHost}"));

        var ids = Ids(await search.SearchAsync("kittens", requesterIri: LocalUser));

        Assert.Single(ids);
        Assert.Contains(ids, id => id.Contains("local-note"));
    }

    [Fact]
    public async Task Nested_AnnounceWrappingCreate_IsUnwrappedToEmbeddedPost()
    {
        var p = await SeedLocalAsync();
        // S96: a community post delivered from a remote instance arrives NESTED — an Announce whose object
        // is a Create whose object is the actual post (a Lemmy community post is an Announce of a Create
        // of a Page/Note). A single-level unwrap sees the Announce -> the Create (also an activity) and
        // returns null, dropping the post. The fix recurses through the nesting to the embedded post.
        var remotePostIri = $"https://{RemoteHost}/post/77";
        var remoteAuthorLink = new Link { Href = new Uri(RemoteAuthor.Value) };
        var nested = new Announce
        {
            Id = remotePostIri,
            Actor = [remoteAuthorLink],
            Object = [new Create
            {
                Id = remotePostIri,
                Actor = [remoteAuthorLink],
                Object = [new Note
                {
                    Id = remotePostIri,
                    Content = ["a nested remote community post about kittens"],
                    AttributedTo = [remoteAuthorLink],
                    To = [new Link { Href = new Uri(Iri.Public.Value) }],
                }],
            }],
        };
        var feed = new FakeFollowFeed([nested]);
        var search = new GlobalSearchService(p, new Iri($"https://{LocalHost}"), feed);

        var ids = Ids(await search.SearchAsync("kittens", requesterIri: LocalUser));

        Assert.Contains(ids, id => id == remotePostIri);   // the nested post is unwrapped and surfaced
        Assert.Contains(ids, id => id.Contains("local-note"));
    }
}
