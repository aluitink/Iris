using Iris.Core;
using Iris.Server.InMemory;
using KristofferStrube.ActivityStreams;

namespace Iris.Server.Tests.Services;

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

    // --- Type filter restricts to a single ActivityStreams type (31.4 directory) ----------

    [Fact]
    public async Task Search_TypeActor_ReturnsOnlyActors_NoContent()
    {
        var persistence = new InMemoryPersistenceProvider();
        await PutActorAsync(persistence, "alice");
        await PutActorAsync(persistence, "bob");
        await PutNoteAsync(persistence, "n1", "hello world");

        var service = new GlobalSearchService(persistence);

        // type="Actor" → only the actor pass runs; the content pass is skipped, so the notes are
        // excluded even though a no-type search would return them.
        var byActor = (await service.SearchAsync("Alice", type: "Actor")).Select(ToId).ToArray();
        Assert.Equal($"https://{AHost}/ap/v1/u/alice", Assert.Single(byActor));

        // An empty query with type="Actor" lists all actors (the directory), still no content.
        var allActors = (await service.SearchAsync(null, type: "Actor")).Select(ToId).ToArray();
        Assert.Equal(
            [
                $"https://{AHost}/ap/v1/u/alice",
                $"https://{AHost}/ap/v1/u/bob",
            ],
            allActors);

        // The type filter is case-insensitive.
        var lower = (await service.SearchAsync(null, type: "actor")).Select(ToId).ToArray();
        Assert.Equal(allActors, lower);
    }

    [Fact]
    public async Task Search_TypeNote_ReturnsOnlyMatchingContent()
    {
        var persistence = new InMemoryPersistenceProvider();
        await PutActorAsync(persistence, "alice");
        await PutNoteAsync(persistence, "n1", "garden post");

        var service = new GlobalSearchService(persistence);

        // type="Note" → the actor pass is skipped and the content pass filters to Note items, so the
        // actor is excluded and the note is returned (content-only surface).
        var byNote = (await service.SearchAsync("garden", type: "Note")).Select(ToId).ToArray();
        Assert.Equal($"https://{AHost}/ap/v1/u/alice/notes/n1", Assert.Single(byNote));
    }

    // --- localOnly restricts the actor pass to this instance's own actors (92.1 directory) ---

    [Fact]
    public async Task Search_LocalOnly_ExcludesCachedRemoteActors()
    {
        var persistence = new InMemoryPersistenceProvider();
        // A local actor (carries a handle) and a cached remote actor (no preferredUsername).
        await PutActorAsync(persistence, "alice");
        await PutRemoteActorAsync(persistence, "remote", "mastodon.social");

        var service = new GlobalSearchService(persistence);

        // "All known actors" (default) returns both the local actor and the cached remote actor.
        var all = (await service.SearchAsync(null, type: "Actor")).Select(ToId).ToArray();
        Assert.Equal(2, all.Length);
        Assert.Contains($"https://{AHost}/ap/v1/u/alice", all);
        Assert.Contains($"https://mastodon.social/users/remote", all);

        // "This instance" (localOnly) drops the cached remote actor but keeps the local one.
        var local = (await service.SearchAsync(null, type: "Actor", localOnly: true)).Select(ToId).ToArray();
        Assert.Equal($"https://{AHost}/ap/v1/u/alice", Assert.Single(local));
    }

    [Fact]
    public async Task Search_LocalOnly_AppliesToQueryMatches()
    {
        var persistence = new InMemoryPersistenceProvider();
        // Both a local and a remote actor whose name contains "gardener".
        var localIri = new Iri($"https://{AHost}/ap/v1/u/gardener");
        await persistence.ActorStore.PutActorAsync(new Person
        {
            Id = localIri.Value,
            PreferredUsername = "gardener",
            Name = ["Gardener"],
        });
        var remoteIri = new Iri($"https://remote.example/users/gardener");
        await persistence.ActorStore.PutActorAsync(new Person
        {
            Id = remoteIri.Value,
            Name = ["Gardener"],
        });

        var service = new GlobalSearchService(persistence);

        // A query match without localOnly finds both; with localOnly it finds only the local one.
        var all = (await service.SearchAsync("gardener", type: "Actor")).Select(ToId).ToArray();
        Assert.Equal(2, all.Length);

        var local = (await service.SearchAsync("gardener", type: "Actor", localOnly: true)).Select(ToId).ToArray();
        Assert.Equal(localIri.Value, Assert.Single(local));
    }

    [Fact]
    public async Task Search_LocalOnly_DoesNotAffectContent()
    {
        var persistence = new InMemoryPersistenceProvider();
        await PutActorAsync(persistence, "alice");
        await PutNoteAsync(persistence, "n1", "hello world");

        var service = new GlobalSearchService(persistence);

        // Content is the instance's stored content regardless of localOnly — a localOnly search with a
        // no-type filter still returns the note (content is never "remote" in this model).
        var withLocal = (await service.SearchAsync("hello", localOnly: true)).Select(ToId).ToArray();
        Assert.Contains($"https://{AHost}/ap/v1/u/alice/notes/n1", withLocal);
    }

    // --- IRI-prefix-based local/remote discrimination (Phase 105 directory) -----------------

    [Fact]
    public async Task Search_LocalOnly_WithInstanceBase_KeepsRemoteActorsWithPreferredUsername()
    {
        var persistence = new InMemoryPersistenceProvider();
        // A local actor and a remote actor that DOES carry a preferredUsername (a remote Iris actor from
        // another instance, or a Mastodon user). Both are legitimate directory entries in the "All known"
        // scope: the remote actor's IRI is on a different origin, so it is a cached peer, not a local
        // actor with a stale IRI (S5). The previous logic dropped any actor with a preferredUsername whose
        // IRI was not on the local instance base, which incorrectly excluded remote Iris actors.
        await PutActorAsync(persistence, "alice");
        var foreignIri = new Iri($"https://mastodon.social/users/remote_user");
        await persistence.ActorStore.PutActorAsync(new Person
        {
            Id = foreignIri.Value,
            PreferredUsername = "remote_user",
            Name = ["Remote User"],
        });

        var instanceBase = new Iri($"https://{AHost}");
        var service = new GlobalSearchService(persistence, instanceBase);

        // "All known actors" (the mixed path) keeps both the local actor and the remote actor (a cached
        // peer, even though it carries a preferredUsername).
        var all = (await service.SearchAsync(null, type: "Actor")).Select(ToId).OrderBy(i => i).ToArray();
        Assert.Equal(
            [
                $"https://{AHost}/ap/v1/u/alice",
                foreignIri.Value,
            ],
            all);

        // "This instance" (localOnly + instance base) excludes the remote actor (it is not on this
        // instance) but keeps the local one.
        var local = (await service.SearchAsync(null, type: "Actor", localOnly: true)).Select(ToId).ToArray();
        Assert.Equal($"https://{AHost}/ap/v1/u/alice", Assert.Single(local));
    }

    [Fact]
    public async Task Search_LocalOnly_WithInstanceBase_IncludesLocalActors()
    {
        var persistence = new InMemoryPersistenceProvider();
        await PutActorAsync(persistence, "alice");
        await PutActorAsync(persistence, "bob");
        await PutRemoteActorAsync(persistence, "ext", "other.example");

        var instanceBase = new Iri($"https://{AHost}");
        var service = new GlobalSearchService(persistence, instanceBase);

        var local = (await service.SearchAsync(null, type: "Actor", localOnly: true)).Select(ToId).ToArray();
        Assert.Equal(
            [
                $"https://{AHost}/ap/v1/u/alice",
                $"https://{AHost}/ap/v1/u/bob",
            ],
            local);
    }

    [Fact]
    public async Task Search_LocalOnly_WithInstanceBase_FallsBackToStoreHeuristic_WhenBaseIsNull()
    {
        var persistence = new InMemoryPersistenceProvider();
        await PutActorAsync(persistence, "alice");
        await PutRemoteActorAsync(persistence, "remote", "mastodon.social");

        // No instance base: falls back to the store's preferredUsername heuristic.
        var service = new GlobalSearchService(persistence);

        var local = (await service.SearchAsync(null, type: "Actor", localOnly: true)).Select(ToId).ToArray();
        Assert.Equal($"https://{AHost}/ap/v1/u/alice", Assert.Single(local));
    }

    [Fact]
    public async Task Search_LocalOnly_WithInstanceBase_QueryFilter()
    {
        var persistence = new InMemoryPersistenceProvider();
        // A local actor matching the query, and a remote actor with the same name + a
        // preferredUsername (which the old heuristic would include).
        await PutActorAsync(persistence, "gardener");
        var remoteIri = new Iri("https://remote.example/users/gardener");
        await persistence.ActorStore.PutActorAsync(new Person
        {
            Id = remoteIri.Value,
            PreferredUsername = "gardener",
            Name = ["Gardener"],
        });

        var instanceBase = new Iri($"https://{AHost}");
        var service = new GlobalSearchService(persistence, instanceBase);

        // Without localOnly (the mixed path): the foreign-origin preferredUsername actor is not
        // canonical for this instance, so only the local gardener surfaces (S5 rule).
        var all = (await service.SearchAsync("gardener", type: "Actor")).Select(ToId).ToArray();
        Assert.Equal($"https://{AHost}/ap/v1/u/gardener", Assert.Single(all));

        // With localOnly + instance base: only the local actor (the same result).
        var local = (await service.SearchAsync("gardener", type: "Actor", localOnly: true)).Select(ToId).ToArray();
        Assert.Equal($"https://{AHost}/ap/v1/u/gardener", Assert.Single(local));
    }

    // --- S5: a stale local actor on a foreign base (localhost) is dropped from the mixed path ---

    [Fact]
    public async Task Search_MixedPath_WithInstanceBase_DropsStaleLocalActorOnForeignBase()
    {
        var persistence = new InMemoryPersistenceProvider();
        // The canonical local alice (on the instance's public base) and a STALE orphaned alice
        // persisted under a dev base — the SAME public host, but the dev default (http://localhost:8088)
        // instead of the advertised https base. This is the exact S5 shape: a row written while the
        // container booted with Iris:AdvertiseBase unset (dev default) that is never re-canonicalized.
        // Both carry a preferredUsername (the same local handle); only the canonical one begins with the
        // instance base.
        await PutActorAsync(persistence, "alice");
        var staleIri = new Iri($"http://localhost:8088/ap/v1/u/alice");
        await persistence.ActorStore.PutActorAsync(new Person
        {
            Id = staleIri.Value,
            PreferredUsername = "alice",
            Name = ["alice"],
        });

        var instanceBase = new Iri($"https://{AHost}");
        var service = new GlobalSearchService(persistence, instanceBase);

        // The Search page (localOnly=false) must surface ONLY the canonical public-base alice — the
        // stale dev-base ghost is dropped (S5). Before the fix both surfaced (two "alice" cards).
        var mixed = (await service.SearchAsync("alice", type: "Actor")).Select(ToId).ToArray();
        Assert.Equal($"https://{AHost}/ap/v1/u/alice", Assert.Single(mixed));

        // The localOnly path is unaffected (it already filtered to the instance base).
        var local = (await service.SearchAsync("alice", type: "Actor", localOnly: true)).Select(ToId).ToArray();
        Assert.Equal($"https://{AHost}/ap/v1/u/alice", Assert.Single(local));
    }

    [Fact]
    public async Task Search_MixedPath_WithInstanceBase_KeepsGenuineRemoteActor()
    {
        var persistence = new InMemoryPersistenceProvider();
        await PutActorAsync(persistence, "alice");
        // A genuine REMOTE actor (a different origin, no local handle) — a legitimate cached remote
        // actor that must still surface in the mixed path (the S5 rule only drops LOCAL actors whose IRI
        // is not canonical for this instance).
        var remoteIri = new Iri("https://mastodon.social/users/remote_user");
        await persistence.ActorStore.PutActorAsync(new Person
        {
            Id = remoteIri.Value,
            Name = ["Remote User"],
        });

        var instanceBase = new Iri($"https://{AHost}");
        var service = new GlobalSearchService(persistence, instanceBase);

        var mixed = (await service.SearchAsync(null, type: "Actor")).Select(ToId).ToArray();
        Assert.Equal(2, mixed.Length);
        Assert.Contains($"https://{AHost}/ap/v1/u/alice", mixed);
        Assert.Contains(remoteIri.Value, mixed);
    }

    [Fact]
    public async Task Search_MixedPath_WithoutInstanceBase_KeepsStaleLocalActor_FallsBackToStoreHeuristic()
    {
        var persistence = new InMemoryPersistenceProvider();
        await PutActorAsync(persistence, "alice");
        var staleIri = new Iri("http://localhost:8088/ap/v1/u/alice");
        await persistence.ActorStore.PutActorAsync(new Person
        {
            Id = staleIri.Value,
            PreferredUsername = "alice",
            Name = ["alice"],
        });

        // No instance base: there is no origin to compare against, so the stale local row is kept
        // (the service cannot tell it is stale) — the store's heuristic is the only filter available.
        var service = new GlobalSearchService(persistence);

        var mixed = (await service.SearchAsync("alice", type: "Actor")).Select(ToId).ToArray();
        Assert.Equal(2, mixed.Length);
        Assert.Contains($"https://{AHost}/ap/v1/u/alice", mixed);
        Assert.Contains(staleIri.Value, mixed);
    }

    // --- S30 (A8.2): the "All known" communities facet surfaces cached remote communities ----

    [Fact]
    public async Task Search_AllKnownCommunities_SurfacesCachedRemoteGroup_NotLocal()
    {
        var persistence = new InMemoryPersistenceProvider();
        await PutActorAsync(persistence, "alice");
        // A LOCAL community (provisioned, instance-base IRI) lives in the community store.
        await PutCommunityAsync(persistence, "local-comm", $"https://{AHost}/ap/v1/c/local-comm", local: true);
        // A CACHED REMOTE community (Group) persisted to the community store during federation.
        await PutCommunityAsync(persistence, "ii-comm", "https://remote.example/ap/v1/c/ii-comm", local: false);

        var service = new GlobalSearchService(persistence, new Iri($"https://{AHost}"));

        // The Directory "All known" communities tab (type=Actor, localOnly=false) lists the actor + the
        // cached REMOTE community — but NOT the local community (it is "All on this instance", not
        // "all known"). Before the S30 fix the remote group was absent → the tab was empty.
        var allKnown = (await service.SearchAsync(null, type: "Actor")).Select(ToId).ToArray();
        Assert.Equal(2, allKnown.Length);
        Assert.Contains($"https://{AHost}/ap/v1/u/alice", allKnown);
        Assert.Contains("https://remote.example/ap/v1/c/ii-comm", allKnown);
        Assert.DoesNotContain($"https://{AHost}/ap/v1/c/local-comm", allKnown);

        // "This instance" (localOnly=true) lists only the local actors — the cached remote community and
        // the local community are both excluded (the community-store merge is skipped for localOnly).
        var local = (await service.SearchAsync(null, type: "Actor", localOnly: true)).Select(ToId).ToArray();
        Assert.Equal($"https://{AHost}/ap/v1/u/alice", Assert.Single(local));
    }

    [Fact]
    public async Task Search_AllKnownCommunities_QueryMatchesRemoteCommunityByName()
    {
        var persistence = new InMemoryPersistenceProvider();
        await PutActorAsync(persistence, "alice");
        await PutCommunityAsync(persistence, "ii-comm", "https://remote.example/ap/v1/c/ii-comm", local: false);

        var service = new GlobalSearchService(persistence, new Iri($"https://{AHost}"));

        // A query matching the cached remote community's name/IRI surfaces it in the mixed path.
        var byName = (await service.SearchAsync("ii-comm", type: "Actor")).Select(ToId).ToArray();
        Assert.Contains("https://remote.example/ap/v1/c/ii-comm", byName);

        // A query matching nothing matches neither the actor nor the cached community.
        Assert.Empty(await service.SearchAsync("zzz-nonexistent", type: "Actor"));
    }

    [Fact]
    public async Task Search_AllKnownCommunities_NoDoubleCounting_WhenCommunityAlreadyInActorStore()
    {
        var persistence = new InMemoryPersistenceProvider();
        // A community stored in BOTH the actor store and the community store must surface exactly once.
        var communityIri = "https://remote.example/ap/v1/c/ii-comm";
        await persistence.ActorStore.PutActorAsync(new Group
        {
            Id = communityIri,
            Name = ["II Comm"],
        });
        await PutCommunityAsync(persistence, "ii-comm", communityIri, local: false);

        var service = new GlobalSearchService(persistence, new Iri($"https://{AHost}"));

        var all = (await service.SearchAsync(null, type: "Actor")).Select(ToId).ToArray();
        Assert.Equal(1, all.Count(i => i == communityIri));
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

    /// <summary>
    /// Stores a cached remote actor (an actor from another server that this instance cached) — it
    /// carries a remote IRI and <em>no</em> <c>preferredUsername</c> (a remote stand-in has no local
    /// handle), which is exactly what the <c>localOnly</c> filter excludes.
    /// </summary>
    private static Task PutRemoteActorAsync(InMemoryPersistenceProvider persistence, string name, string host)
    {
        var iri = new Iri($"https://{host}/users/{name}");
        return persistence.ActorStore.PutActorAsync(new Person
        {
            Id = iri.Value,
            Name = [name],
        });
    }

    /// <summary>
    /// Stores a community (<see cref="Group"/>) in the community store — the durable store a local
    /// community is provisioned into and a CACHED remote community is persisted into by
    /// <c>RemoteCommunityPersister</c>. A Group is not an <see cref="Actor"/>, so it is invisible to the
    /// actor-store search pass; the S30 "All known" communities facet is what surfaces it.
    /// </summary>
    private static Task PutCommunityAsync(InMemoryPersistenceProvider persistence, string name, string iri, bool local)
    {
        var group = new Group
        {
            Id = iri,
            Name = [name],
        };
        if (local)
        {
            group.PreferredUsername = name;
        }

        return persistence.Communities.PutCommunityAsync(group);
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
