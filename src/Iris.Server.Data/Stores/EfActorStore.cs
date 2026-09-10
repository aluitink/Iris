using Iris.Core;
using Iris.Server.Stores;
using Iris.Server.Data.Entities;
using KristofferStrube.ActivityStreams;
using Microsoft.EntityFrameworkCore;

namespace Iris.Server.Data.Stores;

/// <summary>
/// An EF Core (PostgreSQL) <see cref="IActorStore"/>. Actors round-trip through a <c>jsonb</c>
/// document column; the relational columns index identity for lookup.
/// </summary>
public sealed class EfActorStore : IActorStore
{
    private readonly IDbContextFactory<IrisDbContext> _factory;

    /// <summary>
    /// Initializes the store over a context factory.
    /// </summary>
    /// <param name="factory">The <see cref="IrisDbContext"/> factory. Must not be null.</param>
    public EfActorStore(IDbContextFactory<IrisDbContext> factory)
        => _factory = factory ?? throw new ArgumentNullException(nameof(factory));

    /// <inheritdoc/>
    /// <remarks>
    /// Non-<c>async</c> because it has an <c>out</c> parameter (an async method cannot); the read is the
    /// synchronous <see cref="DbContext"/> query under a short-lived context, appropriate for a per-request
    /// actor lookup (mirrors the in-memory store's contract).
    /// </remarks>
    public Task<bool> TryGetActorAsync(Iri actorIri, out Actor? actor, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        actor = null;
        using var db = _factory.CreateDbContext();
        var entity = db.Set<ActorEntity>().AsNoTracking().FirstOrDefault(e => e.Id == actorIri.Value);
        if (entity is null)
        {
            return Task.FromResult(false);
        }

        actor = AsDocument.Deserialize(entity.Document) as Actor;
        return Task.FromResult(actor is not null);
    }

    /// <inheritdoc/>
    public async Task PutActorAsync(Actor actor, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(actor);
        if (string.IsNullOrWhiteSpace(actor.Id))
        {
            throw new ArgumentException("Actor must have a non-null Id.", nameof(actor));
        }

        ct.ThrowIfCancellationRequested();
        var document = AsDocument.Serialize(actor);
        await using var db = await _factory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var iri = actor.Id;
        var existing = await db.Set<ActorEntity>().FirstOrDefaultAsync(e => e.Id == iri, ct).ConfigureAwait(false);
        var type = TypeOf(actor);
        if (existing is null)
        {
            db.Set<ActorEntity>().Add(new ActorEntity
            {
                Id = iri,
                Handle = actor.PreferredUsername,
                Type = type,
                CreatedAt = DateTimeOffset.UtcNow,
                Document = document,
            });
        }
        else
        {
            existing.Handle = actor.PreferredUsername;
            existing.Type = type;
            existing.Document = document;
        }

        await db.SaveChangesAsync(ct).ConfigureAwait(false);

        // Update the tsvector column via raw SQL (SearchVector is [NotMapped] — EF Core does not
        // support string → tsvector mapping). The 'simple' text search configuration does no
        // stemming or stopword removal.
        await db.Database.ExecuteSqlRawAsync(
            @"UPDATE ""Actors"" SET ""SearchVector"" =
                setweight(to_tsvector('simple', COALESCE((""Document"" ->> 'name')::text, '')), 'A') ||
                setweight(to_tsvector('simple', COALESCE((""Document"" ->> 'preferredUsername')::text, '')), 'B') ||
                setweight(to_tsvector('simple', COALESCE((""Document"" ->> 'summary')::text, '')), 'C')
              WHERE ""Id"" = {0}", new object[] { iri }, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// The actor's primary ActivityStreams <c>@type</c> (the first of the type list), or the CLR type's
    /// name when none is set (the same fallback the search service uses).
    /// </summary>
    private static string? TypeOf(IObjectOrLink value)
        => (value as IObject)?.Type?.FirstOrDefault() ?? (value as IObject)?.GetType().Name;

    /// <inheritdoc/>
    public async Task<bool> RemoveActorAsync(Iri actorIri, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        await using var db = await _factory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var existing = await db.Set<ActorEntity>().FirstOrDefaultAsync(e => e.Id == actorIri.Value, ct).ConfigureAwait(false);
        if (existing is null)
        {
            return false;
        }

        db.Set<ActorEntity>().Remove(existing);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return true;
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<Actor>> ListActorsAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        await using var db = await _factory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var entities = await db.Set<ActorEntity>().AsNoTracking().ToListAsync(ct).ConfigureAwait(false);
        var result = new List<Actor>(entities.Count);
        foreach (var entity in entities)
        {
            if (AsDocument.Deserialize(entity.Document) is Actor actor)
            {
                result.Add(actor);
            }
        }

        return result;
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<Actor>> SearchActorsAsync(string? query, int limit, int offset, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var normalized = query?.Trim();
        var hasQuery = !string.IsNullOrWhiteSpace(normalized);

        await using var db = await _factory.CreateDbContextAsync(ct).ConfigureAwait(false);
        List<ActorEntity> entities;
        if (hasQuery)
        {
            // Full-text search using the tsvector column. The 'simple' text search configuration
            // matches words literally (case-insensitive), preserving the behavior of the previous
            // ILIKE substring search but with GIN index support and ts_rank-based relevance ordering.
            // Fallback: for rows where SearchVector is NULL (pre-migration rows not yet backfilled),
            // also match via ILIKE on the Document column.
            entities = await db.Set<ActorEntity>().FromSqlRaw<ActorEntity>(
                @"SELECT * FROM ""Actors"" WHERE (
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
            entities = await db.Set<ActorEntity>().AsNoTracking()
                .OrderBy(e => e.Id)
                .Skip(offset)
                .Take(limit)
                .ToListAsync(ct)
                .ConfigureAwait(false);
        }

        var result = new List<Actor>(entities.Count);
        foreach (var entity in entities)
        {
            if (AsDocument.Deserialize(entity.Document) is Actor actor)
            {
                result.Add(actor);
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
            return await db.Set<ActorEntity>().FromSqlRaw<ActorEntity>(
                @"SELECT * FROM ""Actors"" WHERE (
                    ""SearchVector"" @@ plainto_tsquery('simple', {0})
                    OR (""SearchVector"" IS NULL AND ""Document""::text ILIKE {1} ESCAPE '\')
                )",
                normalized!, $"%{EscapeLike(normalized!)}%")
                .CountAsync(ct)
                .ConfigureAwait(false);
        }

        return await db.Set<ActorEntity>().AsNoTracking().CountAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Escapes LIKE metacharacters (<c>%</c>, <c>_</c>, <c>\</c>) so a user-supplied query is matched
    /// literally (not as a pattern). Used in the ILIKE fallback for rows without a tsvector.
    /// </summary>
    private static string EscapeLike(string value)
    {
        return value.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");
    }
}
