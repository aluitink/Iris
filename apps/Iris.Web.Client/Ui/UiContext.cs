using System.Collections.Concurrent;
using Iris.Client;
using Iris.Client.Collections;
using Iris.Core.Identity;
using Iris.Web.Client.Accounts;
using KristofferStrube.ActivityStreams;

namespace Iris.Web.Client.Ui;

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
    private static readonly TimeSpan MembershipTtl = TimeSpan.FromMinutes(2);

    private sealed record FollowingEntry(HashSet<string> Set, DateTime At);
    private sealed record ActorEntry(IObject Doc, DateTime At);
    private sealed record MembershipEntry(HashSet<string> Set, DateTime At);

    private readonly ConcurrentDictionary<string, FollowingEntry> _following = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, ActorEntry> _actors = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, MembershipEntry> _memberships = new(StringComparer.OrdinalIgnoreCase);
    // In-flight actor fetches, keyed by actor IRI value. Coalesces concurrent requests for the
    // same actor (e.g. N feed cards rendering the same author at once) into a single network
    // call — without this, each concurrent caller misses the TTL cache and fires its own GET
    // (local) / POST-proxy (remote) for the same IRI.
    private readonly ConcurrentDictionary<string, Task<IObject?>> _actorInFlight = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _membershipGate = new(1, 1);

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
    /// Concurrent calls for the same IRI are coalesced into a single network request, so N feed
    /// cards rendering the same author at once issue one GET (local) / POST-proxy (remote) rather
    /// than N.
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

        // Coalesce in-flight fetches for this IRI: the first caller starts the fetch and publishes
        // its Task; concurrent callers await the same Task instead of each hitting the network.
        var fetchTask = _actorInFlight.GetOrAdd(actorIri.Value, _ => FetchActorAsync(client, actorIri));
        try
        {
            return await fetchTask;
        }
        finally
        {
            // Clear the in-flight marker so a later call (e.g. after the TTL expires) can re-fetch.
            // TryRemove returns the value that was present; we only care that the marker is gone
            // by the time this caller finishes, which is safe because any concurrent caller that
            // already grabbed `fetchTask` holds its own reference to it.
            _actorInFlight.TryRemove(actorIri.Value, out _);
        }
    }

    /// <summary>
    /// Performs a single actor fetch for <paramref name="actorIri"/>: reads the document from the
    /// network and, on success, stores it in the per-circuit actor cache for the TTL window.
    /// Returns null when the fetch fails or the actor is not found (nothing is cached on failure,
    /// so a later call can retry).
    /// </summary>
    private async Task<IObject?> FetchActorAsync(IActivityPubClient client, Iri actorIri)
    {
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

    /// <summary>
    /// Whether the signed-in actor is a member of <paramref name="communityIri"/>. Consults the
    /// per-circuit membership cache (keyed by community IRI); on a miss, walks the community's
    /// <c>members</c> collection once and caches the full set for the TTL window.
    /// </summary>
    public async Task<bool> IsMemberAsync(Iri communityIri)
    {
        if (_session.ActorId is not { } me || _session.Client is not { } client)
        {
            return false;
        }

        if (_memberships.TryGetValue(communityIri.Value, out var entry)
            && DateTime.UtcNow - entry.At < MembershipTtl)
        {
            return entry.Set.Contains(me.Value);
        }

        await _membershipGate.WaitAsync();
        try
        {
            if (_memberships.TryGetValue(communityIri.Value, out var fresh)
                && DateTime.UtcNow - fresh.At < MembershipTtl)
            {
                return fresh.Set.Contains(me.Value);
            }

            var members = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                var membersIri = AppendSegment(communityIri, "members");
                await foreach (var item in client.GetCollectionItemsAsync(membersIri, new CollectionQuery(BypassCache: true)))
                {
                    var iri = item.ResolveObjectIri()?.Value;
                    if (iri is not null)
                    {
                        members.Add(iri);
                    }
                }
            }
            catch
            {
                // Non-fatal: return the (possibly empty) set we have so far.
            }

            _memberships[communityIri.Value] = new MembershipEntry(members, DateTime.UtcNow);
            return members.Contains(me.Value);
        }
        finally
        {
            _membershipGate.Release();
        }
    }

    /// <summary>
    /// Invalidates the cached membership set for a community (call after a join/leave so the next
    /// <see cref="IsMemberAsync"/> re-reads the server).
    /// </summary>
    public void InvalidateMembership(Iri communityIri)
    {
        _memberships.TryRemove(communityIri.Value, out _);
    }

    private static Iri AppendSegment(Iri iri, string segment)
    {
        var baseUri = iri.Uri;
        var builder = new UriBuilder(baseUri);
        var path = builder.Path;
        if (path.Length == 0 || !path.EndsWith('/'))
        {
            path += "/";
        }

        builder.Path = path + segment;
        return new Iri(builder.Uri);
    }
}
