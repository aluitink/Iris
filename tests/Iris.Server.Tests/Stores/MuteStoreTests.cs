using Iris.Core;
using Iris.Server.InMemory;

namespace Iris.Server.Tests.Stores;

/// <summary>
/// Unit tests for the F-07 mute edge in <see cref="InMemoryModerationStore"/> (the local moderation
/// store): recording a mute, querying the mutes collection and the mute predicate, idempotent re-mute,
/// removal, and removal of a non-existent mute. A mute is a local, directed edge (muter → muted); it is
/// forward-only (an actor's <c>mutes</c> collection) with no inverse query (no delivery-suppression use).
/// The block/flag store behavior is covered elsewhere; these tests pin the mute-specific methods.
/// </summary>
public sealed class MuteStoreTests
{
    private static readonly Iri Alice = new("https://b.domain.local/ap/v1/u/alice");
    private static readonly Iri Bob = new("https://b.domain.local/ap/v1/u/bob");
    private static readonly Iri Carol = new("https://b.domain.local/ap/v1/u/carol");

    [Fact]
    public async Task RecordMute_AppearsInMutesCollectionAndPredicate()
    {
        var store = new InMemoryModerationStore();
        await store.RecordMuteAsync(Alice, Bob);

        // The mute edge is recorded: bob is in alice's mutes, and the predicate confirms it.
        Assert.Contains(Bob, await store.GetMutesAsync(Alice));
        Assert.True(await store.IsMutedAsync(Alice, Bob));
    }

    [Fact]
    public async Task Mute_IsDirected_NotMutual()
    {
        var store = new InMemoryModerationStore();
        await store.RecordMuteAsync(Alice, Bob);

        // The mute is directed (alice → bob): bob does not know he is muted (no inverse mute query), and
        // the reverse edge (bob → alice) is not recorded.
        Assert.Empty(await store.GetMutesAsync(Bob));
        Assert.False(await store.IsMutedAsync(Bob, Alice));
    }

    [Fact]
    public async Task RecordMute_Idempotent()
    {
        var store = new InMemoryModerationStore();
        await store.RecordMuteAsync(Alice, Bob);
        await store.RecordMuteAsync(Alice, Bob);

        // Re-muting is a no-op: bob appears exactly once in alice's mutes.
        var mutes = await store.GetMutesAsync(Alice);
        Assert.Single(mutes);
        Assert.Equal(Bob, mutes[0]);
    }

    [Fact]
    public async Task GetMutes_SortedByIri()
    {
        var store = new InMemoryModerationStore();
        // Record in non-sorted order; the collection is IRI-sorted for a stable paged output.
        await store.RecordMuteAsync(Alice, Carol);
        await store.RecordMuteAsync(Alice, Bob);

        var mutes = await store.GetMutesAsync(Alice);
        Assert.Equal([Bob, Carol], mutes);
    }

    [Fact]
    public async Task RemoveMute_RemovesEdge()
    {
        var store = new InMemoryModerationStore();
        await store.RecordMuteAsync(Alice, Bob);
        await store.RecordMuteAsync(Alice, Carol);

        // Removing one mute leaves the other; the predicate reflects the removal.
        Assert.True(await store.RemoveMuteAsync(Alice, Bob));
        Assert.False(await store.IsMutedAsync(Alice, Bob));
        Assert.True(await store.IsMutedAsync(Alice, Carol));
        var mutes = await store.GetMutesAsync(Alice);
        Assert.Single(mutes);
        Assert.Equal(Carol, mutes[0]);
    }

    [Fact]
    public async Task RemoveMute_NonExistent_ReturnsFalse()
    {
        var store = new InMemoryModerationStore();

        // Un-muting an actor that was never muted is a no-op (returns false; no edge is created).
        Assert.False(await store.RemoveMuteAsync(Alice, Bob));
        Assert.False(await store.IsMutedAsync(Alice, Bob));
        Assert.Empty(await store.GetMutesAsync(Alice));
    }
}
