namespace Iris.Server.Data.Accounts;

/// <summary>
/// The in-memory <see cref="IInstanceMetadataStore"/> (the default backend for the bare host and
/// integration tests). Holds a single <see cref="InstanceMetadata"/> value (null until first set).
/// </summary>
public sealed class InMemoryInstanceMetadataStore : IInstanceMetadataStore
{
    private readonly object _gate = new();
    private InstanceMetadata? _metadata;

    /// <inheritdoc/>
    public Task<InstanceMetadata?> GetAsync(CancellationToken ct = default)
    {
        lock (_gate)
        {
            return Task.FromResult<InstanceMetadata?>(_metadata);
        }
    }

    /// <inheritdoc/>
    public Task UpdateAsync(InstanceMetadata metadata, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        lock (_gate)
        {
            _metadata = metadata;
        }
        return Task.CompletedTask;
    }
}
