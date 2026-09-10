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
        var document = AsDocument.Serialize(obj);
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
                Document = document,
            });
        }
        else
        {
            existing.AttributedTo = ExtractAttributedTo(obj);
            existing.ObjectType = type;
            existing.IsTombstoned = isTombstone;
            existing.Document = document;
        }

        await db.SaveChangesAsync(ct).ConfigureAwait(false);

        // Update the tsvector column via raw SQL (SearchVector is [NotMapped] — EF Core does not
        // support string → tsvector mapping). The 'simple' text search configuration does no
        // stemming or stopword removal.
        await db.Database.ExecuteSqlRawAsync(
            @"UPDATE ""Objects"" SET ""SearchVector"" =
                setweight(to_tsvector('simple', COALESCE((""Document"" ->> 'content')::text, '')), 'A') ||
                setweight(to_tsvector('simple', COALESCE((""Document"" ->> 'name')::text, '')), 'B') ||
                setweight(to_tsvector('simple', COALESCE((""Document"" ->> 'summary')::text, '')), 'C')
              WHERE ""Id"" = {0}", new object[] { iri }, ct).ConfigureAwait(false);
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

    /// <inheritdoc/>
    public async Task<IReadOnlyList<IObject>> ListByActorAsync(Iri actorIri, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        await using var db = await _factory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var entities = await db.Set<ObjectEntity>().AsNoTracking()
            .Where(e => e.AttributedTo == actorIri.Value)
            .ToListAsync(ct).ConfigureAwait(false);
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

    /// <inheritdoc/>
    public async Task<IReadOnlyList<IObject>> SearchObjectsAsync(string? query, int limit, int offset, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var normalized = query?.Trim();
        var hasQuery = !string.IsNullOrWhiteSpace(normalized);

        await using var db = await _factory.CreateDbContextAsync(ct).ConfigureAwait(false);
        List<ObjectEntity> entities;
        if (hasQuery)
        {
            // Full-text search using the tsvector column. The 'simple' text search configuration
            // does no stemming or stopword removal — it matches words literally (case-insensitive),
            // preserving the behavior of the previous ILIKE substring search but with GIN index
            // support and ts_rank-based relevance ordering.
            // Fallback: for rows where SearchVector is NULL (pre-migration rows not yet backfilled),
            // also match via ILIKE on the Document column.
            entities = await db.Set<ObjectEntity>().FromSqlRaw<ObjectEntity>(
                @"SELECT * FROM ""Objects"" WHERE NOT ""IsTombstoned"" AND (
                    ""SearchVector"" @@ plainto_tsquery('simple', {0})
                    OR (""SearchVector"" IS NULL AND ""Document""::text ILIKE {1} ESCAPE '\')
                ) ORDER BY
                    ts_rank(""SearchVector"", plainto_tsquery('simple', {0})) DESC NULLS LAST,
                    ""Id""
                LIMIT {2} OFFSET {3}",
                normalized!, $"%{EscapeLike(normalized!)}%", limit, offset).AsNoTracking()
                .ToListAsync(ct)
                .ConfigureAwait(false);
        }
        else
        {
            entities = await db.Set<ObjectEntity>().AsNoTracking().Where(e => !e.IsTombstoned)
                .OrderBy(e => e.Id)
                .Skip(offset)
                .Take(limit)
                .ToListAsync(ct)
                .ConfigureAwait(false);
        }

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

    /// <inheritdoc/>
    public async Task<int> CountSearchMatchesAsync(string? query, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var normalized = query?.Trim();
        var hasQuery = !string.IsNullOrWhiteSpace(normalized);

        await using var db = await _factory.CreateDbContextAsync(ct).ConfigureAwait(false);
        if (hasQuery)
        {
            // Count matching rows: tsvector match OR (NULL vector AND ILIKE fallback).
            return await db.Set<ObjectEntity>().FromSqlRaw<ObjectEntity>(
                @"SELECT * FROM ""Objects"" WHERE NOT ""IsTombstoned"" AND (
                    ""SearchVector"" @@ plainto_tsquery('simple', {0})
                    OR (""SearchVector"" IS NULL AND ""Document""::text ILIKE {1} ESCAPE '\')
                )",
                normalized!, $"%{EscapeLike(normalized!)}%")
                .CountAsync(ct)
                .ConfigureAwait(false);
        }

        return await db.Set<ObjectEntity>().AsNoTracking().Where(e => !e.IsTombstoned)
            .CountAsync(ct)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Escapes LIKE metacharacters (<c>%</c>, <c>_</c>, <c>\</c>) so a user-supplied query is matched
    /// literally (not as a pattern). Used in the ILIKE fallback for rows without a tsvector.
    /// </summary>
    private static string EscapeLike(string value)
    {
        return value.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");
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
