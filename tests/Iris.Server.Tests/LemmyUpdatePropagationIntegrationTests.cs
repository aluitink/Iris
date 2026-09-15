using Iris.Core;
using Iris.Server;
using Iris.Server.Delivery;
using Iris.Server.InMemory;
using Iris.Server.Inbox;
using Iris.Server.Media;
using Iris.Testing;
using KristofferStrube.ActivityStreams;
using Xunit;
using ActivityObject = KristofferStrube.ActivityStreams.Object;

namespace Iris.Server.Tests;

/// <summary>
/// 138.22 (edit/update propagation into the archive) — verifies that a Lemmy-side post/comment
/// <c>Update</c> updates Iris's locally archived copy, so the archive doesn't go stale. The
/// <see cref="UpdateActivityHandler"/> accepts an inbound <c>Update</c> when the updating actor
/// owns the stored object (the object's <c>attributedTo</c> matches the activity's <c>actor</c>),
/// re-stores the updated content under the same IRI, and stamps the <c>updated</c> timestamp.
/// A remote (Lemmy) actor updating a locally-archived copy it owns is the 138.22 scenario: the
/// local copy is refreshed in place, no re-propagation (the actor is not local).
/// </summary>
public sealed class LemmyUpdatePropagationIntegrationTests
{
    private const string Host = "updatetest.domain.local";
    private const string Member = "bob";
    private const string RemoteHost = "lemmy.luit.ink";

    private static readonly Iri MemberIri = new($"https://{Host}/ap/v1/u/{Member}");
    private static readonly Iri RemoteAuthorIri = new($"https://{RemoteHost}/u/lemmyadmin");
    private static readonly Iri PostIri = new($"https://{RemoteHost}/post/300");
    private static readonly Iri CommentIri = new($"https://{RemoteHost}/comment/400");

    [Fact]
    public async Task RemoteAuthor_UpdatesArchivedPost_LocalStoreRefreshed()
    {
        var (persistence, handler) = await BuildFixtureAsync();

        // (a) The original post is in the local store.
        Assert.True(await persistence.Objects.TryGetObjectAsync(PostIri, out var original));
        var origPage = Assert.IsType<Page>(original);
        Assert.Contains("Original title", origPage.Name!);
        Assert.Contains("<p>Original body</p>", origPage.Content!);

        // (b) Deliver a Lemmy-side Update: the author edits the post title and body.
        var updatedPage = new Page
        {
            Id = PostIri.Value,
            Name = ["Edited title"],
            Content = ["<p>Edited body</p>"],
            AttributedTo = [new Link { Href = RemoteAuthorIri.Uri }],
        };
        var update = new Update
        {
            Id = $"https://{RemoteHost}/c/interop/update/post/300/1",
            Actor = [new Link { Href = RemoteAuthorIri.Uri }],
            Object = [updatedPage],
        };
        await handler.HandleAsync(new InboxDelivery(MemberIri, update), update);

        // (c) The local store now has the updated content (same IRI, new title/body).
        Assert.True(await persistence.Objects.TryGetObjectAsync(PostIri, out var refreshed));
        var newPage = Assert.IsType<Page>(refreshed);
        Assert.Contains("Edited title", newPage.Name!);
        Assert.Contains("<p>Edited body</p>", newPage.Content!);

        // (d) The original content is gone (replaced, not appended).
        Assert.DoesNotContain("Original title", newPage.Name!);
    }

    [Fact]
    public async Task RemoteAuthor_UpdatesArchivedComment_LocalStoreRefreshed()
    {
        var (persistence, handler) = await BuildFixtureAsync();

        // (a) The original comment is in the local store.
        Assert.True(await persistence.Objects.TryGetObjectAsync(CommentIri, out var original));
        var origNote = Assert.IsType<Note>(original);
        Assert.Contains("<p>Original comment</p>", origNote.Content!);

        // (b) Deliver a Lemmy-side Update: the author edits the comment.
        var updatedNote = new Note
        {
            Id = CommentIri.Value,
            Content = ["<p>Edited comment</p>"],
            InReplyTo = [new Link { Href = PostIri.Uri }],
            AttributedTo = [new Link { Href = RemoteAuthorIri.Uri }],
        };
        var update = new Update
        {
            Id = $"https://{RemoteHost}/c/interop/update/c/400/1",
            Actor = [new Link { Href = RemoteAuthorIri.Uri }],
            Object = [updatedNote],
        };
        await handler.HandleAsync(new InboxDelivery(MemberIri, update), update);

        // (c) The local store now has the updated comment.
        Assert.True(await persistence.Objects.TryGetObjectAsync(CommentIri, out var refreshed));
        var newNote = Assert.IsType<Note>(refreshed);
        Assert.Contains("<p>Edited comment</p>", newNote.Content!);
        Assert.DoesNotContain("<p>Original comment</p>", newNote.Content!);
    }

