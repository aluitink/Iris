using Iris.Server.InMemory;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace Iris.Testing;

/// <summary>
/// A per-xunit-collection fixture that builds two in-process <see cref="TestServer"/> hosts ONCE and
/// shares them across every test method in the collection (29.3 follow-up, two-instance federation
/// classes). Mirrors <see cref="SharedHostFixture"/> but for a pair of cross-wired hosts (A delivers
/// to B, B delivers to A; each fetcher routes by host IRI).
/// </summary>
/// <remarks>
/// The two hosts' persistence stores are seeded once (via the <see cref="ActivityPubHostOptions"/> passed
/// to the constructor) and then shared. A mutating test class calls <see cref="Reset"/> before each
/// method (via <c>IAsyncLifetime.InitializeAsync</c>) to clear both persistence stores + both
/// collection-page response caches, then re-seeds. The key stores are left intact (the hosts' signing
/// infrastructure depends on them), so <see cref="TestSeeder.SeedPersonWithExistingKey"/> /
/// <see cref="TestSeeder.SeedCommunityWithExistingKey"/> can restore actors/communities with the same
/// key instances the fetchers/clients hold.
/// </remarks>
public class SharedTwoHostFixture : IDisposable
{
    private readonly TestServer _serverA;
    private readonly TestServer _serverB;
    private readonly LocalCollectionPageCache? _collectionCacheA;
    private readonly LocalCollectionPageCache? _collectionCacheB;

    /// <summary>The shared host A, built once for the collection.</summary>
    public TestServer ServerA => _serverA;

    /// <summary>The shared host B, built once for the collection.</summary>
    public TestServer ServerB => _serverB;

    /// <summary>Host A's <see cref="IPersistenceProvider"/> (its seeded in-memory store).</summary>
    public IPersistenceProvider PersistenceA { get; }

    /// <summary>Host B's <see cref="IPersistenceProvider"/> (its seeded in-memory store).</summary>
    public IPersistenceProvider PersistenceB { get; }

    /// <summary>
    /// Builds the two shared hosts from <paramref name="optionsA"/> and <paramref name="optionsB"/>
    /// (their <see cref="ActivityPubHostOptions.Persistence"/> providers are seeded before the hosts
    /// bind them) and registers both in the <see cref="SharedHostFixture.ServerRegistry"/> so a
    /// fetcher/delivery handler wired with <see cref="SharedHostFixture.ServerRefFor"/> can resolve the
    /// other host at request time.
    /// </summary>
    public SharedTwoHostFixture(ActivityPubHostOptions optionsA, ActivityPubHostOptions optionsB)
    {
        _serverA = ActivityPubHostFactory.Create(optionsA);
        _serverB = ActivityPubHostFactory.Create(optionsB);
        PersistenceA = optionsA.Persistence;
        PersistenceB = optionsB.Persistence;
        SharedHostFixture.ServerRegistry[PersistenceA] = _serverA;
        SharedHostFixture.ServerRegistry[PersistenceB] = _serverB;
        _collectionCacheA = _serverA.Services.GetService<LocalCollectionPageCache>();
        _collectionCacheB = _serverB.Services.GetService<LocalCollectionPageCache>();
    }

    /// <summary>
    /// Builds the two shared hosts from a tuple of options (convenience overload for derived fixtures
    /// that build both options in a single method so they can share persistence instances and
    /// cross-reference each other's servers via <see cref="SharedHostFixture.ServerRefFor"/>).
    /// </summary>
    protected SharedTwoHostFixture((ActivityPubHostOptions A, ActivityPubHostOptions B) options)
        : this(options.A, options.B)
    {
    }

    /// <summary>
    /// Clears both hosts' persisted data (all in-memory stores) and their local collection-page response
    /// caches, so a mutating test class can start each method from a clean slate. The key stores are left
    /// intact (the hosts' signing infrastructure depends on them). After calling this, re-seed via
    /// <see cref="IPersistenceProvider"/> to restore the test's baseline state.
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

        _collectionCacheA?.Clear();
        _collectionCacheB?.Clear();
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        SharedHostFixture.ServerRegistry.TryRemove(PersistenceA, out _);
        SharedHostFixture.ServerRegistry.TryRemove(PersistenceB, out _);
        _serverA.Dispose();
        _serverB.Dispose();
    }
}
