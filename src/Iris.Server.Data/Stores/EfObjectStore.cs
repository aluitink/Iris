using Iris.Core;
using Iris.Core.Identity;
using Iris.Server.Stores;
using Iris.Server.Data.Entities;
using KristofferStrube.ActivityStreams;
using Microsoft.EntityFrameworkCore;

namespace Iris.Server.Data.Stores;

/// <summary>
/// An EF Core (PostgreSQL) <see cref="IObjectStore"/>. Content objects round-trip through a
/// <c>jsonb</c> document column; the relational columns index them for lookup.
/// </summary>
public sealed class EfObjectStore : IObjectStore
{
    private readonly IDbContextFactory<IrisDbContext> _factory;

    /// <summary>
    /// Initializes the store over a context factory.
    /// </summary>
    /// <param name="factory">The <see cref="IrisDbContext"/> factory. Must not be null.</param>
    public EfObjectStore(IDbContextFactory<IrisDbContext> factory)
        => _factory = factory ?? throw new ArgumentNullException(nameof(factory));

    /// <inheritdoc/>
    /// <remarks>
    /// Non-<c>async</c> because it has an <c>out</c> parameter (an async method cannot); the read is the
    /// synchronous <see cref="DbContext"/> query under a short-lived context (mirrors the in-memory store).
    /// </remarks>
    public Task<bool> TryGetObjectAsync(Iri objectIri, out IObject? obj, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        obj = null;
        using var db = _factory.CreateDbContext();
        var entity = db.Set<ObjectEntity>().AsNoTracking().FirstOrDefault(e => e.Id == objectIri.Value);
        if (entity is null)
        {
            return Task.FromResult(false);
        }

        obj = AsDocument.Deserialize(entity.Document) as IObject;
        return Task.FromResult(obj is not null);
    }

    /// <inheritdoc/>
    public async Task PutObjectAsync(IObject obj, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(obj);
        if (string.IsNullOrWhiteSpace(obj.Id))
        {
            throw new ArgumentException("Object must have a non-null Id.", nameof(obj));
        }

        ct.ThrowIfCancellationRequested();
        var iri = obj.Id;
        var type = TypeOf(obj);
        var isTombstone = string.Equals(type, "Tombstone", StringComparison.OrdinalIgnoreCase);
        await using var db = await _factory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var existing = await db.Set<ObjectEntity>().FirstOrDefaultAsync(e => e.Id == iri, ct).ConfigureAwait(false);
        if (existing is null)
        {
            db.Set<ObjectEntity>().Add(new ObjectEntity
            {
                Id = iri,
                AttributedTo = ExtractAttributedTo(obj),
                ObjectType = type,
                IsTombstoned = isTombstone,
                CreatedAt = DateTimeOffset.UtcNow,
                Document = AsDocument.Serialize(obj),
            });
        }
        else
        {
            existing.AttributedTo = ExtractAttributedTo(obj);
            existing.ObjectType = type;
            existing.IsTombstoned = isTombstone;
            existing.Document = AsDocument.Serialize(obj);
        }

        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// The object's primary ActivityStreams <c>@type</c> (the first of the type list), or the CLR type's
    /// name when none is set (the same fallback the search service uses).
    /// </summary>
    private static string TypeOf(IObject obj)
        => obj.Type?.FirstOrDefault() ?? obj.GetType().Name;

    /// <inheritdoc/>
    public async Task<bool> TryDeleteObjectAsync(Iri objectIri, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        await using var db = await _factory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var existing = await db.Set<ObjectEntity>().FirstOrDefaultAsync(e => e.Id == objectIri.Value, ct).ConfigureAwait(false);
        if (existing is null)
        {
            return false;
        }

        db.Set<ObjectEntity>().Remove(existing);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return true;
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<IObject>> ListObjectsAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        await using var db = await _factory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var entities = await db.Set<ObjectEntity>().AsNoTracking().ToListAsync(ct).ConfigureAwait(false);
        var result = new List<IObject>(entities.Count);
        foreach (var entity in entities)
        {
            if (AsDocument.Deserialize(entity.Document) is IObject obj)
            {
                result.Add(obj);
            }
        }

        return result;
    }

    /// <summary>
    /// Reads the object's attributed-to IRI (for the relational index) when it is a resolvable link.
    /// </summary>
    private static string? ExtractAttributedTo(IObject obj)
    {
        var attributedTo = (obj as KristofferStrube.ActivityStreams.Object)?.AttributedTo?.FirstOrDefault();
        return attributedTo?.ResolveObjectIri()?.Value;
    }
}
