using Microsoft.AspNetCore.TestHost;

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
/// then shared, so the fixture is only correct for
/// test classes whose methods are read-only or idempotent (they do not assert on absolute collection
/// counts that a prior method's writes would pollute). A mutating class must reset its persistence per
/// method (or build its own host) rather than share this one.
/// </remarks>
public class SharedHostFixture : IDisposable
{
    private readonly TestServer _server;

    /// <summary>The shared in-process host, built once for the collection.</summary>
    public TestServer Server { get; }

    /// <summary>The host's <see cref="IPersistenceProvider"/> (its seeded in-memory store), exposed for
    /// test code that needs to read or (for a mutating class, after a per-method reset) write state.</summary>
    public IPersistenceProvider Persistence { get; }

    /// <summary>Builds the shared host from <paramref name="options"/> (its <see cref="ActivityPubHostOptions.Persistence"/>
    /// is seeded before the host binds it) and registers the local key when requested.</summary>
    public SharedHostFixture(ActivityPubHostOptions options)
    {
        _server = ActivityPubHostFactory.Create(options);
        Server = _server;
        Persistence = options.Persistence;
    }

    /// <inheritdoc/>
    public void Dispose() => _server.Dispose();
}
