using System.Collections.Concurrent;
using Iris.Server.InMemory;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace Iris.Testing;

/// <summary>
/// A per-xunit-collection fixture that builds a single in-process <see cref="TestServer"/> host ONCE and
/// shares it across every test method in the collection. xunit constructs a fresh test-class instance per
/// method (and a class's methods run in its own collection), so a test that builds its host in the
/// constructor rebuilds the whole pipeline once per method. Classes that build an identical, read-only
/// host for every method can instead resolve this fixture and reuse the one host, amortizing construction
/// across the collection's methods (29.3).
/// </summary>
/// <remarks>
/// The host's persistence store is seeded once (via <see cref="ActivityPubHostOptions.Persistence"/>) and
/// then shared. A read-only/idempotent class can share the host as-is. A mutating class (one whose
/// methods write to persistence and assert on absolute counts) should call <see cref="Reset"/> before
/// each method (via <c>IAsyncLifetime.InitializeAsync</c>) to clear the persistence + response caches,
/// then re-seed, so each method starts from a clean, freshly-seeded state.
/// </remarks>
public class SharedHostFixture : IDisposable
{
    private readonly TestServer _server;
    private readonly LocalCollectionPageCache? _collectionCache;

    /// <summary>The shared in-process host, built once for the collection.</summary>
    public TestServer Server => _server;

    /// <summary>The host's <see cref="IPersistenceProvider"/> (its seeded in-memory store), exposed for
    /// test code that needs to read or (for a mutating class, after a per-method reset) write state.</summary>
    public IPersistenceProvider Persistence { get; }

    /// <summary>A static deferred-lookup registry that maps a persistence instance to its host server,
    /// so a fetcher/client handler factory wired during host construction (before the server exists) can
    /// resolve the server at request time (after the server exists). The <see cref="LazyHandler"/> defers
    /// <c>CreateHandler()</c> until a request, by which point the mapping is registered.</summary>
    internal static readonly ConcurrentDictionary<IPersistenceProvider, TestServer> ServerRegistry = new();

    /// <summary>Builds the shared host from <paramref name="options"/> (its <see cref="ActivityPubHostOptions.Persistence"/>
    /// is seeded before the host binds it) and registers the local key when requested.</summary>
    public SharedHostFixture(ActivityPubHostOptions options)
    {
        _server = ActivityPubHostFactory.Create(options);
        Persistence = options.Persistence;
        ServerRegistry[Persistence] = _server;
        _collectionCache = _server.Services.GetService<LocalCollectionPageCache>();
    }

    /// <summary>
    /// Clears the host's persisted data (all in-memory stores) and its local collection-page response
    /// cache, so a mutating test class can start each method from a clean slate. The key store is left
    /// intact (the host's signing infrastructure depends on it). After calling this, re-seed via
    /// <see cref="IPersistenceProvider"/> to restore the test's baseline state.
    /// </summary>
    public void Reset()
    {
        if (Persistence is InMemoryPersistenceProvider inMemory)
        {
            inMemory.Reset();
        }

        _collectionCache?.Clear();
    }

    /// <summary>
    /// Returns a deferred <c>Func&lt;TestServer&gt;</c> that resolves the host server for
    /// <paramref name="persistence"/> via <see cref="ServerRegistry"/>. Safe to use in a
    /// <see cref="LazyHandler"/> factory wired during host construction: the registry entry is added by
    /// the <see cref="SharedHostFixture"/> constructor (after <c>ActivityPubHostFactory.Create</c> returns),
    /// and the factory is only invoked during a request (after construction).
    /// </summary>
    public static Func<TestServer> ServerRefFor(IPersistenceProvider persistence)
        => () => ServerRegistry[persistence];

    /// <inheritdoc/>
    public void Dispose()
    {
        ServerRegistry.TryRemove(Persistence, out _);
        _server.Dispose();
    }
}
