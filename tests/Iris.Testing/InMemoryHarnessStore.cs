namespace Iris.Testing;

/// <summary>
/// In-memory implementation of <see cref="IHarnessStore"/> used by the test pipeline.
/// </summary>
public sealed class InMemoryHarnessStore : IHarnessStore
{
    /// <summary>
    /// Initializes a new instance bound to the given hostname.
    /// </summary>
    /// <param name="hostname">The hostname this store is bound to.</param>
    public InMemoryHarnessStore(string hostname)
    {
        Hostname = hostname;
    }

    /// <inheritdoc/>
    public string Hostname { get; }
}
