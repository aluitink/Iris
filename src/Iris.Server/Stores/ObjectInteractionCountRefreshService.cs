using System.Text.Json;
using Iris.Core;
using Iris.Core.Identity;
using KristofferStrube.ActivityStreams;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Iris.Server.Stores;

/// <summary>
/// A background service that pre-computes the per-object interaction counters (<c>iris:likedCount</c>,
/// <c>iris:sharedCount</c>, <c>iris:repliedCount</c>, <c>iris:dislikedCount</c>, and <c>iris:score</c>) and
/// persists them onto the stored object documents (Phase 151 — background processing).
/// </summary>
/// <remarks>
/// <para>
/// Before this service, the object-document and collection-page read paths computed these counters on
/// <em>every</em> read by walking the <see cref="ILikeStore"/>, <see cref="IAnnounceStore"/>,
/// <see cref="IReplyStore"/>, and <see cref="IDislikeStore"/> reverse indexes (an O(n) sweep per object,
/// per request). At scale that is wasted work: the counters are cacheable (not per-requester), change
/// only when a like/boost/reply/dislike edge is recorded or removed, and the same object is read many
/// more times than it is interacted with.
/// </para>
/// <para>
/// This service inverts that: on a fixed interval (
/// <see cref="ActivityPubServerOptions.ObjectInteractionRefreshInterval"/>, default 30 s; a non-positive
/// value disables the periodic refresh but still runs the startup pass) it enumerates every stored
/// object (<see cref="IObjectStore.ListObjectsAsync"/>), batch-reads the four reverse indexes in a single
/// pass, and writes the resulting counts onto each non-tombstone object's <see cref="IObject.ExtensionData"/>
/// under the deployment's <c>iris:</c> namespace. The object is then re-stored via
/// <see cref="IObjectStore.PutObjectAsync"/>, so the counts become durable and serveable without a
/// per-read sweep.
/// </para>
/// <para>
/// The refresh is best-effort and idempotent: an object whose stored counts already match the freshly
/// computed values is not re-stored (no spurious write / no cache invalidation churn). A failed pass (a
/// transient persistence blip) is logged and the next tick retries; it never throws into the host. The
/// service is a no-op when the instance stores no objects, so a host with an empty store is unaffected.
/// </para>
/// </remarks>
public sealed class ObjectInteractionCountRefreshService : BackgroundService
{
    private readonly IPersistenceProvider? _persistence;
    private readonly IOptions<ActivityPubServerOptions> _options;
    private readonly ILogger<ObjectInteractionCountRefreshService> _logger;
    private readonly TimeSpan _interval;
    private readonly string? _namespace;

    /// <summary>
    /// Initializes a new <see cref="ObjectInteractionCountRefreshService"/>.
    /// </summary>
    /// <param name="persistence">
    /// The shared persistence provider (its <see cref="IPersistenceProvider.Objects"/> is the enumeration
    /// source; its <see cref="IPersistenceProvider.Likes"/>, <see cref="IPersistenceProvider.Announces"/>,
    /// <see cref="IPersistenceProvider.Replies"/>, and <see cref="IPersistenceProvider.Dislikes"/> are the
    /// count sources).
    /// </param>
    /// <param name="options">
    /// The instance's options (provides <see cref="ActivityPubServerOptions.ObjectInteractionRefreshInterval"/>
    /// and the deployment's <c>iris:</c> namespace base via <see cref="ActivityPubServerOptions.NamespaceIri"/>).
    /// </param>
    /// <param name="logger">A logger.</param>
    public ObjectInteractionCountRefreshService(
        IPersistenceProvider? persistence,
        IOptions<ActivityPubServerOptions> options,
        ILogger<ObjectInteractionCountRefreshService> logger)
    {
        // Null persistence (a host that does not add a persistence provider) makes the service inert —
        // there are no stored objects to refresh. The service is registered unconditionally in
        // AddActivityPubServer, so it must tolerate a null provider rather than throw at resolution.
        _persistence = persistence;
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _interval = _options.Value.ObjectInteractionRefreshInterval;
        _namespace = ResolveIrisNamespace(_options.Value);
    }

