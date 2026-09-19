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
    private readonly System.Func<string, bool>? _sourceSurvives;

    /// <summary>
    /// Initializes the shared edge store over a context factory.
    /// </summary>
    /// <param name="factory">The <see cref="IrisDbContext"/> factory. Must not be null.</param>
    /// <param name="sourceSurvives">
    /// Optional predicate reporting whether an edge source IRI should surface in the read paths (the
    /// deleted-actor filter, 139.3-F2). When supplied, a read with <c>filterDeletedActors: true</c>
    /// applies the predicate in-memory over the edge's source; when null (standalone edge-store tests
    /// with no actor store), no filtering is applied. The predicate returns <see langword="true"/> for
    /// a stored actor, a remote actor, or an un-provisioned local actor, and <see langword="false"/>
    /// only for a locally-provisioned actor that has since been removed.
    /// </param>
    public EdgeStore(IDbContextFactory<IrisDbContext> factory, System.Func<string, bool>? sourceSurvives = null)
    {
        _factory = factory ?? throw new ArgumentNullException(nameof(factory));
        _sourceSurvives = sourceSurvives;
    }

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
    /// <param name="kind">The edge kind.</param>
    /// <param name="source">The source IRI (the actor, or a community for membership edges).</param>
    /// <param name="ct">A cancellation token.</param>
    /// <param name="filterDeletedActors">When true, returned targets that were locally-provisioned-then
    /// deleted actors are excluded (139.3-F2) — e.g. a deleted member must not appear in a community's
    /// member list. The returned elements are the actors (or content objects); only deleted *actors* are
    /// dropped (a content object is never in the removed set, so it is unaffected).</param>
    public async Task<IReadOnlyList<Iri>> OutTargetsAsync(EdgeKind kind, string source, CancellationToken ct, bool filterDeletedActors = false)
    {
        ct.ThrowIfCancellationRequested();
        await using var db = await _factory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var query = db.Set<EdgeEntity>()
            .Where(e => e.Kind == kind && e.Source == source);
        var targets = await query
            .OrderBy(e => e.CreatedAt)
            .Select(e => e.Target)
            .ToListAsync(ct)
            .ConfigureAwait(false);
        if (filterDeletedActors && _sourceSurvives is not null)
        {
            // The returned elements are the actors (e.g. a community's members, a deleted actor's
            // outgoing targets that are actors); filter any that were deleted local actors. A content
            // object target is never in the removed set, so it is unaffected.
            targets = targets.Where(t => _sourceSurvives(t)).ToList();
        }

        return targets.Select(t => new Iri(t)).ToList();
    }

    /// <summary>
    /// Enumerates the sources of a target's incoming edges of a kind (reverse direction), as IRIs.
    /// </summary>
    /// <param name="kind">The edge kind.</param>
    /// <param name="target">The target IRI (the actor, or a content object for interaction edges).</param>
    /// <param name="ct">A cancellation token.</param>
    /// <param name="filterDeletedActors">When true, edges whose source is a deleted local actor are
    /// excluded (139.3-F2) — a removed actor must not appear among the target's followers / likers /
    /// announcers / dislikers. The target is never filtered (it is the actor being read or a content
    /// object, neither of which is the deletion subject).</param>
    public async Task<IReadOnlyList<Iri>> InSourcesAsync(EdgeKind kind, string target, CancellationToken ct, bool filterDeletedActors = false)
    {
        ct.ThrowIfCancellationRequested();
        await using var db = await _factory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var query = db.Set<EdgeEntity>()
            .Where(e => e.Kind == kind && e.Target == target);
        var sources = await query
            .OrderBy(e => e.CreatedAt)
            .Select(e => e.Source)
            .ToListAsync(ct)
            .ConfigureAwait(false);
        if (filterDeletedActors && _sourceSurvives is not null)
        {
            sources = sources.Where(s => _sourceSurvives(s)).ToList();
        }

        return sources.Select(s => new Iri(s)).ToList();
    }

    /// <summary>
    /// Enumerates the sources of a target's incoming edges of a kind (reverse direction), as IRIs,
    /// newest-first (descending by <see cref="EdgeEntity.CreatedAt"/>). Used by the pending-request queues
    /// (Phase 100) where the most recent request should surface first.
    /// </summary>
    /// <param name="kind">The edge kind.</param>
    /// <param name="target">The target IRI (the actor whose pending requests are read).</param>
    /// <param name="ct">A cancellation token.</param>
    /// <param name="filterDeletedActors">When true, edges whose source is a deleted local actor are
    /// excluded (139.3-F2).</param>
    public async Task<IReadOnlyList<Iri>> InSourcesDescendingAsync(EdgeKind kind, string target, CancellationToken ct, bool filterDeletedActors = false)
    {
        ct.ThrowIfCancellationRequested();
        await using var db = await _factory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var query = db.Set<EdgeEntity>()
            .Where(e => e.Kind == kind && e.Target == target);
        var sources = await query
            .OrderByDescending(e => e.CreatedAt)
            .Select(e => e.Source)
            .ToListAsync(ct)
            .ConfigureAwait(false);
        if (filterDeletedActors && _sourceSurvives is not null)
        {
            sources = sources.Where(s => _sourceSurvives(s)).ToList();
        }

        return sources.Select(s => new Iri(s)).ToList();
    }

    /// <summary>
    /// Enumerates all edges of a kind whose target is in the given set (batch reverse direction),
    /// grouped by target. Used by the enrichment batch to avoid N+1 queries.
    /// </summary>
    /// <param name="kind">The edge kind.</param>
    /// <param name="targets">The target IRIs to look up.</param>
    /// <param name="ct">A cancellation token.</param>
    /// <returns>A dictionary mapping each found target IRI to the set of source IRIs that point to it.</returns>
    public async Task<IReadOnlyDictionary<Iri, IReadOnlyList<Iri>>> InSourcesBatchAsync(
        EdgeKind kind, IReadOnlyCollection<string> targets, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (targets.Count == 0)
        {
            return new Dictionary<Iri, IReadOnlyList<Iri>>();
        }

        await using var db = await _factory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var rows = await db.Set<EdgeEntity>()
            .Where(e => e.Kind == kind && targets.Contains(e.Target))
            .Select(e => new { e.Source, e.Target })
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var result = new Dictionary<Iri, List<Iri>>();
        foreach (var row in rows)
        {
            var targetIri = new Iri(row.Target);
            if (!result.TryGetValue(targetIri, out var list))
            {
                list = new List<Iri>();
                result[targetIri] = list;
            }
            list.Add(new Iri(row.Source));
        }

        return result.ToDictionary(kv => kv.Key, kv => (IReadOnlyList<Iri>)kv.Value);
    }

    /// <summary>
    /// Enumerates all edges of a kind whose source is in the given set (batch forward direction),
    /// grouped by source. Used by the enrichment batch to avoid N+1 queries.
    /// </summary>
    /// <param name="kind">The edge kind.</param>
    /// <param name="sources">The source IRIs to look up.</param>
    /// <param name="ct">A cancellation token.</param>
    /// <returns>A dictionary mapping each found source IRI to the set of target IRIs it points to.</returns>
    public async Task<IReadOnlyDictionary<Iri, IReadOnlyList<Iri>>> OutTargetsBatchAsync(
        EdgeKind kind, IReadOnlyCollection<string> sources, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (sources.Count == 0)
        {
            return new Dictionary<Iri, IReadOnlyList<Iri>>();
        }

        await using var db = await _factory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var rows = await db.Set<EdgeEntity>()
            .Where(e => e.Kind == kind && sources.Contains(e.Source))
            .Select(e => new { e.Source, e.Target })
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var result = new Dictionary<Iri, List<Iri>>();
        foreach (var row in rows)
        {
            var sourceIri = new Iri(row.Source);
            if (!result.TryGetValue(sourceIri, out var list))
            {
                list = new List<Iri>();
                result[sourceIri] = list;
            }
            list.Add(new Iri(row.Target));
        }

        return result.ToDictionary(kv => kv.Key, kv => (IReadOnlyList<Iri>)kv.Value);
    }

    /// <summary>
    /// Reports which of the given (source, target) pairs of a kind are present (batch containment).
    /// Used by the enrichment batch to check isLiked/isShared for a set of objects in one query.
    /// </summary>
    /// <param name="kind">The edge kind.</param>
    /// <param name="pairs">The (source, target) pairs to check.</param>
    /// <param name="ct">A cancellation token.</param>
    /// <returns>A set of the (source, target) pairs that exist, as "source\0target" strings.</returns>
    public async Task<IReadOnlySet<string>> ContainsBatchAsync(
        EdgeKind kind, IReadOnlyCollection<(string Source, string Target)> pairs, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (pairs.Count == 0)
        {
            return new HashSet<string>();
        }

        var sources = pairs.Select(p => p.Source).Distinct().ToList();
        var targets = pairs.Select(p => p.Target).Distinct().ToList();

        await using var db = await _factory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var existing = await db.Set<EdgeEntity>()
            .Where(e => e.Kind == kind && sources.Contains(e.Source) && targets.Contains(e.Target))
            .Select(e => new { e.Source, e.Target })
            .ToListAsync(ct)
            .ConfigureAwait(false);

        return existing
            .Select(e => e.Source + "\0" + e.Target)
            .ToHashSet();
    }

    /// <summary>
    /// Enumerates all edges of a kind with their creation timestamps (for the admin moderation queue).
    /// </summary>
    public async Task<IReadOnlyList<(string Source, string Target, DateTimeOffset CreatedAt)>> AllEdgesWithTimestampsAsync(EdgeKind kind, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        await using var db = await _factory.CreateDbContextAsync(ct).ConfigureAwait(false);
        return await db.Set<EdgeEntity>()
            .Where(e => e.Kind == kind)
            .OrderByDescending(e => e.CreatedAt)
            .Select(e => new ValueTuple<string, string, DateTimeOffset>(e.Source, e.Target, e.CreatedAt))
            .ToListAsync(ct)
            .ConfigureAwait(false);
    }
}
