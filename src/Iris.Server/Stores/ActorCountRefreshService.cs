using System.Text.Json;
using Iris.Core;
using Iris.Core.Identity;
using KristofferStrube.ActivityStreams;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Iris.Server.Stores;

/// <summary>
/// A background service that pre-computes the per-actor counters (<c>iris:postsCount</c>,
/// <c>iris:followersCount</c>, <c>iris:followingCount</c>) and persists them onto the stored actor
/// documents.
/// </summary>
/// <remarks>
/// <para>
/// Before this service, the actor-document and community-document read paths computed these counters
/// on <em>every</em> read by walking the actor's outbox (<see cref="IActivityStore.GetOutboxAsync"/>)
/// and the follow store (<see cref="IFollowStore.GetFollowersAsync"/> /
/// <see cref="IFollowStore.GetFollowingAsync"/>) — an O(outbox + follows) sweep per actor, per request.
/// The counters are cacheable (not per-requester), change only when a post/follow/unfollow is recorded,
/// and the same actor is read many more times than it is interacted with.
/// </para>
/// <para>
/// This service inverts that: on a fixed interval (
/// <see cref="ActivityPubServerOptions.ActorCountRefreshInterval"/>, default 30 s; a non-positive value
/// disables the periodic refresh but still runs the startup pass) it enumerates every stored actor
/// (<see cref="IActorStore.ListActorsAsync"/>), computes the three counters, and writes them onto the
/// actor's <see cref="IObject.ExtensionData"/> under the deployment's <c>iris:</c> namespace. The actor
/// is then re-stored via <see cref="IActorStore.PutActorAsync"/>, so the counts become durable and
/// serveable without a per-read sweep.
/// </para>
/// <para>
/// The <c>postsCount</c> classification mirrors the read-time
/// <c>CountPostsAsync</c> in <see cref="ActivityPubServerExtensions"/>: an outbox item counts as a post
/// when it is an <see cref="Announce"/> (boost) or a <see cref="Create"/> whose object is a
/// <see cref="Note"/> or <see cref="Article"/>. Social and moderation activities
/// (<c>Follow</c>, <c>Accept</c>, <c>Like</c>, <c>Flag</c>, …) are excluded.
/// </para>
/// <para>
/// The refresh is best-effort and idempotent: an actor whose stored counts already match the freshly
/// computed values is not re-stored (no spurious write / no cache invalidation churn). A failed pass is
/// logged and the next tick retries; it never throws into the host. The service is a no-op when the
/// instance stores no actors.
/// </para>
/// <para>
/// The stored actor's <c>privateKey</c> extension (owner-only PEM) is preserved through the re-store:
/// the service mutates the in-memory actor's <see cref="IObject.ExtensionData"/> in place and calls
/// <see cref="IActorStore.PutActorAsync"/> with the same instance, so all existing extension properties
/// are serialized back to storage.
/// </para>
/// </remarks>
public sealed class ActorCountRefreshService : BackgroundService
{
    private readonly IPersistenceProvider? _persistence;
    private readonly IOptions<ActivityPubServerOptions> _options;
    private readonly ILogger<ActorCountRefreshService> _logger;
    private readonly TimeSpan _interval;
    private readonly string? _namespace;

    /// <summary>
    /// Initializes a new <see cref="ActorCountRefreshService"/>.
    /// </summary>
    /// <param name="persistence">
    /// The shared persistence provider (its <see cref="IPersistenceProvider.Actors"/> is the enumeration
    /// source; its <see cref="IPersistenceProvider.Activities"/> and
    /// <see cref="IPersistenceProvider.Follows"/> are the count sources).
    /// </param>
    /// <param name="options">
    /// The instance's options (provides <see cref="ActivityPubServerOptions.ActorCountRefreshInterval"/>
    /// and the deployment's <c>iris:</c> namespace base via <see cref="ActivityPubServerOptions.NamespaceIri"/>).
    /// </param>
    /// <param name="logger">A logger.</param>
    public ActorCountRefreshService(
        IPersistenceProvider? persistence,
        IOptions<ActivityPubServerOptions> options,
        ILogger<ActorCountRefreshService> logger)
    {
        _persistence = persistence;
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _interval = _options.Value.ActorCountRefreshInterval;
        _namespace = ResolveIrisNamespace(_options.Value);
    }

    /// <summary>
    /// Derives the deployment's <c>iris:</c> namespace base from the options — the same rule the
    /// document handlers use.
    /// </summary>
    private static string? ResolveIrisNamespace(ActivityPubServerOptions options)
    {
        if (options.NamespaceIri is { } configured)
        {
            return configured.Value;
        }

        if (options.BaseUri is { } baseUri)
        {
            return $"{baseUri.Value.TrimEnd('/')}/{ActivityPubServerConstants.NamespaceRouteSegment}#";
        }

        return ActivityPubServerConstants.DefaultCapabilitiesNamespaceIri;
    }

