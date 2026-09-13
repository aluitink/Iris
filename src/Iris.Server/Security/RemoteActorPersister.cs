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
}
