using Iris.Core;
using Iris.Server.InMemory;

namespace Iris.Server.Tests;

/// <summary>
/// Unit tests for the F-06 relay-subscription edge in <see cref="InMemoryRelayStore"/>: recording a
/// subscription, querying the relays (<c>star</c>) collection and the subscription predicate, idempotent
/// re-subscribe, removal, and removal of a non-existent subscription. A relay subscription is a local,
/// directed edge (actor → relay); it is forward-only (an actor's <c>relays</c> / <c>star</c> collection)
/// with no inverse query. These tests pin the relay-specific methods.
/// </summary>
public sealed class RelayStoreTests
{
    private static readonly Iri Alice = new("https://b.domain.local/ap/v1/u/alice");
    private static readonly Iri RelayOne = new("https://relay1.example.com");
    private static readonly Iri RelayTwo = new("https://relay2.example.com");

    [Fact]
    public async Task RecordRelay_AppearsInRelaysCollectionAndPredicate()
    {
        var store = new InMemoryRelayStore();
        await store.RecordRelayAsync(Alice, RelayOne);

        // The subscription is recorded: the relay is in alice's relays, and the predicate confirms it.
        Assert.Contains(RelayOne, await store.GetRelaysAsync(Alice));
        Assert.True(await store.IsRelayAsync(Alice, RelayOne));
    }

    [Fact]
    public async Task RelaySubscription_IsDirected_NotMutual()
    {
        var store = new InMemoryRelayStore();
        await store.RecordRelayAsync(Alice, RelayOne);

        // The subscription is directed (alice → relay): the reverse edge (relay → alice) is not recorded
        // (a relay is not an actor that subscribes to another actor).
        Assert.Empty(await store.GetRelaysAsync(RelayOne));
        Assert.False(await store.IsRelayAsync(RelayOne, Alice));
    }

    [Fact]
    public async Task RecordRelay_Idempotent()
    {
        var store = new InMemoryRelayStore();
        await store.RecordRelayAsync(Alice, RelayOne);
        await store.RecordRelayAsync(Alice, RelayOne);

        // Re-subscribing is a no-op: the relay appears exactly once in alice's relays.
        var relays = await store.GetRelaysAsync(Alice);
        Assert.Single(relays);
        Assert.Equal(RelayOne, relays[0]);
    }

    [Fact]
    public async Task GetRelays_SortedByIri()
    {
        var store = new InMemoryRelayStore();
        // Record in non-sorted order; the collection is IRI-sorted for a stable paged output.
        await store.RecordRelayAsync(Alice, RelayTwo);
        await store.RecordRelayAsync(Alice, RelayOne);

        var relays = await store.GetRelaysAsync(Alice);
        Assert.Equal([RelayOne, RelayTwo], relays);
    }

    [Fact]
    public async Task RemoveRelay_RemovesEdge()
    {
        var store = new InMemoryRelayStore();
        await store.RecordRelayAsync(Alice, RelayOne);
        await store.RecordRelayAsync(Alice, RelayTwo);

        // Removing one subscription leaves the other; the predicate reflects the removal.
        Assert.True(await store.RemoveRelayAsync(Alice, RelayOne));
        Assert.False(await store.IsRelayAsync(Alice, RelayOne));
        Assert.True(await store.IsRelayAsync(Alice, RelayTwo));
        var relays = await store.GetRelaysAsync(Alice);
        Assert.Single(relays);
        Assert.Equal(RelayTwo, relays[0]);
    }

    [Fact]
    public async Task RemoveRelay_NonExistent_ReturnsFalse()
    {
        var store = new InMemoryRelayStore();

        // Un-subscribing from a relay that was never subscribed to is a no-op (returns false; no edge is
        // created).
        Assert.False(await store.RemoveRelayAsync(Alice, RelayOne));
        Assert.False(await store.IsRelayAsync(Alice, RelayOne));
        Assert.Empty(await store.GetRelaysAsync(Alice));
    }
}
