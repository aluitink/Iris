using Iris.Core;
using KristofferStrube.ActivityStreams;

namespace Iris.Server.Stores;

/// <summary>
/// Reads and writes <see cref="Group"/> (community) documents by their IRI.
/// </summary>
/// <remarks>
/// A community is the library's <see cref="Group"/> actor type. The <c>iris:capabilities</c>
/// extension (see Resolved Decision #11) is carried in the group's <c>ExtensionData</c>.
/// </remarks>
public interface ICommunityStore
{
    /// <summary>
    /// Attempts to retrieve the community for the given IRI.
    /// </summary>
    /// <param name="communityIri">The IRI identifying the community.</param>
    /// <param name="community">When successful, the community; otherwise null.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A task that completes with <see langword="true"/> if the community was found; otherwise <see langword="false"/>.</returns>
    public Task<bool> TryGetCommunityAsync(Iri communityIri, out Group? community, CancellationToken ct = default);

    /// <summary>
    /// Stores (or replaces) the community under its IRI. The community's <c>Id</c> must already be set.
    /// </summary>
    /// <param name="community">The community to store. Must not be null and must have a non-null <c>Id</c>.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <exception cref="ArgumentNullException">When <paramref name="community"/> is null.</exception>
    /// <exception cref="ArgumentException">When the community has no <c>Id</c>.</exception>
    public Task PutCommunityAsync(Group community, CancellationToken ct = default);

    /// <summary>
    /// Adds a local actor as a member of the community. Idempotent: adding an existing member is a no-op.
    /// </summary>
    /// <param name="communityIri">The IRI identifying the community.</param>
    /// <param name="actorIri">The IRI of the local actor to add as a member.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A task that completes with <see langword="true"/> when a new membership was added; <see langword="false"/> when the actor was already a member.</returns>
    public Task<bool> AddMemberAsync(Iri communityIri, Iri actorIri, CancellationToken ct = default);

    /// <summary>
    /// Removes a local actor from the community.
    /// </summary>
    /// <param name="communityIri">The IRI identifying the community.</param>
    /// <param name="actorIri">The IRI of the local actor to remove.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A task that completes with <see langword="true"/> when a membership was removed; <see langword="false"/> when the actor was not a member.</returns>
    public Task<bool> RemoveMemberAsync(Iri communityIri, Iri actorIri, CancellationToken ct = default);

    /// <summary>
    /// Determines whether a local actor is a member of the community.
    /// </summary>
    /// <param name="communityIri">The IRI identifying the community.</param>
    /// <param name="actorIri">The IRI of the local actor to check.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A task that completes with <see langword="true"/> when the actor is a member; otherwise <see langword="false"/> (including when the community does not exist).</returns>
    public Task<bool> IsMemberAsync(Iri communityIri, Iri actorIri, CancellationToken ct = default);

    /// <summary>
    /// Returns the IRIs of all local actors that are members of the community.
    /// </summary>
    /// <param name="communityIri">The IRI identifying the community.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A task that completes with the member IRIs (empty when the community does not exist or has no members).</returns>
    public Task<IReadOnlyCollection<Iri>> GetMembersAsync(Iri communityIri, CancellationToken ct = default);

    /// <summary>
    /// Returns the IRIs of all actors (communities or persons) that the community follows.
    /// </summary>
    /// <remarks>
    /// The community's "following" set is what makes its unified feed a <em>federated</em> feed: the
    /// content of the actors it follows (delivered to the community's inbox by the federation path) is
    /// surfaced to its members alongside the members' own posts. A community follows an actor the same
    /// way a person does (a directed <c>follower → target</c> edge), but the edge is recorded against
    /// the community, not a person.
    /// </remarks>
    /// <param name="communityIri">The IRI identifying the community.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A task that completes with the followed actor IRIs (empty when the community does not exist or follows nothing).</returns>
    public Task<IReadOnlyCollection<Iri>> GetFollowsAsync(Iri communityIri, CancellationToken ct = default);

