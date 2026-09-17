using System.Text.Json;
using Iris.Core;
using Iris.Server.Stores;
using KristofferStrube.ActivityStreams;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Iris.Server.Security;

/// <summary>
/// Persists remote actor documents to the durable <see cref="IActorStore"/> on first encounter,
/// so the directory's "All known" scope can list actors the instance has seen during federation.
/// </summary>
/// <remarks>
/// <para>
/// When a remote actor's document is fetched (via <see cref="IActorDocumentFetcher"/>), this
/// persister stores it in the durable actor store if it is not already present. Local actors
/// (whose IRI starts with the instance base) are never persisted by this class — they are
/// provisioned by the registration flow.
/// </para>
/// <para>
/// Persistence is best-effort: a failure to store (e.g. a transient DB error) is logged but does
/// not fail the fetch. The in-memory <see cref="RemoteActorCache"/> continues to serve the document
/// for the cache TTL regardless of whether the durable write succeeded.
/// </para>
/// <para>
/// An actor is only persisted once (the <c>TryGetActorAsync</c> check makes this idempotent).
/// Subsequent fetches of the same actor hit the cache and skip the store check entirely.
/// </para>
/// </remarks>
public sealed class RemoteActorPersister
{
    private readonly IActorStore _actors;
    private readonly Iri? _instanceBase;
    private readonly ILogger<RemoteActorPersister> _logger;

    /// <summary>
    /// Initializes a new <see cref="RemoteActorPersister"/>.
    /// </summary>
    /// <param name="actors">The durable actor store to persist remote actors into.</param>
    /// <param name="instanceBase">
    /// The instance base IRI (e.g. <c>https://iris.luit.ink/ap/v1</c>). Used to exclude local
    /// actors from persistence. May be null (all fetched actors are persisted).
    /// </param>
    /// <param name="logger">The logger. May be null (a no-op logger is used).</param>
    public RemoteActorPersister(
        IActorStore actors,
        Iri? instanceBase = null,
        ILogger<RemoteActorPersister>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(actors);
        _actors = actors;
        _instanceBase = instanceBase;
        _logger = logger ?? NullLogger<RemoteActorPersister>.Instance;
    }

    /// <summary>
    /// Persists <paramref name="actor"/> to the durable store if it is a remote actor not already
    /// stored. Local actors (IRI prefix matches <see cref="_instanceBase"/>) are skipped.
    /// </summary>
    /// <param name="actor">The remote actor document to persist.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns><see langword="true"/> when the actor was newly persisted; <see langword="false"/>
    /// when it was already stored, is a local actor, or has no IRI.</returns>
    public async Task<bool> PersistIfNewAsync(Actor? actor, CancellationToken ct = default)
    {
        if (actor is null || actor.Id is not { } idStr)
        {
            return false;
        }

        var iri = new Iri(idStr);

        // Skip local actors — they are provisioned by the registration flow.
        if (_instanceBase is { } instanceBase)
        {
            var prefix = instanceBase.Value.TrimEnd('/');
            if (idStr.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        var alreadyStored = await _actors.TryGetActorAsync(iri, out _, ct).ConfigureAwait(false);
        if (alreadyStored)
        {
            return false;
        }

        try
        {
            await _actors.PutActorAsync(actor, ct).ConfigureAwait(false);
            _logger.LogInformation("Persisted remote actor {Iri} to durable store", iri);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to persist remote actor {Iri} to durable store", iri);
            return false;
        }
    }

    /// <summary>
    /// Archives a fetched remote ActivityStreams object when it is a remote <see cref="Actor"/> not
    /// already stored. This is the general "archive any remote object we fetch" seam (Phase 117.3):
    /// it is called from the proxy fallback, which is the single choke point for every client-originated
    /// outbound fetch, so an actor the user browses (e.g. via the directory's external lookup) is
    /// archived even though it never passes through the inbound signature-validation path (where
    /// <see cref="IrisActorDocumentFetcher"/> would have persisted it).
    /// </summary>
    /// <remarks>
    /// <para>
    /// A <see cref="Group"/> is deliberately NOT archived here: a Group is a remote community, and it
    /// is persisted to the durable <see cref="ICommunityStore"/> by <see cref="RemoteCommunityPersister"/>
    /// (the proxy routes Group documents to that persister). Archiving a Group into the actor store
    /// would duplicate it and, depending on the store, could even fail (a Group is not an
    /// <see cref="Actor"/> in the ActivityStreams model).
    /// </para>
    /// <para>
    /// Like <see cref="PersistIfNewAsync(Actor, CancellationToken)"/>, this is best-effort and
    /// idempotent: a non-actor object, a local actor, or an already-stored actor is skipped; a store
    /// failure is logged but never propagated (archiving must not break the proxied read).
    /// </para>
    /// </remarks>
    /// <param name="obj">The fetched ActivityStreams object (may be an actor, a community Group, or any other content object).</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns><see langword="true"/> when a remote actor was newly persisted; <see langword="false"/>
    /// when the object is not a remote actor (a Group, a non-actor, a local actor, or an already-stored actor).</returns>
    public async Task<bool> PersistIfNewAsync(IObject? obj, CancellationToken ct = default)
    {
        // Only actors are archived here. A Group is a remote community (handled by the community
        // persister); any other object (a Note, an Article, a collection, …) is content, not an actor.
        if (obj is Group || obj is not Actor actor)
        {
            return false;
        }

        return await PersistIfNewAsync(actor, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Replaces the stored copy of a remote actor with a freshly fetched document (the proxy
    /// endpoint's cache-refresh path): the stored document is overwritten and stamped with the
    /// server-internal <c>iris:fetchedAt</c> freshness mark (the current UTC time) in
    /// <see cref="KristofferStrube.ActivityStreams.Object.ExtensionData"/> so the proxy's cache-first
    /// read can tell a fresh cached document from a stale one. Local actors (IRI prefix matches
    /// <see cref="_instanceBase"/>) are never touched — they are provisioned, not cached.
    /// </summary>
    /// <remarks>
    /// Best-effort and idempotent like <see cref="PersistIfNewAsync(Actor, CancellationToken)"/>: a
    /// store failure is logged and never propagated (the refresh must not break the proxied read).
    /// Unlike <see cref="PersistIfNewAsync(Actor, CancellationToken)"/>, this OVERWRITES the stored
    /// document (a profile update must land), and it stamps the freshness mark so the next cache-first
    /// read within the freshness window serves the refreshed copy without re-fetching.
    /// </remarks>
    /// <param name="actor">The freshly fetched remote actor document to store.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns><see langword="true"/> when the stored copy was refreshed; <see langword="false"/>
    /// when the actor has no IRI, is a local actor, or the store write failed.</returns>
    public async Task<bool> RefreshAsync(Actor? actor, CancellationToken ct = default)
    {
        if (actor is null || actor.Id is not { } idStr)
        {
            return false;
        }

        var iri = new Iri(idStr);

        // Skip local actors — they are provisioned by the registration flow, never cached.
        if (_instanceBase is { } instanceBase)
        {
            var prefix = instanceBase.Value.TrimEnd('/');
            if (idStr.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        try
        {
            actor.ExtensionData ??= new Dictionary<string, JsonElement>();
            actor.ExtensionData[ActivityPubExtensionNames.FetchedAt] =
                JsonSerializer.SerializeToElement(DateTimeOffset.UtcNow.ToString("O"));
            await _actors.PutActorAsync(actor, ct).ConfigureAwait(false);
            _logger.LogInformation("Refreshed cached remote actor {Iri}", iri);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to refresh cached remote actor {Iri}", iri);
            return false;
        }
    }
}
