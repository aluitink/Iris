using Iris.Core;
using Iris.Server.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace Iris.Server.Data.Stores;

/// <summary>
/// The shared EF Core backing for every relationship store (follows, likes, announces, replies, relays,
/// moderation edges). All of them are the same shape — a directed edge <c>source → target</c> of a named
/// <see cref="EdgeKind"/> — so they read/write the single <c>Edges</c> table and only differ in which
/// kind they address and which direction they enumerate.
/// </summary>
public sealed class EdgeStore
{
    private readonly IDbContextFactory<IrisDbContext> _factory;

    /// <summary>
    /// Initializes the shared edge store over a context factory.
    /// </summary>
    /// <param name="factory">The <see cref="IrisDbContext"/> factory. Must not be null.</param>
    public EdgeStore(IDbContextFactory<IrisDbContext> factory)
        => _factory = factory ?? throw new ArgumentNullException(nameof(factory));

    /// <summary>
    /// Adds a directed edge (idempotent — a re-added edge is a no-op).
    /// </summary>
    public async Task AddAsync(EdgeKind kind, string source, string target, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        await using var db = await _factory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var exists = await db.Set<EdgeEntity>().AnyAsync(e => e.Kind == kind && e.Source == source && e.Target == target, ct).ConfigureAwait(false);
        if (exists)
        {
            return;
        }

        db.Set<EdgeEntity>().Add(new EdgeEntity { Kind = kind, Source = source, Target = target, CreatedAt = DateTimeOffset.UtcNow });
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Adds a directed edge and reports whether it was newly added (an idempotent re-add returns
    /// <see langword="false"/>).
    /// </summary>
    public async Task<bool> AddIfNewAsync(EdgeKind kind, string source, string target, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        await using var db = await _factory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var exists = await db.Set<EdgeEntity>().AnyAsync(e => e.Kind == kind && e.Source == source && e.Target == target, ct).ConfigureAwait(false);
        if (exists)
        {
            return false;
        }

        db.Set<EdgeEntity>().Add(new EdgeEntity { Kind = kind, Source = source, Target = target, CreatedAt = DateTimeOffset.UtcNow });
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return true;
    }

    /// <summary>
    /// Removes a directed edge if present.
    /// </summary>
    /// <returns><see langword="true"/> when an edge was removed; otherwise <see langword="false"/>.</returns>
    public async Task<bool> RemoveAsync(EdgeKind kind, string source, string target, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        await using var db = await _factory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var existing = await db.Set<EdgeEntity>().FirstOrDefaultAsync(e => e.Kind == kind && e.Source == source && e.Target == target, ct).ConfigureAwait(false);
        if (existing is null)
        {
            return false;
        }

        db.Set<EdgeEntity>().Remove(existing);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return true;
    }

    /// <summary>
    /// Reports whether a directed edge is present.
    /// </summary>
    public async Task<bool> ContainsAsync(EdgeKind kind, string source, string target, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        await using var db = await _factory.CreateDbContextAsync(ct).ConfigureAwait(false);
        return await db.Set<EdgeEntity>().AnyAsync(e => e.Kind == kind && e.Source == source && e.Target == target, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Enumerates the targets of a source's outgoing edges of a kind (forward direction), as IRIs.
    /// </summary>
    public async Task<IReadOnlyList<Iri>> OutTargetsAsync(EdgeKind kind, string source, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        await using var db = await _factory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var targets = await db.Set<EdgeEntity>()
            .Where(e => e.Kind == kind && e.Source == source)
            .OrderBy(e => e.CreatedAt)
            .Select(e => e.Target)
            .ToListAsync(ct)
            .ConfigureAwait(false);
        return targets.Select(t => new Iri(t)).ToList();
    }

    /// <summary>
    /// Enumerates the sources of a target's incoming edges of a kind (reverse direction), as IRIs.
    /// </summary>
    public async Task<IReadOnlyList<Iri>> InSourcesAsync(EdgeKind kind, string target, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        await using var db = await _factory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var sources = await db.Set<EdgeEntity>()
            .Where(e => e.Kind == kind && e.Target == target)
            .OrderBy(e => e.CreatedAt)
            .Select(e => e.Source)
            .ToListAsync(ct)
            .ConfigureAwait(false);
        return sources.Select(s => new Iri(s)).ToList();
    }
}
