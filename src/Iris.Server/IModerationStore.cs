using Iris.Core;

namespace Iris.Server;

/// <summary>
/// Records and queries moderation relationships (F-07): the directed block edges
/// <c>blocker → blocked</c> and flag edges <c>flagger → flagged</c> an instance knows about.
/// </summary>
/// <remarks>
/// A <c>Block</c> activity (ActivityPub §5.2.1.3) carries <c>actor</c> = the blocking actor and
/// <c>object</c> = the blocked actor. The store records the directed edge so:
/// <list type="bullet">
/// <item>a local actor's <c>blocks</c> collection (served at <c>GET /ap/v1/u/{handle}/blocks</c>)
/// lists the actors it has blocked, and</item>
/// <item>the instance can determine whether an actor is blocked (e.g. to exclude a blocked actor's
/// content from a local actor's feed, or to suppress outbound delivery to an actor that blocked a
/// local actor).</item>
/// </list>
/// The edge is recorded for <em>either</em> direction of locality: when a <em>local</em> actor blocks
/// someone, or when someone blocks a <em>local</em> actor (so the local actor knows it is blocked).
/// A production host may swap in a persistent store; the handlers and endpoints depend only on this
/// interface.
/// </remarks>
public interface IModerationStore
{
    /// <summary>
    /// Records a block edge from <paramref name="blockerIri"/> to <paramref name="blockedIri"/>.
    /// Idempotent (recording the same edge twice is a no-op).
    /// </summary>
    /// <param name="blockerIri">The IRI of the actor issuing the block.</param>
    /// <param name="blockedIri">The IRI of the actor being blocked.</param>
    /// <param name="ct">Cancellation token.</param>
    public Task RecordBlockAsync(Iri blockerIri, Iri blockedIri, CancellationToken ct = default);

    /// <summary>
    /// Removes a block edge (an un-block, e.g. an <c>Undo</c> of a <c>Block</c>).
    /// </summary>
    /// <param name="blockerIri">The IRI of the actor who issued the block.</param>
    /// <param name="blockedIri">The IRI of the actor who was blocked.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A task that completes with <see langword="true"/> when a block edge was removed.</returns>
    public Task<bool> RemoveBlockAsync(Iri blockerIri, Iri blockedIri, CancellationToken ct = default);

    /// <summary>
    /// Returns the IRIs of the actors that <paramref name="blockerIri"/> has blocked (the actor's
    /// <c>blocks</c> collection).
    /// </summary>
    /// <param name="blockerIri">The IRI of the actor whose blocks collection is requested.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A task that completes with the blocked-actor IRIs (possibly empty).</returns>
    public Task<IReadOnlyList<Iri>> GetBlocksAsync(Iri blockerIri, CancellationToken ct = default);

    /// <summary>
    /// Returns whether <paramref name="blockerIri"/> has blocked <paramref name="blockedIri"/>.
    /// </summary>
    /// <param name="blockerIri">The IRI of the potential blocker.</param>
    /// <param name="blockedIri">The IRI of the potential blocked actor.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A task that completes with <see langword="true"/> when the block edge exists.</returns>
    public Task<bool> IsBlockedAsync(Iri blockerIri, Iri blockedIri, CancellationToken ct = default);

    /// <summary>
    /// Returns the IRIs of the actors that have blocked <paramref name="blockedIri"/> (the inverse
    /// query: "who has blocked this actor?"). Used to know whether a local actor is blocked by a
    /// remote actor (so its outbound delivery to that blocker can be suppressed).
    /// </summary>
    /// <param name="blockedIri">The IRI of the potentially-blocked actor.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A task that completes with the blocker IRIs (possibly empty).</returns>
    public Task<IReadOnlyList<Iri>> GetBlockersAsync(Iri blockedIri, CancellationToken ct = default);

    /// <summary>
    /// Records a flag edge from <paramref name="flaggerIri"/> to <paramref name="flaggedIri"/> (F-07
    /// moderation — the <c>Flag</c> activity, AS2.0). Idempotent (recording the same flag twice is a
    /// no-op).
    /// </summary>
    /// <param name="flaggerIri">The IRI of the actor issuing the flag.</param>
    /// <param name="flaggedIri">The IRI of the actor being flagged.</param>
    /// <param name="ct">Cancellation token.</param>
    public Task RecordFlagAsync(Iri flaggerIri, Iri flaggedIri, CancellationToken ct = default);

    /// <summary>
    /// Removes a flag edge (an un-flag, e.g. an <c>Undo</c> of a <c>Flag</c>).
    /// </summary>
    /// <param name="flaggerIri">The IRI of the actor who issued the flag.</param>
    /// <param name="flaggedIri">The IRI of the actor who was flagged.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A task that completes with <see langword="true"/> when a flag edge was removed.</returns>
    public Task<bool> RemoveFlagAsync(Iri flaggerIri, Iri flaggedIri, CancellationToken ct = default);

    /// <summary>
    /// Returns the IRIs of the actors that <paramref name="flaggerIri"/> has flagged (the actor's
    /// <c>flags</c> collection).
    /// </summary>
    /// <param name="flaggerIri">The IRI of the actor whose flags collection is requested.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A task that completes with the flagged-actor IRIs (possibly empty).</returns>
    public Task<IReadOnlyList<Iri>> GetFlagsAsync(Iri flaggerIri, CancellationToken ct = default);

    /// <summary>
    /// Returns whether <paramref name="flaggerIri"/> has flagged <paramref name="flaggedIri"/>.
    /// </summary>
    /// <param name="flaggerIri">The IRI of the potential flagger.</param>
    /// <param name="flaggedIri">The IRI of the potential flagged actor.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A task that completes with <see langword="true"/> when the flag edge exists.</returns>
    public Task<bool> HasFlaggedAsync(Iri flaggerIri, Iri flaggedIri, CancellationToken ct = default);
}
