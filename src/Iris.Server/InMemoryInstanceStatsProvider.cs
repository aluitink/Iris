namespace Iris.Server;

/// <summary>
/// Default <see cref="IInstanceStatsProvider"/> that returns zero for all counts.
/// Registered as the fallback when no persistence-layer implementation is available
/// (e.g. in-memory persistence or a host that doesn't wire the EF Core provider).
/// </summary>
public sealed class InMemoryInstanceStatsProvider : IInstanceStatsProvider
{
    /// <inheritdoc/>
    public Task<int> GetLocalUserCountAsync(CancellationToken ct = default)
        => Task.FromResult(0);
}
