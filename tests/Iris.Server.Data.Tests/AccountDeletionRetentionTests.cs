using Iris.Core;
using Iris.Server.Data.Accounts;
using Iris.Server.Stores;
using KristofferStrube.ActivityStreams;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Iris.Server.Data.Tests;

/// <summary>
/// 139.3 Scenario 11 (retention / right-to-deletion): a review scenario that deletes a local account
/// and confirms the account's content is handled per the documented retention model (Phase 136.19) —
/// no orphaned references that break a read path, no broken links elsewhere, a consistent
/// post-deletion state.
///
/// The deletion performed here mirrors <c>AccountDeletionService.DeleteAsync</c> (which lives in
/// <c>Iris.Web</c> and so cannot be referenced from this project) exactly: tombstone each of the
/// actor's content objects, remove the actor from the actor store, and delete the account row.
///
/// The documented retention model (Phase 136.19) states: tombstones are permanent; read paths already
/// exclude tombstones from search/listing; the activity store retains all activities (append-only)
/// with no pruning; like/announce edges on tombstoned objects are not swept (a "bounded stale
/// artifact, not user-visible"). This test verifies the *consistent* half of that model (the parts
/// that must hold for a coherent post-deletion state) and, separately, *demonstrates* what survives
/// (the activities, box items, and edges that the model explicitly tolerates as retained) so the
/// review's "no broken links elsewhere" bar is checked with evidence rather than assumed.
/// </summary>
public sealed class AccountDeletionRetentionTests : IClassFixture<PostgresFixture>
{
    private readonly PostgresFixture _fixture;

    public AccountDeletionRetentionTests(PostgresFixture fixture)
    {
        _fixture = fixture;
    }

    private static Iri Iri(string value) => new(value);

    private static Link Link(string iri) => new() { Href = new Uri(iri) };

