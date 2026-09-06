using Iris.Core;
using Iris.Server.Stores;
using Iris.Server.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace Iris.Server.Data.Stores;

/// <summary>
/// An EF Core (PostgreSQL) <see cref="ICreateIndex"/>: the object → <c>Create</c> index (decision 055).
/// </summary>
public sealed class EfCreateIndex : ICreateIndex
{
    private readonly IDbContextFactory<IrisDbContext> _factory;

    /// <summary>
    /// Initializes the store over a context factory.
    /// </summary>
    /// <param name="factory">The <see cref="IrisDbContext"/> factory. Must not be null.</param>
    public EfCreateIndex(IDbContextFactory<IrisDbContext> factory)
        => _factory = factory ?? throw new ArgumentNullException(nameof(factory));

    /// <inheritdoc/>
    public async Task RecordAsync(Iri objectIri, Iri createIri, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        await using var db = await _factory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var existing = await db.Set<CreateIndexEntity>().FirstOrDefaultAsync(e => e.ObjectId == objectIri.Value, ct).ConfigureAwait(false);
        if (existing is null)
        {
            db.Set<CreateIndexEntity>().Add(new CreateIndexEntity { ObjectId = objectIri.Value, CreateActivityId = createIri.Value });
        }
        else
        {
            existing.CreateActivityId = createIri.Value;
        }

        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async Task<bool> RemoveAsync(Iri objectIri, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        await using var db = await _factory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var existing = await db.Set<CreateIndexEntity>().FirstOrDefaultAsync(e => e.ObjectId == objectIri.Value, ct).ConfigureAwait(false);
        if (existing is null)
        {
            return false;
        }

        db.Set<CreateIndexEntity>().Remove(existing);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return true;
    }

    /// <inheritdoc/>
    public async Task<Iri?> TryGetCreateIriAsync(Iri objectIri, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        await using var db = await _factory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var entity = await db.Set<CreateIndexEntity>().AsNoTracking().FirstOrDefaultAsync(e => e.ObjectId == objectIri.Value, ct).ConfigureAwait(false);
        return entity is null ? null : new Iri(entity.CreateActivityId);
    }
}
