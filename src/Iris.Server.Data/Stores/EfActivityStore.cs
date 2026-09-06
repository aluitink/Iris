using Iris.Core;
using Iris.Core.Identity;
using Iris.Server.Stores;
using Iris.Server.Data.Entities;
using KristofferStrube.ActivityStreams;
using Microsoft.EntityFrameworkCore;

namespace Iris.Server.Data.Stores;

/// <summary>
/// An EF Core (PostgreSQL) <see cref="IActivityStore"/>. Activities round-trip through a <c>jsonb</c>
/// document column; outbox/inbox membership is recorded in the shared <c>BoxItems</c> table.
/// </summary>
public sealed class EfActivityStore : IActivityStore
{
    private const int Outbox = 0;
    private const int Inbox = 1;

    private readonly IDbContextFactory<IrisDbContext> _factory;

    /// <summary>
    /// Initializes the store over a context factory.
    /// </summary>
    /// <param name="factory">The <see cref="IrisDbContext"/> factory. Must not be null.</param>
    public EfActivityStore(IDbContextFactory<IrisDbContext> factory)
        => _factory = factory ?? throw new ArgumentNullException(nameof(factory));

    /// <inheritdoc/>
    /// <remarks>
    /// Non-<c>async</c> because it has an <c>out</c> parameter (an async method cannot); the read is the
    /// synchronous <see cref="DbContext"/> query under a short-lived context (mirrors the in-memory store).
    /// </remarks>
    public Task<bool> TryGetActivityAsync(Iri activityIri, out IObject? activity, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        activity = null;
        using var db = _factory.CreateDbContext();
        var entity = db.Set<ActivityEntity>().AsNoTracking().FirstOrDefault(e => e.Id == activityIri.Value);
        if (entity is null)
        {
            return Task.FromResult(false);
        }

        activity = AsDocument.Deserialize(entity.Document) as IObject;
        return Task.FromResult(activity is not null);
    }