    /// <summary>
    /// Records that the community follows the given actor. Idempotent: recording an existing follow is a no-op.
    /// </summary>
    /// <param name="communityIri">The IRI identifying the community.</param>
    /// <param name="actorIri">The IRI of the actor the community follows.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A task that completes with <see langword="true"/> when a new follow was recorded; <see langword="false"/> when the community already followed the actor.</returns>
    public Task<bool> AddFollowAsync(Iri communityIri, Iri actorIri, CancellationToken ct = default);

    /// <summary>
    /// Removes a follow recorded for the community.
    /// </summary>
    /// <param name="communityIri">The IRI identifying the community.</param>
    /// <param name="actorIri">The IRI of the actor the community no longer follows.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A task that completes with <see langword="true"/> when a follow was removed; <see langword="false"/> when the community did not follow the actor.</returns>
    public Task<bool> RemoveFollowAsync(Iri communityIri, Iri actorIri, CancellationToken ct = default);

    /// <summary>
    /// Returns the IRIs of all actors (communities or persons) that follow the community.
    /// </summary>
    /// <remarks>
    /// The community's "followers" set is the inverse of its follows set: when an actor follows a local
    /// community, the <see cref="FollowActivityHandler"/> records both that the community follows the
    /// follower (the follows set, so the follower's content reaches the community's members via the
    /// federation path) and that the follower follows the community (this set, so the community's
    /// <c>followers</c> collection — <c>GET /c/{name}/followers</c> — lists the follower). This closes
    /// F-24 (the community's <c>followers</c> collection was always empty because no edge was recorded
    /// in the follower → community direction).
    /// </remarks>
    /// <param name="communityIri">The IRI identifying the community.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A task that completes with the follower IRIs (empty when the community does not exist or has no followers).</returns>
    public Task<IReadOnlyCollection<Iri>> GetFollowersAsync(Iri communityIri, CancellationToken ct = default);

    /// <summary>
    /// Records that the given actor follows the community. Idempotent: recording an existing follower is a no-op.
    /// </summary>
    /// <param name="communityIri">The IRI identifying the community.</param>
    /// <param name="actorIri">The IRI of the actor that follows the community.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A task that completes with <see langword="true"/> when a new follower was recorded; <see langword="false"/> when the actor already followed the community.</returns>
    public Task<bool> AddFollowerAsync(Iri communityIri, Iri actorIri, CancellationToken ct = default);

    /// <summary>
    /// Removes a follower recorded for the community (the actor no longer follows the community).
    /// </summary>
    /// <param name="communityIri">The IRI identifying the community.</param>
    /// <param name="actorIri">The IRI of the actor that no longer follows the community.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A task that completes with <see langword="true"/> when a follower was removed; <see langword="false"/> when the actor did not follow the community.</returns>
    public Task<bool> RemoveFollowerAsync(Iri communityIri, Iri actorIri, CancellationToken ct = default);

    /// <summary>
    /// Returns the IRIs of all communities this store hosts (the local communities).
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A task that completes with the community IRIs (possibly empty).</returns>
    public Task<IReadOnlyCollection<Iri>> GetAllCommunityIrisAsync(CancellationToken ct = default);

    // --- Community moderation (19.5.4) ---
    //
    // A community moderates the actors whose content it surfaces in its unified feed: a community-level
    // block/mute/flag edge is a <c>community → actor</c> edge (the community is the moderator, the actor
    // is moderated) recorded in this community's own moderation sets, distinct from the person-level
    // moderation edges (which are keyed by an actor IRI in the <see cref="IModerationStore"/>). The
    // community's unified feed excludes the content of the members it has blocked or muted (and, for a
    // flagged member, the flag is a soft moderation report that does not exclude content on its own — it
    // is surfaced in the community's <c>flags</c> collection for the operator to act on). A community
    // blocks/mutes/flags an actor the same way a person does, but the edge is recorded against the
    // community, not a person, so a community operator's moderation is scoped to that community (another
    // community's feed is unaffected).

