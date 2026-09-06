using Iris.Server.Stores;

namespace Iris.Server.Data;

/// <summary>
/// The EF Core (PostgreSQL) <see cref="IPersistenceProvider"/>: the aggregate persistence boundary for
/// the production server. Bundles the per-concern EF Core stores (each backed by the
/// <see cref="IrisDbContext"/> factory) behind one interface, so a host registers a single
/// <c>AddEntityFrameworkPersistence()</c> and the server resolves a cohesive persistence surface.
/// </summary>
public sealed class EntityFrameworkPersistenceProvider : IPersistenceProvider
{
    private readonly EdgeStore _edges;

    /// <summary>
    /// Initializes the provider over its stores.
    /// </summary>
    /// <param name="actors">The actor store. Must not be null.</param>
    /// <param name="activities">The activity store. Must not be null.</param>
    /// <param name="follows">The follow store. Must not be null.</param>
    /// <param name="likes">The like store. Must not be null.</param>
    /// <param name="replies">The reply store. Must not be null.</param>
    /// <param name="announces">The announce store. Must not be null.</param>
    /// <param name="moderation">The moderation store. Must not be null.</param>
    /// <param name="relays">The relay store. Must not be null.</param>
    /// <param name="objects">The object store. Must not be null.</param>
    /// <param name="creates">The create index. Must not be null.</param>
    /// <param name="communities">The community store. Must not be null.</param>
    /// <param name="keys">The key store. Must not be null.</param>
    /// <param name="media">The media store. Must not be null.</param>
    /// <param name="edges">The shared edge store (owned by the provider; not exposed publicly). Must not be null.</param>
    public EntityFrameworkPersistenceProvider(
        IActorStore actors,
        IActivityStore activities,
        IFollowStore follows,
        ILikeStore likes,
        IReplyStore replies,
        IAnnounceStore announces,
        IModerationStore moderation,
        IRelayStore relays,
        IObjectStore objects,
        ICreateIndex creates,
        ICommunityStore communities,
        IKeyStore keys,
        IMediaStore media,
        EdgeStore edges)
    {
        Actors = actors ?? throw new ArgumentNullException(nameof(actors));
        Activities = activities ?? throw new ArgumentNullException(nameof(activities));
        Follows = follows ?? throw new ArgumentNullException(nameof(follows));
        Likes = likes ?? throw new ArgumentNullException(nameof(likes));
        Replies = replies ?? throw new ArgumentNullException(nameof(replies));
        Announces = announces ?? throw new ArgumentNullException(nameof(announces));
        Moderation = moderation ?? throw new ArgumentNullException(nameof(moderation));
        Relays = relays ?? throw new ArgumentNullException(nameof(relays));
        Objects = objects ?? throw new ArgumentNullException(nameof(objects));
        Creates = creates ?? throw new ArgumentNullException(nameof(creates));
        Communities = communities ?? throw new ArgumentNullException(nameof(communities));
        Keys = keys ?? throw new ArgumentNullException(nameof(keys));
        Media = media ?? throw new ArgumentNullException(nameof(media));
        _edges = edges ?? throw new ArgumentNullException(nameof(edges));
    }

    /// <inheritdoc/>
    public IActorStore Actors { get; }

    /// <inheritdoc/>
    public IActivityStore Activities { get; }

    /// <inheritdoc/>
    public IFollowStore Follows { get; }

    /// <inheritdoc/>
    public ILikeStore Likes { get; }

    /// <inheritdoc/>
    public IReplyStore Replies { get; }

    /// <inheritdoc/>
    public IAnnounceStore Announces { get; }

    /// <inheritdoc/>
    public IModerationStore Moderation { get; }

    /// <inheritdoc/>
    public IRelayStore Relays { get; }

    /// <inheritdoc/>
    public IObjectStore Objects { get; }

    /// <inheritdoc/>
    public ICreateIndex Creates { get; }

    /// <inheritdoc/>
    public ICommunityStore Communities { get; }

    /// <inheritdoc/>
    public IKeyStore Keys { get; }

    /// <inheritdoc/>
    public IMediaStore Media { get; }
}