    /// <inheritdoc/>
    public async Task PutActivityAsync(IObject activity, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(activity);
        if (string.IsNullOrWhiteSpace(activity.Id))
        {
            throw new ArgumentException("Activity must have a non-null Id.", nameof(activity));
        }

        ct.ThrowIfCancellationRequested();
        var iri = activity.Id;
        var type = TypeOf(activity);
        await using var db = await _factory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var existing = await db.Set<ActivityEntity>().FirstOrDefaultAsync(e => e.Id == iri, ct).ConfigureAwait(false);
        if (existing is null)
        {
            db.Set<ActivityEntity>().Add(new ActivityEntity
            {
                Id = iri,
                ActivityType = type,
                CreatedAt = DateTimeOffset.UtcNow,
                Document = AsDocument.Serialize(activity),
            });
        }
        else
        {
            existing.ActivityType = type;
            existing.Document = AsDocument.Serialize(activity);
        }

        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// The activity's primary ActivityStreams <c>@type</c> (the first of the type list), or the CLR
    /// type's name when none is set (the same fallback the search service uses).
    /// </summary>
    private static string TypeOf(IObject activity)
        => activity.Type?.FirstOrDefault() ?? activity.GetType().Name;

    /// <inheritdoc/>
    public async Task<bool> TryAddActivityAsync(IObject activity, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(activity);
        if (string.IsNullOrWhiteSpace(activity.Id))
        {
            throw new ArgumentException("Activity must have a non-null Id.", nameof(activity));
        }

        ct.ThrowIfCancellationRequested();
        await using var db = await _factory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var iri = activity.Id;
        var existing = await db.Set<ActivityEntity>().FirstOrDefaultAsync(e => e.Id == iri, ct).ConfigureAwait(false);
        if (existing is not null)
        {
            return false;
        }

        db.Set<ActivityEntity>().Add(new ActivityEntity
        {
            Id = iri,
            ActivityType = TypeOf(activity),
            CreatedAt = DateTimeOffset.UtcNow,
            Document = AsDocument.Serialize(activity),
        });
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return true;
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<IObjectOrLink>> GetOutboxAsync(Iri actorIri, CancellationToken ct = default)
        => await GetBoxAsync(Outbox, actorIri, ct).ConfigureAwait(false);

    /// <inheritdoc/>
    public async Task<IReadOnlyList<IObjectOrLink>> GetInboxAsync(Iri actorIri, CancellationToken ct = default)
        => await GetBoxAsync(Inbox, actorIri, ct).ConfigureAwait(false);

    /// <summary>
    /// Reads a collection (outbox or inbox) as its items, newest first.
    /// </summary>
    private async Task<IReadOnlyList<IObjectOrLink>> GetBoxAsync(int direction, Iri actorIri, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        await using var db = await _factory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var items = await db.Set<BoxItemEntity>()
            .AsNoTracking()
            .Where(i => i.Direction == direction && i.ActorId == actorIri.Value)
            .OrderBy(i => i.Position)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var result = new List<IObjectOrLink>(items.Count);
        foreach (var item in items)
        {
            var activity = await db.Set<ActivityEntity>().AsNoTracking().FirstOrDefaultAsync(e => e.Id == item.ItemIri, ct).ConfigureAwait(false);
            if (activity is null)
            {
                continue;
            }

            if (AsDocument.Deserialize(activity.Document) is IObjectOrLink doc)
            {
                result.Add(doc);
            }
        }

        return result;
    }

    /// <inheritdoc/>
    public async Task AddToOutboxAsync(Iri actorIri, IObjectOrLink item, CancellationToken ct = default)
        => await AddToBoxAsync(Outbox, actorIri, item, ct).ConfigureAwait(false);

    /// <inheritdoc/>
    public async Task AddToInboxAsync(Iri actorIri, IObjectOrLink item, CancellationToken ct = default)
        => await AddToBoxAsync(Inbox, actorIri, item, ct).ConfigureAwait(false);

    /// <summary>
    /// Adds an item to a collection (idempotent by item IRI) and stores the item's document if it is an
    /// activity that is not already stored.
    /// </summary>
    private async Task AddToBoxAsync(int direction, Iri actorIri, IObjectOrLink item, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(item);
        ct.ThrowIfCancellationRequested();
        var itemIri = item.ResolveObjectIri()?.Value;
        if (string.IsNullOrEmpty(itemIri))
        {
            return;
        }

        await using var db = await _factory.CreateDbContextAsync(ct).ConfigureAwait(false);

        // Store the activity document if the item is a stored activity and not yet present (the outbox
        // item references the activity by IRI, so the document must exist to be read back).
        if (item is IObject activity)
        {
            var activityExists = await db.Set<ActivityEntity>().AnyAsync(e => e.Id == itemIri, ct).ConfigureAwait(false);
            if (!activityExists)
            {
                db.Set<ActivityEntity>().Add(new ActivityEntity
                {
                    Id = itemIri,
                    ActivityType = TypeOf(activity),
                    CreatedAt = DateTimeOffset.UtcNow,
                    Document = AsDocument.Serialize(item),
                });
            }
        }

        // Idempotent add: a re-recorded (direction, actor, item) is a no-op.
        var alreadyThere = await db.Set<BoxItemEntity>().AnyAsync(i => i.Direction == direction && i.ActorId == actorIri.Value && i.ItemIri == itemIri, ct).ConfigureAwait(false);
        if (!alreadyThere)
        {
            // Newer items get a lower position (served ascending). The first item is 0; each later item
            // is (current minimum) − 1, keeping the invariant without a separate sequence table.
            var current = await db.Set<BoxItemEntity>()
                .Where(i => i.Direction == direction && i.ActorId == actorIri.Value)
                .MinAsync(i => (long?)i.Position, ct)
                .ConfigureAwait(false);
            var position = current is null ? 0 : current.Value - 1;

            db.Set<BoxItemEntity>().Add(new BoxItemEntity
            {
                Direction = direction,
                ActorId = actorIri.Value,
                ItemIri = itemIri,
                Position = position,
            });
        }

        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async Task<bool> RemoveFromOutboxAsync(Iri actorIri, Iri itemIri, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        await using var db = await _factory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var existing = await db.Set<BoxItemEntity>().FirstOrDefaultAsync(i => i.Direction == Outbox && i.ActorId == actorIri.Value && i.ItemIri == itemIri.Value, ct).ConfigureAwait(false);
        if (existing is null)
        {
            return false;
        }

        db.Set<BoxItemEntity>().Remove(existing);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return true;
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<IObject>> GetAllActivitiesAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        await using var db = await _factory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var entities = await db.Set<ActivityEntity>().AsNoTracking().ToListAsync(ct).ConfigureAwait(false);
        var result = new List<IObject>(entities.Count);
        foreach (var entity in entities)
        {
            if (AsDocument.Deserialize(entity.Document) is IObject activity)
            {
                result.Add(activity);
            }
        }

        return result;
    }
}
