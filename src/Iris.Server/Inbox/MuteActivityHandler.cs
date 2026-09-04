using Iris.Core;
using KristofferStrube.ActivityStreams;

namespace Iris.Server.Inbox;

/// <summary>
/// Handles inbound <see cref="MuteActivity"/> activities: records the directed mute edge
/// <c>muter → muted</c> in the <see cref="IModerationStore"/> when the <em>muted</em> actor is a local
/// actor (the mute arrived in the muted actor's inbox).
/// </summary>
/// <remarks>
/// A <c>Mute</c> carries <c>actor</c> = the muting actor and <c>object</c> = the muted actor, and is
/// delivered to the muted actor's inbox (the muter's own home instance records the edge locally via the
/// outbox-publish path; this handler records it on the <em>recipient's</em> instance so the muted actor's
/// home knows it has been muted — e.g. to surface a "you have been muted" signal to the operator). The
/// edge is recorded only when the muted actor is local: when both parties are remote it is not this
/// instance's concern, and when the <em>muter</em> is the only local party the muter's home instance
/// already recorded the edge (the outbox path) — recording it again here would be redundant. A malformed
/// mute (no resolvable actor or object) is stored (by the processor) but interpreted as a no-op.
/// Recording is idempotent (a repeated <c>Mute</c> does not duplicate the edge). The inverse (an
/// <c>Undo</c> of a <c>Mute</c>) is handled by <see cref="UndoActivityHandler"/>.
/// </remarks>
public sealed class MuteActivityHandler : ActivityHandlerBase<MuteActivity>
{
    private readonly IPersistenceProvider _persistence;
    private readonly ILocalActorResolver _localActors;

    /// <summary>
    /// Initializes a new <see cref="MuteActivityHandler"/>.
    /// </summary>
    /// <param name="persistence">The persistence provider (provides the <see cref="IModerationStore"/>).</param>
    /// <param name="localActors">Resolves whether the muted actor is a local actor.</param>
    /// <exception cref="ArgumentNullException">When any argument is null.</exception>
    public MuteActivityHandler(IPersistenceProvider persistence, ILocalActorResolver localActors)
    {
        ArgumentNullException.ThrowIfNull(persistence);
        ArgumentNullException.ThrowIfNull(localActors);
        _persistence = persistence;
        _localActors = localActors;
    }

    /// <inheritdoc/>
    public override async Task HandleAsync(InboxDelivery delivery, MuteActivity mute, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(delivery);
        ArgumentNullException.ThrowIfNull(mute);

        // The muter is the activity's actor; the muted actor is the activity's object
        // (Rule 3: read multi-valued as IEnumerable, null-safe).
        var muterIri = mute.Actor?.FirstOrDefault().ResolveObjectIri();
        var mutedIri = mute.Object?.FirstOrDefault().ResolveObjectIri();
        if (!muterIri.HasValue || !mutedIri.HasValue)
        {
            // A mute with no resolvable actor or object is malformed; nothing to record. The activity
            // is still stored (by the processor) so it can be inspected.
            return;
        }

        // Record the edge when the muted actor is local (the mute arrived in the muted actor's inbox).
        // When the muted actor is remote the muter's home instance recorded the edge (the outbox path);
        // when both are remote it is not this instance's concern.
        var mutedIsLocal = await _localActors.IsLocalActorAsync(mutedIri.Value, ct).ConfigureAwait(false);
        if (!mutedIsLocal)
        {
            return;
        }

        await _persistence.Moderation
            .RecordMuteAsync(muterIri.Value, mutedIri.Value, ct)
            .ConfigureAwait(false);
    }
}
