using Iris.Server.Data.Accounts;
using Iris.Server.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace Iris.Server.Data.Stores;

/// <summary>
/// The EF Core <see cref="IInstanceMetadataStore"/>. Reads and writes the single
/// <c>InstanceMetadata</c> row.
/// </summary>
public sealed class EfInstanceMetadataStore : IInstanceMetadataStore
{
    private readonly IDbContextFactory<IrisDbContext> _contextFactory;

    /// <summary>
    /// Initializes the store over the shared context factory.
    /// </summary>
    /// <param name="contextFactory">The EF Core context factory. Must not be null.</param>
    public EfInstanceMetadataStore(IDbContextFactory<IrisDbContext> contextFactory)
    {
        _contextFactory = contextFactory ?? throw new ArgumentNullException(nameof(contextFactory));
    }

    /// <inheritdoc/>
    public async Task<InstanceMetadata?> GetAsync(CancellationToken ct = default)
    {
        await using var db = await _contextFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var entity = await db.Set<InstanceMetadataEntity>().FirstOrDefaultAsync(ct).ConfigureAwait(false);
        return entity is null
            ? null
            : new InstanceMetadata(entity.Name, entity.Description, entity.UpdatedAt);
    }

    /// <inheritdoc/>
    public async Task UpdateAsync(InstanceMetadata metadata, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        await using var db = await _contextFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var entity = await db.Set<InstanceMetadataEntity>().FirstOrDefaultAsync(ct).ConfigureAwait(false);
        if (entity is null)
        {
            entity = new InstanceMetadataEntity();
            db.Set<InstanceMetadataEntity>().Add(entity);
        }
        entity.Name = metadata.Name;
        entity.Description = metadata.Description;
        entity.UpdatedAt = metadata.UpdatedAt;
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }
}
