using Iris.Core;
using KristofferStrube.ActivityStreams;

namespace Iris.Server;

/// <summary>
/// Handles inbound <see cref="Flag"/> activities (F-07 moderation): records the directed flag edge
/// <c>flagger → flagged</c> in the <see cref="IModerationStore"/> when <em>either</em> party is a local
/// actor.
/// </summary>
/// <remarks>
/// Per AS2.0, a <c>Flag</c> carries <c>actor</c> = the flagging actor and <c>object</c> = the flagged
/// actor (the flag is a moderation report — it does not sever the relationship the way a
/// <see cref="BlockActivityHandler"/> block does). The handler records the edge in two cases (mirroring
/// the block handler):
/// <list type="number">
/// <item><strong>Local flagger.</strong> When <c>actor</c> is a local actor, the local actor flagged
/// <c>object</c> — the edge is recorded so the local actor's <c>flags</c> collection
/// (served at <c>GET /ap/v1/u/{handle}/flags</c>) lists the flagged actor.</item>
/// <item><strong>Local flagged.</strong> When <c>object</c> is a local actor (the flag arrived in the
/// local actor's inbox), a remote actor flagged the local actor — the edge is recorded so the instance
/// knows the local actor has been flagged (a moderation signal for operators).</item>
/// </list>
/// When both parties are remote, the edge is not recorded (it is not this instance's concern). A
/// malformed flag (no resolvable actor or object) is stored (by the processor) but interpreted as a
/// no-op. Recording is idempotent (a repeated <c>Flag</c> does not duplicate the edge). Unlike a
/// <c>Block</c>, a <c>Flag</c> has no feed/delivery application — it is a report the instance stores and
/// surfaces in the flagger's <c>flags</c> collection; the inverse (an <c>Undo</c> of a <c>Flag</c>) is
/// handled by <see cref="UndoActivityHandler"/>.
/// </remarks>
public sealed class FlagActivityHandler : ActivityHandlerBase<Flag>
{
    private readonly IPersistenceProvider _persistence;
    private readonly ILocalActorResolver _localActors;

    /// <summary>
    /// Initializes a new <see cref="FlagActivityHandler"/>.
    /// </summary>
    /// <param name="persistence">The persistence provider (provides the <see cref="IModerationStore"/>).</param>
    /// <param name="localActors">Resolves whether the flagger (or the flagged) is a local actor.</param>
    /// <exception cref="ArgumentNullException">When any argument is null.</exception>
    public FlagActivityHandler(IPersistenceProvider persistence, ILocalActorResolver localActors)
    {
        ArgumentNullException.ThrowIfNull(persistence);
        ArgumentNullException.ThrowIfNull(localActors);
        _persistence = persistence;
        _localActors = localActors;
    }

    /// <inheritdoc/>
    public override async Task HandleAsync(InboxDelivery delivery, Flag flag, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(delivery);
        ArgumentNullException.ThrowIfNull(flag);

        // The flagger is the activity's actor; the flagged actor is the activity's object
        // (Rule 3: read multi-valued as IEnumerable, null-safe).
        var flaggerIri = flag.Actor?.FirstOrDefault().ResolveObjectIri();
        var flaggedIri = flag.Object?.FirstOrDefault().ResolveObjectIri();
        if (!flaggerIri.HasValue || !flaggedIri.HasValue)
        {
            // A flag with no resolvable actor or object is malformed; nothing to record. The activity
            // is still stored (by the processor) so it can be inspected.
            return;
        }

        // Record the edge when either party is local: a local flagger (its flags collection lists the
        // flagged actor) or a local flagged (the instance knows the local actor was flagged). When both
        // are remote, it is not this instance's concern.
        var flaggerIsLocal = await _localActors.IsLocalActorAsync(flaggerIri.Value, ct).ConfigureAwait(false);
        var flaggedIsLocal = await _localActors.IsLocalActorAsync(flaggedIri.Value, ct).ConfigureAwait(false);
        if (!flaggerIsLocal && !flaggedIsLocal)
        {
            return;
        }

        await _persistence.Moderation
            .RecordFlagAsync(flaggerIri.Value, flaggedIri.Value, ct)
            .ConfigureAwait(false);
    }
}
