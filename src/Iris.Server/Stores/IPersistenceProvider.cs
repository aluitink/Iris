using Iris.Core;

namespace Iris.Server.Stores;

/// <summary>
/// The aggregate persistence boundary for an Iris server instance.
/// </summary>
/// <remarks>
/// Bundles the per-concern stores (<see cref="IActorStore"/>, <see cref="IActivityStore"/>,
/// <see cref="IFollowStore"/>, <see cref="ILikeStore"/>, <see cref="IObjectStore"/>, <see cref="ICommunityStore"/>, and
/// <see cref="IKeyStore"/> for the local actor's signing keys) behind one interface so that
/// <c>AddActivityPubServer()</c> can register a single dependency and endpoints can resolve a
/// cohesive persistence surface. Implementations: <c>Iris.Server.InMemory</c> (ephemeral) and,
/// later, a real database.
/// </remarks>
public interface IPersistenceProvider
{
    /// <summary>
    /// The actor store.
    /// </summary>
    public IActorStore Actors { get; }

    /// <summary>
    /// The activity store.
    /// </summary>
    public IActivityStore Activities { get; }

    /// <summary>
    /// The follow store.
    /// </summary>
    public IFollowStore Follows { get; }

    /// <summary>
    /// The like store.
    /// </summary>
    public ILikeStore Likes { get; }

    /// <summary>
    /// The reply (thread) store (F-12).
    /// </summary>
    public IReplyStore Replies { get; }

    /// <summary>
    /// The moderation (block) store (F-07).
    /// </summary>
    public IModerationStore Moderation { get; }

    /// <summary>
    /// The relay-subscription store (F-06).
    /// </summary>
    public IRelayStore Relays { get; }

    /// <summary>
    /// The object store.
    /// </summary>
    public IObjectStore Objects { get; }

    /// <summary>
    /// The community store.
    /// </summary>
    public ICommunityStore Communities { get; }

    /// <summary>
    /// The key store for the local instance's signing keys.
    /// </summary>
    public IKeyStore Keys { get; }
}
