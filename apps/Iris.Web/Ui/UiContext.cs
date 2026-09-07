using System.Collections.Concurrent;
using Iris.Client;
using Iris.Core.Identity;
using Iris.Web.Accounts;
using KristofferStrube.ActivityStreams;

namespace Iris.Web.Ui;

/// <summary>
/// UI-layer context that caches ActivityPub reads the Blazor components need repeatedly
/// (follow-state, actor documents) so multiple components sharing a circuit don't each
/// walk the same collections. Sits above the general-purpose <see cref="IActivityPubClient"/>
/// and adds the UI-specific caching/prefetch concerns the client shouldn't know about.
/// </summary>
public sealed class UiContext
{
    private static readonly TimeSpan FollowingTtl = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan ActorTtl = TimeSpan.FromMinutes(5);

    private sealed record FollowingEntry(HashSet<string> Set, DateTime At);
    private sealed record ActorEntry(IObject Doc, DateTime At);

    private readonly ConcurrentDictionary<string, FollowingEntry> _following = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, ActorEntry> _actors = new(StringComparer.OrdinalIgnoreCase);

    private readonly IActorSessionAccessor _session;
    private readonly SemaphoreSlim _followingGate = new(1, 1);

    public UiContext(IActorSessionAccessor session)
    {
        _session = session;
    }

    /// <summary>
    /// The signed-in actor's IRI, or null when signed out.
    /// </summary>
    public Iri? CurrentActorId => _session.ActorId;

    /// <summary>
    /// Whether the signed-in actor follows <paramref name="targetIri"/>. Consults the per-circuit
    /// following-set cache; on a miss, walks the follower's following collection once and caches
    /// the full set for the TTL window. Subsequent checks for other targets are O(1) lookups.
    /// </summary>
    public async Task<bool> IsFollowingAsync(Iri targetIri)
    {
        if (_session.ActorId is not { } me || _session.Client is not { } client)
        {
            return false;
        }

        if (_following.TryGetValue(me.Value, out var entry)
            && DateTime.UtcNow - entry.At < FollowingTtl)
        {
            return entry.Set.Contains(targetIri.Value);
        }

        await _followingGate.WaitAsync();
        try
        {
            // Re-check under the gate (another task may have refreshed while we waited).
            if (_following.TryGetValue(me.Value, out var fresh)
                && DateTime.UtcNow - fresh.At < FollowingTtl)
            {
                return fresh.Set.Contains(targetIri.Value);
            }

            var following = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                await foreach (var item in client.GetCollectionItemsAsync(me.FollowingOf()))
                {
                    var iri = item.ResolveObjectIri()?.Value;
                    if (iri is not null)
                    {
                        following.Add(iri);
                    }
                }
            }
            catch
            {
                // Non-fatal: return the (possibly empty) set we have so far.
            }

            _following[me.Value] = new FollowingEntry(following, DateTime.UtcNow);
            return following.Contains(targetIri.Value);
        }
        finally
        {
            _followingGate.Release();
        }
    }

    /// <summary>
    /// Finds the server-minted IRI of the <c>Follow</c> activity the signed-in actor sent to
    /// <paramref name="targetIri"/> by scanning the actor's outbox. Returns null when no such
    /// activity is found or the outbox cannot be read.
    /// </summary>
    public async Task<string?> GetFollowActivityIriAsync(Iri targetIri)
    {
        if (_session.ActorId is not { } me || _session.Client is not { } client)
        {
            return null;
        }

        try
        {
            await foreach (var item in client.GetCollectionItemsAsync(me.OutboxOf()))
            {
                if (item is Follow follow && follow.Id is { Length: > 0 } id)
                {
                    var target = follow.Object?.FirstOrDefault()?.ResolveObjectIri();
                    if (target is not null && target == targetIri)
                    {
                        return id;
                    }
                }
            }
        }
        catch
        {
            // Non-fatal: return null.
        }

        return null;
    }

    /// <summary>
    /// Invalidates the cached following set for the signed-in actor (call after a follow/unfollow
    /// so the next <see cref="IsFollowingAsync"/> re-reads the server).
    /// </summary>
    public void InvalidateFollowing()
    {
        if (_session.ActorId is { } me)
        {
            _following.TryRemove(me.Value, out _);
        }
    }

    /// <summary>
    /// Gets an actor document by IRI, consulting the per-circuit actor cache (5-minute TTL).
    /// Returns null when the actor is not found or the client is unavailable.
    /// </summary>
    public async Task<IObject?> GetActorAsync(Iri actorIri)
    {
        if (_session.Client is not { } client)
        {
            return null;
        }

        if (_actors.TryGetValue(actorIri.Value, out var cached)
            && DateTime.UtcNow - cached.At < ActorTtl)
        {
            return cached.Doc;
        }

        IObject? doc;
        try
        {
            doc = await client.GetObjectAsync(actorIri);
        }
        catch
        {
            return null;
        }

        if (doc is not null)
        {
            _actors[actorIri.Value] = new ActorEntry(doc, DateTime.UtcNow);
        }

        return doc;
    }

    /// <summary>
    /// Invalidates a cached actor document (e.g. after the actor's profile is updated).
    /// </summary>
    public void InvalidateActor(Iri actorIri)
    {
        _actors.TryRemove(actorIri.Value, out _);
    }
}
