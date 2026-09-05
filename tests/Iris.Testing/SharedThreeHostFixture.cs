using Iris.Server.InMemory;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace Iris.Testing;

/// <summary>
/// A per-xunit-collection fixture that builds three in-process <see cref="TestServer"/> hosts ONCE and
/// shares them across every test method in the collection (29.3 follow-up, three-instance federation
/// classes). Mirrors <see cref="SharedTwoHostFixture"/> but for a triple of hosts (A, B, C) where the
/// delivery/fetcher wiring is topology-specific (set by the derived fixture).
/// </summary>
/// <remarks>
/// The three hosts' persistence stores are seeded once (via the <see cref="ActivityPubHostOptions"/>
/// passed to the constructor) and then shared. A mutating test class calls <see cref="Reset"/> before
/// each method (via <c>IAsyncLifetime.InitializeAsync</c>) to clear all three persistence stores + all
/// three collection-page response caches, then re-seeds. The key stores are left intact (the hosts'
/// signing infrastructure depends on them), so <see cref="TestSeeder.SeedPersonWithExistingKey"/> /
/// <see cref="TestSeeder.SeedCommunityWithExistingKey"/> can restore actors/communities with the same
/// key instances the fetchers/clients hold.
/// </remarks>
public class SharedThreeHostFixture : IDisposable
{
    private readonly TestServer _serverA;
    private readonly TestServer _serverB;
    private readonly TestServer _serverC;
    private readonly LocalCollectionPageCache? _collectionCacheA;
    private readonly LocalCollectionPageCache? _collectionCacheB;
    private readonly LocalCollectionPageCache? _collectionCacheC;

    /// <summary>The shared host A, built once for the collection.</summary>
    public TestServer ServerA => _serverA;

    /// <summary>The shared host B, built once for the collection.</summary>
    public TestServer ServerB => _serverB;

    /// <summary>The shared host C, built once for the collection.</summary>
    public TestServer ServerC => _serverC;

    /// <summary>Host A's <see cref="IPersistenceProvider"/> (its seeded in-memory store).</summary>
    public IPersistenceProvider PersistenceA { get; }

    /// <summary>Host B's <see cref="IPersistenceProvider"/> (its seeded in-memory store).</summary>
    public IPersistenceProvider PersistenceB { get; }

    /// <summary>Host C's <see cref="IPersistenceProvider"/> (its seeded in-memory store).</summary>
    public IPersistenceProvider PersistenceC { get; }

    /// <summary>
    /// Builds the three shared hosts from <paramref name="optionsA"/>, <paramref name="optionsB"/>, and
    /// <paramref name="optionsC"/> (their <see cref="ActivityPubHostOptions.Persistence"/> providers are
    /// seeded before the hosts bind them) and registers all three in the
    /// <see cref="SharedHostFixture.ServerRegistry"/> so a fetcher/delivery handler wired with
    /// <see cref="SharedHostFixture.ServerRefFor"/> can resolve any host at request time.
    /// </summary>
    public SharedThreeHostFixture(
        ActivityPubHostOptions optionsA, ActivityPubHostOptions optionsB, ActivityPubHostOptions optionsC)
    {
        _serverA = ActivityPubHostFactory.Create(optionsA);
        _serverB = ActivityPubHostFactory.Create(optionsB);
        _serverC = ActivityPubHostFactory.Create(optionsC);
        PersistenceA = optionsA.Persistence;
        PersistenceB = optionsB.Persistence;
        PersistenceC = optionsC.Persistence;
        SharedHostFixture.ServerRegistry[PersistenceA] = _serverA;
        SharedHostFixture.ServerRegistry[PersistenceB] = _serverB;
        SharedHostFixture.ServerRegistry[PersistenceC] = _serverC;
        _collectionCacheA = _serverA.Services.GetService<LocalCollectionPageCache>();
        _collectionCacheB = _serverB.Services.GetService<LocalCollectionPageCache>();
        _collectionCacheC = _serverC.Services.GetService<LocalCollectionPageCache>();
    }

    /// <summary>
    /// Builds the three shared hosts from a tuple of options (convenience overload for derived fixtures
    /// that build all three options in a single method so they can share persistence instances and
    /// cross-reference each other's servers via <see cref="SharedHostFixture.ServerRefFor"/>).
    /// </summary>
    protected SharedThreeHostFixture(
        (ActivityPubHostOptions A, ActivityPubHostOptions B, ActivityPubHostOptions C) options)
        : this(options.A, options.B, options.C)
    {
    }

    /// <summary>
    /// Clears all three hosts' persisted data (all in-memory stores) and their local collection-page
    /// response caches, so a mutating test class can start each method from a clean slate. The key
    /// stores are left intact (the hosts' signing infrastructure depends on them). After calling this,
    /// re-seed via <see cref="IPersistenceProvider"/> to restore the test's baseline state.
    /// </summary>
    public void Reset()
    {
        if (PersistenceA is InMemoryPersistenceProvider inMemoryA)
        {
            inMemoryA.Reset();
        }

        if (PersistenceB is InMemoryPersistenceProvider inMemoryB)
        {
            inMemoryB.Reset();
        }

        if (PersistenceC is InMemoryPersistenceProvider inMemoryC)
        {
            inMemoryC.Reset();
        }

        _collectionCacheA?.Clear();
        _collectionCacheB?.Clear();
        _collectionCacheC?.Clear();
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        SharedHostFixture.ServerRegistry.TryRemove(PersistenceA, out _);
        SharedHostFixture.ServerRegistry.TryRemove(PersistenceB, out _);
        SharedHostFixture.ServerRegistry.TryRemove(PersistenceC, out _);
        _serverA.Dispose();
        _serverB.Dispose();
        _serverC.Dispose();
    }
}