    private (IPersistenceProvider Provider, EfUserAccountStore Accounts) NewContexts()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Iris:ConnectionString"] = _fixture.ConnectionString,
                ["Iris:MediaBlobDir"] = Path.Combine(_fixture.BlobRoot, Guid.NewGuid().ToString("N")),
            })
            .Build();
        var services = new ServiceCollection();
        services.AddEntityFrameworkPersistence(config);
        var sp = services.BuildServiceProvider();
        var provider = sp.GetRequiredService<IPersistenceProvider>();
        var factory = sp.GetRequiredService<IDbContextFactory<IrisDbContext>>();
        return (provider, new EfUserAccountStore(factory));
    }

    [Fact]
    public async Task DeletedAccount_ContentTombstoned_ActorGone_AccountGone_SearchExcludes_AndRetainedRowsSurvive()
    {
        var (p, accounts) = NewContexts();
        var ns = $"delret-{Guid.NewGuid():N}";
        var baseUri = $"https://{ns}.example.org/ap/v1";

        // --- Seed: two local actors, a community, alice's two posts, and three edges. ---
        var alice = Iri($"{baseUri}/u/alice");
        var bob = Iri($"{baseUri}/u/bob");
        var community = Iri($"{baseUri}/c/{ns}");

        await p.Actors.PutActorAsync(new Person
        {
            Id = alice.Value,
            PreferredUsername = $"alice-{ns}",
            Name = [$"Alice {ns}"],
            Summary = ["alice retention subject"],
        });
        await p.Actors.PutActorAsync(new Person
        {
            Id = bob.Value,
            PreferredUsername = $"bob-{ns}",
            Name = [$"Bob {ns}"],
            Summary = ["bob the follower"],
        });

        // alice's two notes (content objects) + the Create activities + the outbox box items.
        var note1 = Iri($"{baseUri}/objects/note1-{ns}");
        var note2 = Iri($"{baseUri}/objects/note2-{ns}");
        var note1Content = $"alice first post {ns} with the delretneedle token";
        var note2Content = $"alice second post {ns} no needle";

        await p.Objects.PutObjectAsync(new Note { Id = note1.Value, AttributedTo = [Link(alice.Value)], Content = [note1Content] });
        await p.Objects.PutObjectAsync(new Note { Id = note2.Value, AttributedTo = [Link(alice.Value)], Content = [note2Content] });

        var create1 = new Create
        {
            Id = $"{baseUri}/activities/create1-{ns}",
            Actor = [Link(alice.Value)],
            Object = [new Note { Id = note1.Value, AttributedTo = [Link(alice.Value)], Content = [note1Content] }],
        };
        var create2 = new Create
        {
            Id = $"{baseUri}/activities/create2-{ns}",
            Actor = [Link(alice.Value)],
            Object = [new Note { Id = note2.Value, AttributedTo = [Link(alice.Value)], Content = [note2Content] }],
        };
        await p.Activities.PutActivityAsync(create1);
        await p.Activities.PutActivityAsync(create2);
        await p.Activities.AddToOutboxAsync(alice, create1);
        await p.Activities.AddToOutboxAsync(alice, create2);

        // Three edges that reference alice: bob follows alice; alice is a community member; bob likes
        // alice's first post.
        await p.Follows.RecordFollowAsync(bob, alice);
        await p.Communities.AddMemberAsync(community, alice);
        await p.Likes.RecordLikeAsync(bob, note1);

        // The account row linked to alice's actor.
        var account = new UserAccount
        {
            Id = Guid.NewGuid(),
            Username = $"alice-{ns}",
            PasswordHash = "hash",
            ActorId = alice,
        };
        await accounts.CreateAsync(account);

        // --- Delete: mirror AccountDeletionService.DeleteAsync exactly. ---
        var objectsToDelete = await p.Objects.ListByActorAsync(alice);
        foreach (var obj in objectsToDelete)
        {
            if (obj is Tombstone)
            {
                continue;
            }

            await p.Objects.PutObjectAsync(new Tombstone { Id = obj.Id, Deleted = DateTime.UtcNow });
        }

        await p.Actors.RemoveActorAsync(alice);
        var accountDeleted = await accounts.DeleteAsync(account.Id);

        // --- Consistent post-deletion state (the parts that MUST hold). ---

        // The account row is deleted.
        Assert.True(accountDeleted);

        // The actor is gone.
        var actorFound = await p.Actors.TryGetActorAsync(alice, out var deletedActor);
        Assert.False(actorFound);
        Assert.Null(deletedActor);

        // Both of alice's content objects serve as Tombstones (the IRIs still resolve as "deleted").
        Assert.True(await p.Objects.TryGetObjectAsync(note1, out var t1));
        Assert.IsType<Tombstone>(t1);
        Assert.True(await p.Objects.TryGetObjectAsync(note2, out var t2));
        Assert.IsType<Tombstone>(t2);

        // The account row is gone (by id and by username).
        Assert.Null(await accounts.FindByIdAsync(account.Id));
        Assert.Null(await accounts.FindByUsernameAsync(account.Username));

        // Object search excludes the (now tombstoned) content, including the needle.
        Assert.Equal(0, await p.Objects.CountSearchMatchesAsync($"delretneedle {ns}"));
        Assert.Empty(await p.Objects.SearchObjectsAsync($"delretneedle {ns}", limit: 10, offset: 0));

        // Actor search does not return the deleted actor.
        var actorHits = await p.Actors.SearchActorsAsync($"Alice {ns}", limit: 10, offset: 0, localOnly: true);
        Assert.Empty(actorHits);

        // The other local actor (bob) is untouched and still resolves.
        Assert.True(await p.Actors.TryGetActorAsync(bob, out var bobAfter));
        Assert.NotNull(bobAfter);

        // --- What survives (the model's tolerated "bounded stale artifacts") — evidence for the
        //     "no broken links elsewhere" review bar. These are NOT cleaned up by deletion. ---

        // (a) alice's outbox still returns both Create activities (BoxItems + Activities survive),
        //     and the embedded content is the ORIGINAL note, not a Tombstone.
        var outbox = await p.Activities.GetOutboxAsync(alice);
        Assert.Equal(2, outbox.Count);
        var created = outbox.Where(item => item is Create).ToList();
        Assert.Equal(2, created.Count);

        // The outbox's Create activities still embed the ORIGINAL notes (not Tombstones): the
        // object-document endpoint is the path that serves the Tombstone, consistent with the 136.19
        // model's note that the retained artifacts are "not user-visible" via the object-document path.
        // (The outbox is served newest-first, so the embedded object identity varies by ordering; the
        // invariant under test is the type, not a specific note IRI.)
        foreach (var item in created)
        {
            var embedded = ((Create)item).Object!.First();
            Assert.True(embedded is IObject);
            Assert.NotEqual("Tombstone", ((IObject)embedded).Type?.FirstOrDefault());
        }

        // (b) the Follow edge (bob -> alice) survives: bob still "follows" the deleted actor, and
        //     alice still has a follower.
        Assert.True(await p.Follows.IsFollowingAsync(bob, alice));
        var aliceFollowers = await p.Follows.GetFollowersAsync(alice);
        Assert.Contains(bob, aliceFollowers);

        // (c) the CommunityMember edge (alice in the community) is FILTERED from the member list
        //     (139.3-F2): a deleted local actor no longer surfaces as a community member, even though
        //     the underlying edge row still exists (the read path applies the deleted-actor filter).
        var members = await p.Communities.GetMembersAsync(community);
        Assert.DoesNotContain(alice, members);

        // (d) the Like edge (bob -> note1) survives.
        Assert.True(await p.Likes.HasLikedAsync(bob, note1));
        var likers = await p.Likes.GetLikersAsync(note1);
        Assert.Contains(bob, likers);
    }
}