    /// <summary>
    /// Derives the deployment's <c>iris:</c> namespace base from the options — the same rule the
    /// document handlers use (<c>ActivityPubServerExtensions</c>): the explicitly configured
    /// <see cref="ActivityPubServerOptions.NamespaceIri"/>, else <c>{BaseUri}/ns#</c>, else the default.
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
        // The startup pass runs before the interval loop so a fresh store converges its counters before the
        // first read that could otherwise fall back to the per-read sweep.
        await RefreshOnceAsync(stoppingToken).ConfigureAwait(false);

        // A non-positive interval disables the periodic refresh (the startup pass still ran). This lets a
        // host that wants on-demand refresh opt out of the timer without disabling the service.
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
    /// Re-computes and persists the per-object interaction counters once (the pass
    /// <see cref="ExecuteAsync"/> runs at startup and on each tick). Never throws — a failed pass is
    /// logged and the next tick retries.
    /// </summary>
    internal async Task RefreshOnceAsync(CancellationToken ct)
    {
        if (string.IsNullOrEmpty(_namespace))
        {
            // No iris: namespace configured (an unusual host) — there is no extension key to write the
            // counts under, so the refresh is a no-op.
            return;
        }

        if (_persistence is null)
        {
            // No persistence provider — there are no stored objects to refresh (inert).
            return;
        }

        try
        {
            var objects = await _persistence.Objects.ListObjectsAsync(ct).ConfigureAwait(false);
            if (objects.Count == 0)
            {
                return;
            }

            // Collect the non-tombstone objects with a resolvable IRI (a tombstone has no interaction state
            // to pre-compute, and an object without an IRI cannot be addressed in the reverse indexes).
            var targets = new List<IObject>(objects.Count);
            var iris = new List<Iri>(objects.Count);
            foreach (var obj in objects)
            {
                if (obj is Tombstone || obj.Id is not { Length: > 0 } id)
                {
                    continue;
                }

                var iri = new Iri(id);
                targets.Add(obj);
                iris.Add(iri);
            }

            if (iris.Count == 0)
            {
                return;
            }

            // A single batch read of each reverse index (57.4 — avoids an N+1 per object). Objects with no
            // edges are simply absent from the result maps.
            var likersByObject = await _persistence.Likes.GetLikersBatchAsync(iris, ct).ConfigureAwait(false);
            var announcersByObject = await _persistence.Announces.GetAnnouncersBatchAsync(iris, ct).ConfigureAwait(false);
            var repliesByObject = await _persistence.Replies.GetRepliesBatchAsync(iris, ct).ConfigureAwait(false);

            var ns = _namespace!;
            var updated = 0;
            for (var i = 0; i < targets.Count; i++)
            {
                var obj = targets[i];
                var iri = iris[i];

                var likedCount = likersByObject.TryGetValue(iri, out var likers) ? likers.Count : 0;
                var sharedCount = announcersByObject.TryGetValue(iri, out var announcers) ? announcers.Count : 0;
                var repliedCount = repliesByObject.TryGetValue(iri, out var replies) ? replies.Count : 0;
                // The dislike store has no batch method (it is the least-used counter); a single read per
                // object is acceptable here because the refresh is off the request path and runs on a timer.
                var dislikedCount = (await _persistence.Dislikes.GetDislikersAsync(iri, ct).ConfigureAwait(false)).Count;
                var score = likedCount - dislikedCount;

                if (WriteCountsIfChanged(obj, ns, likedCount, sharedCount, repliedCount, dislikedCount, score))
                {
                    await _persistence.Objects.PutObjectAsync(obj, ct).ConfigureAwait(false);
                    updated++;
                }
            }

            _logger.LogDebug(
                "object_interaction_counts_refreshed: pre-computed counters for {Total} stored objects; {Updated} updated.",
                targets.Count, updated);
        }
        catch (OperationCanceledException)
        {
            // host stopping — do not log a failure on a cancelled pass
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "object_interaction_counts_refresh_failed: the pass failed; the stored counters keep their last value and the next tick retries.");
        }
    }

    /// <summary>
    /// Re-computes and persists the per-object interaction counters for a single stored object, so the
    /// object's denormalized <c>likedCount</c> / <c>sharedCount</c> / <c>repliedCount</c> /
    /// <c>dislikedCount</c> reflect a just-recorded (or removed) like / boost / reply / dislike edge
    /// immediately, rather than waiting for the next interval pass. No-op when the object is not stored
    /// locally (a remote object's counters live on the object's home instance), is a tombstone, has no
    /// resolvable IRI, or no <c>iris:</c> namespace is configured.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The object-document and collection-page read paths serve the pre-computed counters (persisted onto
    /// the stored object's <see cref="IObject.ExtensionData"/>) when present, and only fall back to the
    /// per-read reverse-index sweep when they are absent. Once the startup / first tick has persisted a
    /// zero for an object that has no edges, a subsequent like does not make those counters absent — it
    /// leaves them stale until the next interval pass. Calling this right after recording (or removing) a
    /// like edge closes that gap: the counter is refreshed synchronously so the very next read of the
    /// object's document is correct (S37 — a remote Like is stored and the <c>/likes</c> collection is
    /// correct, but the Note's <c>likedCount</c> stays 0 until the 30 s refresh tick).
    /// </para>
    /// </remarks>
    /// <param name="objectIri">The IRI of the stored object whose counters should be refreshed.</param>
    /// <param name="ct">A cancellation token.</param>
    /// <returns><see langword="true"/> when the object's stored counters were updated;
    /// <see langword="false"/> when there was nothing to update (object not stored / tombstone / no
    /// namespace) or the stored counters already matched the freshly computed values.</returns>
    public async Task<bool> RefreshObjectCountsAsync(Iri objectIri, CancellationToken ct)
    {
        if (_namespace is null)
        {
            return false;
        }

        if (_persistence is null)
        {
            return false;
        }

        if (!await _persistence.Objects.TryGetObjectAsync(objectIri, out var obj, ct).ConfigureAwait(false)
            || obj is null)
        {
            // A remote (not locally stored) object's counters are maintained on the object's home
            // instance, not here — there is nothing to refresh locally.
            return false;
        }

        if (obj is Tombstone || obj.Id is not { Length: > 0 })
        {
            return false;
        }

        var likers = (await _persistence.Likes.GetLikersAsync(objectIri, ct).ConfigureAwait(false)).Count;
        var shared = (await _persistence.Announces.GetAnnouncersAsync(objectIri, ct).ConfigureAwait(false)).Count;
        var replied = (await _persistence.Replies.GetRepliesAsync(objectIri, ct).ConfigureAwait(false)).Count;
        var disliked = (await _persistence.Dislikes.GetDislikersAsync(objectIri, ct).ConfigureAwait(false)).Count;
        var score = likers - disliked;

        if (!WriteCountsIfChanged(obj, _namespace!, likers, shared, replied, disliked, score))
        {
            return false;
        }

        await _persistence.Objects.PutObjectAsync(obj, ct).ConfigureAwait(false);
        return true;
    }

    /// <summary>
    /// Writes the four counters (and the derived <c>iris:score</c>) onto the object's
    /// <see cref="IObject.ExtensionData"/> under the given <c>iris:</c> namespace base. Returns
    /// <see langword="true"/> when any value changed (so the caller re-stores the object);
    /// <see langword="false"/> when the stored values already match (no write, no cache churn).
    /// </summary>
    private static bool WriteCountsIfChanged(
        IObject obj,
        string ns,
        int likedCount,
        int sharedCount,
        int repliedCount,
        int dislikedCount,
        int score)
    {
        var changed = false;
        var ext = obj.ExtensionData ??= new Dictionary<string, JsonElement>();

        changed |= SetInt(ext, ns + IrisExtensionTerms.LikedCount, likedCount);
        changed |= SetInt(ext, ns + IrisExtensionTerms.SharedCount, sharedCount);
        changed |= SetInt(ext, ns + IrisExtensionTerms.RepliedCount, repliedCount);
        changed |= SetInt(ext, ns + IrisExtensionTerms.DislikedCount, dislikedCount);
        changed |= SetInt(ext, ns + IrisExtensionTerms.Score, score);

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
