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
    private readonly InMemoryDislikeStore _dislikes;
    private readonly InMemoryAnnounceStore _announces;
    private readonly InMemoryReplyStore _replies;
    private readonly InMemoryModerationStore _moderation;
    private readonly InMemoryRelayStore _relays;
    private readonly InMemoryObjectStore _objects;
    private readonly InMemoryCreateIndex _creates;
    private readonly InMemoryCommunityStore _communities;
    private readonly IKeyStore _keys;
    private readonly IMediaStore _media;
    private readonly IBookmarkStore _bookmarks;

    /// <summary>
    /// Initializes a new provider with fresh in-memory stores and a fresh in-memory key store.
    /// </summary>
    public InMemoryPersistenceProvider()
        : this(new InMemoryActorStore(), new InMemoryActivityStore(), new InMemoryFollowStore(),
            new InMemoryLikeStore(), new InMemoryDislikeStore(), new InMemoryAnnounceStore(), new InMemoryReplyStore(),
            new InMemoryModerationStore(), new InMemoryRelayStore(), new InMemoryObjectStore(),
            new InMemoryCreateIndex(), new InMemoryCommunityStore(), new InMemoryKeyStore(),
            new InMemoryMediaStore(), new InMemoryBookmarkStore())
    {
    }

    /// <summary>
    /// Initializes a new provider over the given stores (used by tests to pre-seed data).
    /// </summary>
    /// <param name="actors">The actor store.</param>
    /// <param name="activities">The activity store.</param>
    /// <param name="follows">The follow store.</param>
    /// <param name="likes">The like store.</param>
    /// <param name="dislikes">The dislike store.</param>
    /// <param name="announces">The announce (boost) store.</param>
    /// <param name="replies">The reply (thread) store (F-12).</param>
    /// <param name="moderation">The moderation (block) store (F-07).</param>
    /// <param name="relays">The relay-subscription store (F-06).</param>
    /// <param name="objects">The object store.</param>
    /// <param name="creates">The object → Create index (decision 055).</param>
    /// <param name="communities">The community store.</param>
    /// <param name="keys">The key store.</param>
    /// <param name="media">The media store (Phase 20.4 (a)).</param>
    /// <param name="bookmarks">The bookmark store (S111).</param>
    public InMemoryPersistenceProvider(
        InMemoryActorStore actors,
        InMemoryActivityStore activities,
        InMemoryFollowStore follows,
        InMemoryLikeStore likes,
        InMemoryDislikeStore dislikes,
        InMemoryAnnounceStore announces,
        InMemoryReplyStore replies,
        InMemoryModerationStore moderation,
        InMemoryRelayStore relays,
        InMemoryObjectStore objects,
        InMemoryCreateIndex creates,
        InMemoryCommunityStore communities,
        IKeyStore keys,
        IMediaStore media,
        IBookmarkStore bookmarks)
    {
        _actors = actors ?? throw new ArgumentNullException(nameof(actors));
        _activities = activities ?? throw new ArgumentNullException(nameof(activities));
        _follows = follows ?? throw new ArgumentNullException(nameof(follows));
        _likes = likes ?? throw new ArgumentNullException(nameof(likes));
        _dislikes = dislikes ?? throw new ArgumentNullException(nameof(dislikes));
        _announces = announces ?? throw new ArgumentNullException(nameof(announces));
        _replies = replies ?? throw new ArgumentNullException(nameof(replies));
        _moderation = moderation ?? throw new ArgumentNullException(nameof(moderation));
        _relays = relays ?? throw new ArgumentNullException(nameof(relays));
        _objects = objects ?? throw new ArgumentNullException(nameof(objects));
        _creates = creates ?? throw new ArgumentNullException(nameof(creates));
        _communities = communities ?? throw new ArgumentNullException(nameof(communities));
        _keys = keys ?? throw new ArgumentNullException(nameof(keys));
        _media = media ?? throw new ArgumentNullException(nameof(media));
        _bookmarks = bookmarks ?? throw new ArgumentNullException(nameof(bookmarks));

        // Wire the deleted-actor filter (139.3-F2) into every in-memory edge store: read paths exclude
        // edges whose source was a *locally-stored-then-deleted* actor (the EF sibling applies the same
        // filter at the SQL level). Edges from remote actors and from un-provisioned local actors are
        // kept (their source is not in the actor store's removed set). The predicate is a synchronous
        // ConcurrentDictionary read.
        var actorStore = actors;
        System.Func<Iri, bool> sourceExists = iri => actorStore.SourceSurvives(iri);
        _follows.SetSourceExistsPredicate(sourceExists);
        _likes.SetSourceExistsPredicate(sourceExists);
        _dislikes.SetSourceExistsPredicate(sourceExists);
        _announces.SetSourceExistsPredicate(sourceExists);
        _moderation.SetSourceExistsPredicate(sourceExists);
        _relays.SetSourceExistsPredicate(sourceExists);
        _communities.SetSourceExistsPredicate(sourceExists);
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
    public IDislikeStore Dislikes => _dislikes;

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

    /// <inheritdoc/>
    public IBookmarkStore Bookmarks => _bookmarks;

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
        _dislikes.Clear();
        _moderation.Clear();
        _relays.Clear();
        _objects.Clear();
        _creates.Clear();
        _communities.Clear();
        if (_media is InMemoryMediaStore concreteMedia)
        {
            concreteMedia.Clear();
        }
        if (_bookmarks is InMemoryBookmarkStore concreteBookmarks)
        {
            concreteBookmarks.Clear();
        }
    }
}
