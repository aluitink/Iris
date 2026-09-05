using Iris.Core;
using Iris.Server;

namespace Iris.Server.InMemory;

/// <summary>
/// An in-memory <see cref="IPersistenceProvider"/> that bundles the in-memory stores.
/// </summary>
/// <remarks>
/// Ephemeral: all data vanishes on restart. The key store is shared with the server's signing
/// infrastructure (the local actor's signing key lives here).
/// </remarks>
public sealed class InMemoryPersistenceProvider : IPersistenceProvider
{
    private readonly InMemoryActorStore _actors;
    private readonly InMemoryActivityStore _activities;
    private readonly InMemoryFollowStore _follows;
    private readonly InMemoryLikeStore _likes;
    private readonly InMemoryAnnounceStore _announces;
    private readonly InMemoryReplyStore _replies;
    private readonly InMemoryModerationStore _moderation;
    private readonly InMemoryRelayStore _relays;
    private readonly InMemoryObjectStore _objects;
    private readonly InMemoryCreateIndex _creates;
    private readonly InMemoryCommunityStore _communities;
    private readonly IKeyStore _keys;
    private readonly IMediaStore _media;

    /// <summary>
    /// Initializes a new provider with fresh in-memory stores and a fresh in-memory key store.
    /// </summary>
    public InMemoryPersistenceProvider()
        : this(new InMemoryActorStore(), new InMemoryActivityStore(), new InMemoryFollowStore(),
            new InMemoryLikeStore(), new InMemoryAnnounceStore(), new InMemoryReplyStore(),
            new InMemoryModerationStore(), new InMemoryRelayStore(), new InMemoryObjectStore(),
            new InMemoryCreateIndex(), new InMemoryCommunityStore(), new InMemoryKeyStore(),
            new InMemoryMediaStore())
    {
    }

    /// <summary>
    /// Initializes a new provider over the given stores (used by tests to pre-seed data).
    /// </summary>
    /// <param name="actors">The actor store.</param>
    /// <param name="activities">The activity store.</param>
    /// <param name="follows">The follow store.</param>
    /// <param name="likes">The like store.</param>
    /// <param name="announces">The announce (boost) store.</param>
    /// <param name="replies">The reply (thread) store (F-12).</param>
    /// <param name="moderation">The moderation (block) store (F-07).</param>
    /// <param name="relays">The relay-subscription store (F-06).</param>
    /// <param name="objects">The object store.</param>
    /// <param name="creates">The object → Create index (decision 055).</param>
    /// <param name="communities">The community store.</param>
    /// <param name="keys">The key store.</param>
    /// <param name="media">The media store (Phase 20.4 (a)).</param>
    public InMemoryPersistenceProvider(
        InMemoryActorStore actors,
        InMemoryActivityStore activities,
        InMemoryFollowStore follows,
        InMemoryLikeStore likes,
        InMemoryAnnounceStore announces,
        InMemoryReplyStore replies,
        InMemoryModerationStore moderation,
        InMemoryRelayStore relays,
        InMemoryObjectStore objects,
        InMemoryCreateIndex creates,
        InMemoryCommunityStore communities,
        IKeyStore keys,
        IMediaStore media)
    {
        _actors = actors ?? throw new ArgumentNullException(nameof(actors));
        _activities = activities ?? throw new ArgumentNullException(nameof(activities));
        _follows = follows ?? throw new ArgumentNullException(nameof(follows));
        _likes = likes ?? throw new ArgumentNullException(nameof(likes));
        _announces = announces ?? throw new ArgumentNullException(nameof(announces));
        _replies = replies ?? throw new ArgumentNullException(nameof(replies));
        _moderation = moderation ?? throw new ArgumentNullException(nameof(moderation));
        _relays = relays ?? throw new ArgumentNullException(nameof(relays));
        _objects = objects ?? throw new ArgumentNullException(nameof(objects));
        _creates = creates ?? throw new ArgumentNullException(nameof(creates));
        _communities = communities ?? throw new ArgumentNullException(nameof(communities));
        _keys = keys ?? throw new ArgumentNullException(nameof(keys));
        _media = media ?? throw new ArgumentNullException(nameof(media));
    }

    /// <inheritdoc/>
    public IActorStore Actors => _actors;

    /// <inheritdoc/>
    public IActivityStore Activities => _activities;

    /// <inheritdoc/>
    public IFollowStore Follows => _follows;

    /// <inheritdoc/>
    public ILikeStore Likes => _likes;

    /// <inheritdoc/>
    public IAnnounceStore Announces => _announces;

    /// <inheritdoc/>
    public IReplyStore Replies => _replies;

    /// <inheritdoc/>
    public IModerationStore Moderation => _moderation;

    /// <inheritdoc/>
    public IRelayStore Relays => _relays;

    /// <inheritdoc/>
    public IObjectStore Objects => _objects;

    /// <inheritdoc/>
    public ICreateIndex Creates => _creates;

    /// <inheritdoc/>
    public ICommunityStore Communities => _communities;

    /// <inheritdoc/>
    public IKeyStore Keys => _keys;

    /// <inheritdoc/>
    public IMediaStore Media => _media;

    /// <summary>
    /// The concrete in-memory actor store (for seeding/tests).
    /// </summary>
    public InMemoryActorStore ActorStore => _actors;

    /// <summary>
    /// The concrete in-memory activity store (for seeding/tests).
    /// </summary>
    public InMemoryActivityStore ActivityStore => _activities;

    /// <summary>
    /// Clears all persisted data (actors, activities, follows, likes, announces, replies,
    /// moderation, relays, objects, creates, communities, media) while leaving the key store
    /// intact, so the host's signing infrastructure keeps working.
    /// Used by tests that share a single host across methods (per-method reset) and by
    /// teardown paths.
    /// </summary>
    public void Reset()
    {
        _actors.Clear();
        _activities.Clear();
        _follows.Clear();
        _likes.Clear();
        _announces.Clear();
        _replies.Clear();
        _moderation.Clear();
        _relays.Clear();
        _objects.Clear();
        _creates.Clear();
        _communities.Clear();
        if (_media is InMemoryMediaStore concreteMedia)
        {
            concreteMedia.Clear();
        }
    }
}
