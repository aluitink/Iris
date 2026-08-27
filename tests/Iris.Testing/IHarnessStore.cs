namespace Iris.Testing;

/// <summary>
/// A minimal persistence marker resolved by the test pipeline. Phase 3 replaces this
/// with the real <c>IPersistenceProvider</c> + store interfaces from <c>Iris.Server</c>.
/// </summary>
public interface IHarnessStore
{
    /// <summary>
    /// Gets the hostname this store instance is bound to (for diagnostics).
    /// </summary>
    string Hostname { get; }
}