    /// <inheritdoc/>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await RefreshOnceAsync(stoppingToken).ConfigureAwait(false);

        if (_interval <= TimeSpan.Zero)
        {
            return;
        }

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                await Task.Delay(_interval, stoppingToken).ConfigureAwait(false);
                await RefreshOnceAsync(stoppingToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // host stopping — expected
        }
    }

    /// <summary>
    /// Re-computes and persists the per-actor counters once. Never throws — a failed pass is
    /// logged and the next tick retries.
    /// </summary>
    internal async Task RefreshOnceAsync(CancellationToken ct)
    {
        if (string.IsNullOrEmpty(_namespace))
        {
            return;
        }

        if (_persistence is null)
        {
            return;
        }

        try
        {
            var actors = await _persistence.Actors.ListActorsAsync(ct).ConfigureAwait(false);
            if (actors.Count == 0)
            {
                return;
            }

            var ns = _namespace!;
            var updated = 0;
            for (var i = 0; i < actors.Count; i++)
            {
                var actor = actors[i];
                if (actor.Id is not { Length: > 0 } id)
                {
                    continue;
                }

                var iri = new Iri(id);

                var postsTask = CountPostsAsync(_persistence, iri, ct);
                var followersTask = _persistence.Follows.GetFollowersAsync(iri, ct);
                var followingTask = _persistence.Follows.GetFollowingAsync(iri, ct);
                var posts = await postsTask.ConfigureAwait(false);
                var followers = await followersTask.ConfigureAwait(false);
                var following = await followingTask.ConfigureAwait(false);

                if (WriteCountsIfChanged(actor, ns, posts, followers.Count, following.Count))
                {
                    await _persistence.Actors.PutActorAsync(actor, ct).ConfigureAwait(false);
                    updated++;
                }
            }

            _logger.LogDebug(
                "actor_counts_refreshed: pre-computed counters for {Total} stored actors; {Updated} updated.",
                actors.Count, updated);
        }
        catch (OperationCanceledException)
        {
            // host stopping — do not log a failure on a cancelled pass
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "actor_counts_refresh_failed: the pass failed; the stored counters keep their last value and the next tick retries.");
        }
    }

    /// <summary>
    /// Counts the content posts in an actor's outbox using the same classification as the read-time
    /// <c>CountPostsAsync</c> in <see cref="ActivityPubServerExtensions"/>: an <see cref="Announce"/>
    /// (boost) or a <see cref="Create"/> whose object is a <see cref="Note"/> or <see cref="Article"/>.
    /// </summary>
    private static async Task<int> CountPostsAsync(IPersistenceProvider persistence, Iri actorIri, CancellationToken ct)
    {
        var items = await persistence.Activities.GetOutboxAsync(actorIri, ct).ConfigureAwait(false);
        var count = 0;
        foreach (var item in items)
        {
            if (item is Announce)
            {
                count++;
                continue;
            }

            if (item is not Create create)
            {
                continue;
            }

            if (create.Object is { } objects)
            {
                foreach (var obj in objects)
                {
                    if (obj is Note || obj is Article)
                    {
                        count++;
                        break;
                    }
                }
            }
        }

        return count;
    }

    /// <summary>
    /// Writes the three counters onto the actor's <see cref="IObject.ExtensionData"/> under the given
    /// <c>iris:</c> namespace base. Returns <see langword="true"/> when any value changed (so the
    /// caller re-stores the actor); <see langword="false"/> when the stored values already match.
    /// </summary>
    private static bool WriteCountsIfChanged(
        Actor actor,
        string ns,
        int postsCount,
        int followersCount,
        int followingCount)
    {
        var changed = false;
        var ext = actor.ExtensionData ??= new Dictionary<string, JsonElement>();

        changed |= SetInt(ext, ns + IrisExtensionTerms.PostsCount, postsCount);
        changed |= SetInt(ext, ns + IrisExtensionTerms.FollowersCount, followersCount);
        changed |= SetInt(ext, ns + IrisExtensionTerms.FollowingCount, followingCount);

        return changed;
    }

    /// <summary>
    /// Sets an integer extension property only when its stored value differs (returns
    /// <see langword="true"/> when a write occurred). A missing or non-integer stored value is treated as
    /// changed.
    /// </summary>
    private static bool SetInt(Dictionary<string, JsonElement> ext, string key, int value)
    {
        if (ext.TryGetValue(key, out var existing) &&
            existing.ValueKind == JsonValueKind.Number &&
            existing.TryGetInt32(out var current) &&
            current == value)
        {
            return false;
        }

        ext[key] = JsonSerializer.SerializeToElement(value);
        return true;
    }
}
