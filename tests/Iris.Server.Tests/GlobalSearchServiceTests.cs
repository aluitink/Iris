using Iris.Core;
using Iris.Server.InMemory;
using KristofferStrube.ActivityStreams;

namespace Iris.Server.Tests;

/// <summary>
/// Phase 12 Slice 12.11 unit tests for <see cref="GlobalSearchService"/> (F-13 global search): the
/// search logic in isolation — a case-insensitive substring match over the instance's local actors
/// (the directory) and stored content objects, with tombstones and content-pass actors excluded.
/// </summary>
/// <remarks>
/// These tests drive the service directly (no HTTP): they seed an <see cref="InMemoryPersistenceProvider"/>
/// with actors and content objects (including a <see cref="Tombstone"/> and an object that is itself an
/// <see cref="Actor"/>) and assert the service's matching, ordering, and exclusion rules — the edge cases
/// the integration test (which seeds only persons + notes) does not cover.
/// </remarks>
public sealed class GlobalSearchServiceTests
{
    private const string AHost = "a.domain.local";

    // --- Empty query returns everything (actors first, then content, IRI-sorted) ----------

    [Fact]
    public async Task Search_EmptyQuery_ReturnsAllActorsThenContent_SortedByIri()
    {
        var persistence = new InMemoryPersistenceProvider();
        await PutActorAsync(persistence, "zebra");
        await PutActorAsync(persistence, "alpha");
        await PutNoteAsync(persistence, "n2", "second note");
        await PutNoteAsync(persistence, "n1", "first note");
        await PutTombstoneAsync(persistence, "tomb");
        // A stored object that is itself an actor: excluded from the content pass (matched by the
        // actor pass, not duplicated).
        await PutActorAsObjectAsync(persistence, "actorobj");

        var service = new GlobalSearchService(persistence);
        var results = await service.SearchAsync("");

        // Actors (from the actor store, IRI-sorted: alpha, zebra) then content notes (IRI-sorted: n1,
        // n2). The Tombstone is excluded; actorobj (an Actor stored in the object store) is excluded
        // from the content pass (it is an actor, matched only by the actor pass — which reads the actor
        // store, where actorobj does not live).
        Assert.Equal(
            [
                $"https://{AHost}/ap/v1/u/alpha",
                $"https://{AHost}/ap/v1/u/zebra",
                $"https://{AHost}/ap/v1/u/alice/notes/n1",
                $"https://{AHost}/ap/v1/u/alice/notes/n2",
            ],
            results.Select(ToId).ToArray());
    }

    // --- Matching is a case-insensitive substring over the relevant fields ----------------

    [Fact]
    public async Task Search_MatchesActorName_PreferenceUsernameAndIri()
    {
        var persistence = new InMemoryPersistenceProvider();
        await PutActorAsync(persistence, "alice");
        await PutActorAsync(persistence, "bob");

        var service = new GlobalSearchService(persistence);

        // "ALIC" matches alice (case-insensitive substring of the handle/name).
        var byName = (await service.SearchAsync("ALIC")).Select(ToId).ToArray();
        Assert.Equal($"https://{AHost}/ap/v1/u/alice", Assert.Single(byName));

        // The IRI itself is also a searchable surface: "domain.local" matches every local actor.
        var byIri = (await service.SearchAsync("domain.local")).Select(ToId).ToArray();
        Assert.Equal(2, byIri.Length);
    }

    [Fact]
    public async Task Search_MatchesContentByNameAndContent()
    {
        var persistence = new InMemoryPersistenceProvider();
        // A note whose NAME matches ("My GARDEN") but content does not; one whose CONTENT matches
        // ("garden post") but name does not.
        await persistence.Objects.PutObjectAsync(new Note
        {
            Id = $"https://{AHost}/ap/v1/u/a/notes/n-name",
            Name = ["My GARDEN"],
            Content = ["no keyword here"],
        });
        await persistence.Objects.PutObjectAsync(new Note
        {
            Id = $"https://{AHost}/ap/v1/u/a/notes/n-content",
            Name = ["unremarkable"],
            Content = ["a garden post"],
        });

        var service = new GlobalSearchService(persistence);
        var results = (await service.SearchAsync("GARDEN")).Select(ToId).ToArray();

        Assert.Equal(
            [
                $"https://{AHost}/ap/v1/u/a/notes/n-content",
                $"https://{AHost}/ap/v1/u/a/notes/n-name",
            ],
            results);
    }

    // --- A no-match query returns nothing -------------------------------------------------

    [Fact]
    public async Task Search_NoMatch_ReturnsEmpty()
    {
        var persistence = new InMemoryPersistenceProvider();
        await PutActorAsync(persistence, "alice");
        await PutNoteAsync(persistence, "n1", "hello world");

        var service = new GlobalSearchService(persistence);
        Assert.Empty(await service.SearchAsync("zzz"));
        // Whitespace-only is treated as "no query" (matches everything) — a distinct code path from a
        // present-but-mismatched query.
        Assert.Equal(2, (await service.SearchAsync("   ")).Count);
    }

    // --- Tombstones and content-pass actors are excluded ----------------------------------

    [Fact]
    public async Task Search_ExcludesTombstonesAndContentPassActors()
    {
        var persistence = new InMemoryPersistenceProvider();
        await PutActorAsync(persistence, "alice");
        await PutTombstoneAsync(persistence, "tomb");
        await PutActorAsObjectAsync(persistence, "actorobj");

        var service = new GlobalSearchService(persistence);
        var results = (await service.SearchAsync("")).Select(ToId).ToArray();

        // The tombstone is never a content match, and actorobj (an Actor in the object store) is
        // excluded from the content pass (it is an actor, not duplicated as content). The actor store
        // holds only alice, so the empty query yields alice alone.
        Assert.Equal([$"https://{AHost}/ap/v1/u/alice"], results);
    }

    // --- Helpers --------------------------------------------------------------------------

    private static string ToId(IObjectOrLink o) => o is IObject obj ? obj.Id ?? string.Empty : string.Empty;

    private static Task PutActorAsync(InMemoryPersistenceProvider persistence, string handle)
    {
        var iri = new Iri($"https://{AHost}/ap/v1/u/{handle}");
        return persistence.ActorStore.PutActorAsync(new Person
        {
            Id = iri.Value,
            PreferredUsername = handle,
            Name = [handle],
        });
    }

    private static Task PutNoteAsync(InMemoryPersistenceProvider persistence, string noteId, string content)
        => persistence.Objects.PutObjectAsync(new Note
        {
            Id = $"https://{AHost}/ap/v1/u/alice/notes/{noteId}",
            Content = [content],
        });

    private static Task PutTombstoneAsync(InMemoryPersistenceProvider persistence, string noteId)
        => persistence.Objects.PutObjectAsync(new Tombstone
        {
            Id = $"https://{AHost}/ap/v1/u/alice/notes/{noteId}",
        });

    /// <summary>
    /// Stores an <see cref="Actor"/> in the <em>object</em> store (not the actor store) — the case the
    /// content pass must exclude (an object that is an actor is matched by the actor pass, not
    /// duplicated as content).
    /// </summary>
    private static Task PutActorAsObjectAsync(InMemoryPersistenceProvider persistence, string handle)
    {
        var iri = new Iri($"https://{AHost}/ap/v1/u/{handle}");
        return persistence.Objects.PutObjectAsync(new Person
        {
            Id = iri.Value,
            PreferredUsername = handle,
            Name = [handle],
        });
    }
}