    /// <summary>
    /// Records that <paramref name="communityIri"/> has blocked <paramref name="actorIri"/>. Idempotent:
    /// recording an existing block is a no-op.
    /// </summary>
    /// <param name="communityIri">The IRI of the community issuing the block.</param>
    /// <param name="actorIri">The IRI of the actor being blocked.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A task that completes with <see langword="true"/> when a new block was recorded; <see langword="false"/> when the community already blocked the actor.</returns>
    public Task<bool> AddBlockAsync(Iri communityIri, Iri actorIri, CancellationToken ct = default);

    /// <summary>
    /// Removes a block recorded for the community.
    /// </summary>
    /// <param name="communityIri">The IRI of the community that issued the block.</param>
    /// <param name="actorIri">The IRI of the actor that was blocked.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A task that completes with <see langword="true"/> when a block was removed; <see langword="false"/> when the community did not block the actor.</returns>
    public Task<bool> RemoveBlockAsync(Iri communityIri, Iri actorIri, CancellationToken ct = default);

    /// <summary>
    /// Returns the IRIs of the actors the community has blocked.
    /// </summary>
    /// <param name="communityIri">The IRI identifying the community.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A task that completes with the blocked actor IRIs (empty when the community has blocked no one).</returns>
    public Task<IReadOnlyCollection<Iri>> GetBlocksAsync(Iri communityIri, CancellationToken ct = default);

    /// <summary>
    /// Records that <paramref name="communityIri"/> has flagged <paramref name="actorIri"/>. Idempotent:
    /// recording an existing flag is a no-op.
    /// </summary>
    /// <param name="communityIri">The IRI of the community issuing the flag.</param>
    /// <param name="actorIri">The IRI of the actor being flagged.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A task that completes with <see langword="true"/> when a new flag was recorded; <see langword="false"/> when the community already flagged the actor.</returns>
    public Task<bool> AddFlagAsync(Iri communityIri, Iri actorIri, CancellationToken ct = default);

    /// <summary>
    /// Removes a flag recorded for the community.
    /// </summary>
    /// <param name="communityIri">The IRI of the community that issued the flag.</param>
    /// <param name="actorIri">The IRI of the actor that was flagged.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A task that completes with <see langword="true"/> when a flag was removed; <see langword="false"/> when the community did not flag the actor.</returns>
    public Task<bool> RemoveFlagAsync(Iri communityIri, Iri actorIri, CancellationToken ct = default);

    /// <summary>
    /// Returns the IRIs of the actors the community has flagged.
    /// </summary>
    /// <param name="communityIri">The IRI identifying the community.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A task that completes with the flagged actor IRIs (empty when the community has flagged no one).</returns>
    public Task<IReadOnlyCollection<Iri>> GetFlagsAsync(Iri communityIri, CancellationToken ct = default);

    /// <summary>
    /// Records that <paramref name="communityIri"/> has muted <paramref name="actorIri"/>. Idempotent:
    /// recording an existing mute is a no-op.
    /// </summary>
    /// <param name="communityIri">The IRI of the community issuing the mute.</param>
    /// <param name="actorIri">The IRI of the actor being muted.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A task that completes with <see langword="true"/> when a new mute was recorded; <see langword="false"/> when the community already muted the actor.</returns>
    public Task<bool> AddMuteAsync(Iri communityIri, Iri actorIri, CancellationToken ct = default);

    /// <summary>
    /// Removes a mute recorded for the community.
    /// </summary>
    /// <param name="communityIri">The IRI of the community that issued the mute.</param>
    /// <param name="actorIri">The IRI of the actor that was muted.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A task that completes with <see langword="true"/> when a mute was removed; <see langword="false"/> when the community did not mute the actor.</returns>
    public Task<bool> RemoveMuteAsync(Iri communityIri, Iri actorIri, CancellationToken ct = default);

    /// <summary>
    /// Returns the IRIs of the actors the community has muted.
    /// </summary>
    /// <param name="communityIri">The IRI identifying the community.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A task that completes with the muted actor IRIs (empty when the community has muted no one).</returns>
    public Task<IReadOnlyCollection<Iri>> GetMutesAsync(Iri communityIri, CancellationToken ct = default);
}