    [Fact]
    public async Task NonOwner_RemoteActor_UpdateIsNoOp()
    {
        var (persistence, handler) = await BuildFixtureAsync();

        // A different remote actor (not the post's author) attempts to update the post.
        var impersonatorIri = new Iri($"https://{RemoteHost}/u/impersonator");
        var forgedPage = new Page
        {
            Id = PostIri.Value,
            Name = ["Forged title"],
            Content = ["<p>Forged body</p>"],
            AttributedTo = [new Link { Href = impersonatorIri.Uri }],
        };
        var update = new Update
        {
            Id = $"https://{RemoteHost}/c/interop/update/post/300/forged",
            Actor = [new Link { Href = impersonatorIri.Uri }],
            Object = [forgedPage],
        };
        await handler.HandleAsync(new InboxDelivery(MemberIri, update), update);

        // The local store is unchanged (the impersonator is not the post's attributedTo).
        Assert.True(await persistence.Objects.TryGetObjectAsync(PostIri, out var stored));
        var page = Assert.IsType<Page>(stored);
        Assert.Contains("Original title", page.Name!);
        Assert.DoesNotContain("Forged title", page.Name!);
    }

    [Fact]
    public async Task Update_TombstonedObject_IsNoOp()
    {
        var (persistence, handler) = await BuildFixtureAsync();

        // Tombstone the post first (simulate a prior Delete).
        var tombstone = new Tombstone
        {
            Id = PostIri.Value,
        };
        await persistence.Objects.PutObjectAsync(tombstone);

        // Now deliver an Update for the tombstoned post.
        var updatedPage = new Page
        {
            Id = PostIri.Value,
            Name = ["Resurrected"],
            Content = ["<p>Should not appear</p>"],
            AttributedTo = [new Link { Href = RemoteAuthorIri.Uri }],
        };
        var update = new Update
        {
            Id = $"https://{RemoteHost}/c/interop/update/post/300/late",
            Actor = [new Link { Href = RemoteAuthorIri.Uri }],
            Object = [updatedPage],
        };
        await handler.HandleAsync(new InboxDelivery(MemberIri, update), update);

        // The tombstone is still in place (the Update did not resurrect the object).
        Assert.True(await persistence.Objects.TryGetObjectAsync(PostIri, out var stored));
        Assert.IsType<Tombstone>(stored);
    }

    [Fact]
    public async Task Update_UnknownObject_IsNoOp()
    {
        var (persistence, handler) = await BuildFixtureAsync();

        // An Update for an object that was never archived locally.
        var unknownIri = new Iri($"https://{RemoteHost}/post/999");
        var unknownPage = new Page
        {
            Id = unknownIri.Value,
            Name = ["Unknown"],
            Content = ["<p>Never was here</p>"],
            AttributedTo = [new Link { Href = RemoteAuthorIri.Uri }],
        };
        var update = new Update
        {
            Id = $"https://{RemoteHost}/c/interop/update/post/999/1",
            Actor = [new Link { Href = RemoteAuthorIri.Uri }],
            Object = [unknownPage],
        };
        await handler.HandleAsync(new InboxDelivery(MemberIri, update), update);

        // The object was not created by the Update (Updates don't create, they refresh).
        Assert.False(await persistence.Objects.TryGetObjectAsync(unknownIri, out _));
    }

    // --- Fixture -----------------------------------------------------------------------------------

    private static async Task<(InMemoryPersistenceProvider, UpdateActivityHandler)> BuildFixtureAsync()
    {
        var persistence = new InMemoryPersistenceProvider();
        TestSeeder.SeedCommunityWithKey(persistence, Host, "archival", memberIri: MemberIri);
        TestSeeder.SeedPersonWithKey(persistence, Host, Member);

        // Archive a Lemmy post (Page) and a comment (Note) — as the 138.20 backfill would.
        var post = new Page
        {
            Id = PostIri.Value,
            Name = ["Original title"],
            Content = ["<p>Original body</p>"],
            AttributedTo = [new Link { Href = RemoteAuthorIri.Uri }],
        };
        await persistence.Objects.PutObjectAsync(post);

        var comment = new Note
        {
            Id = CommentIri.Value,
            Content = ["<p>Original comment</p>"],
            InReplyTo = [new Link { Href = PostIri.Uri }],
            AttributedTo = [new Link { Href = RemoteAuthorIri.Uri }],
        };
        await persistence.Objects.PutObjectAsync(comment);

        // The handler: a stub local-actor resolver (the remote Lemmy author is NOT local),
        // a no-op propagation service, a no-op media warmer, and the instance base.
        var localActors = new StubLocalActorResolver();
        var propagation = new NoOpDeletePropagationService();
        var mediaWarmer = new NoOpMediaWarmer();
        var options = new Microsoft.Extensions.Options.OptionsWrapper<Iris.Server.ActivityPubServerOptions>(
            new Iris.Server.ActivityPubServerOptions());

        var handler = new UpdateActivityHandler(
            persistence, localActors, propagation, mediaWarmer, options);

        return (persistence, handler);
    }

    private sealed class StubLocalActorResolver : ILocalActorResolver
    {
        public Task<bool> IsLocalActorAsync(Iri actorIri, CancellationToken ct = default)
            => Task.FromResult(false);
    }

    private sealed class NoOpDeletePropagationService : IDeletePropagationService
    {
        public Task PropagateUpdateAsync(Iri authorIri, Iri objectIri, Update activity, CancellationToken ct = default)
            => Task.CompletedTask;
        public Task PropagateDeleteAsync(Iri authorIri, Iri objectIri, Delete activity, IObject? tombstone, CancellationToken ct = default)
            => Task.CompletedTask;
    }

    private sealed class NoOpMediaWarmer : IMediaWarmer
    {
        public Task WarmAsync(IObject? obj, Iri instanceBase, CancellationToken ct = default)
            => Task.CompletedTask;
    }
}
