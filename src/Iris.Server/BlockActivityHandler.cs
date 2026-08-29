using Iris.Core;
using KristofferStrube.ActivityStreams;

namespace Iris.Server;

/// <summary>
/// Handles inbound <see cref="Block"/> activities (F-07 moderation): records the directed block edge
/// <c>blocker → blocked</c> in the <see cref="IModerationStore"/> when <em>either</em> party is a local
/// actor.
/// </summary>
/// <remarks>
/// Per ActivityPub §5.2.1.3, a <c>Block</c> carries <c>actor</c> = the blocking actor and
/// <c>object</c> = the blocked actor, and is delivered to the blocked actor's inbox (the block is
/// effective on the blocker's side, but the recipient is notified). The handler records the edge in
/// two cases:
/// <list type="number">
/// <item><strong>Local blocker.</strong> When <c>actor</c> is a local actor, the local actor blocked
/// <c>object</c> — the edge is recorded so the local actor's <c>blocks</c> collection
/// (served at <c>GET /ap/v1/u/{handle}/blocks</c>) lists the blocked actor.</item>
/// <item><strong>Local blocked.</strong> When <c>object</c> is a local actor (the block arrived in the
/// local actor's inbox), a remote actor blocked the local actor — the edge is recorded so the instance
/// knows the local actor is blocked (e.g. to suppress outbound delivery to that blocker).</item>
/// </list>
/// When both parties are remote, the edge is not recorded (it is not this instance's concern). A
/// malformed block (no resolvable actor or object) is stored (by the processor) but interpreted as a
/// no-op. Recording is idempotent (a repeated <c>Block</c> does not duplicate the edge).
/// </remarks>
public sealed class BlockActivityHandler : ActivityHandlerBase<Block>
{
    private readonly IPersistenceProvider _persistence;
    private readonly ILocalActorResolver _localActors;

    /// <summary>
    /// Initializes a new <see cref="BlockActivityHandler"/>.
    /// </summary>
    /// <param name="persistence">The persistence provider (provides the <see cref="IModerationStore"/>).</param>
    /// <param name="localActors">Resolves whether the blocker (or the blocked) is a local actor.</param>
    /// <exception cref="ArgumentNullException">When any argument is null.</exception>
    public BlockActivityHandler(IPersistenceProvider persistence, ILocalActorResolver localActors)
    {
        ArgumentNullException.ThrowIfNull(persistence);
        ArgumentNullException.ThrowIfNull(localActors);
        _persistence = persistence;
        _localActors = localActors;
    }

    /// <inheritdoc/>
    public override async Task HandleAsync(InboxDelivery delivery, Block block, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(delivery);
        ArgumentNullException.ThrowIfNull(block);

        // The blocker is the activity's actor; the blocked actor is the activity's object
        // (Rule 3: read multi-valued as IEnumerable, null-safe).
        var blockerIri = block.Actor?.FirstOrDefault().ResolveObjectIri();
        var blockedIri = block.Object?.FirstOrDefault().ResolveObjectIri();
        if (!blockerIri.HasValue || !blockedIri.HasValue)
        {
            // A block with no resolvable actor or object is malformed; nothing to record. The activity
            // is still stored (by the processor) so it can be inspected.
            return;
        }

        // Record the edge when either party is local: a local blocker (its blocks collection lists the
        // blocked actor) or a local blocked (the instance knows the local actor is blocked). When both
        // are remote, it is not this instance's concern.
        var blockerIsLocal = await _localActors.IsLocalActorAsync(blockerIri.Value, ct).ConfigureAwait(false);
        var blockedIsLocal = await _localActors.IsLocalActorAsync(blockedIri.Value, ct).ConfigureAwait(false);
        if (!blockerIsLocal && !blockedIsLocal)
        {
            return;
        }

        await _persistence.Moderation
            .RecordBlockAsync(blockerIri.Value, blockedIri.Value, ct)
            .ConfigureAwait(false);
    }
}
