using Iris.Core.Identity;
using Iris.Server.Stores;

namespace Iris.Server.Persistance;

/// <summary>
/// A file-backed <see cref="IPersistenceProvider"/> (Phase 16.4, production persistence): bundles the
/// file-backed stores so every store (actors, activities, follows, likes, replies, moderation, relays,
/// objects, communities) and the local instance's signing keys survive a host restart.
/// </summary>
/// <remarks>
/// Each store owns its own JSON file under the provider's directory (see the individual
/// <see cref="Persistance"/> store classes for the per-file format and durability model). The files are
/// created on first write; the directory must already exist. Because the aggregate is opt-in (a host
/// calls <c>UseFileBackedPersistence(directory)</c> rather than <c>AddActivityPubServer()</c>), the
/// default in-memory provider is unchanged.
/// </remarks>
/// <remarks>
/// The aggregate is <see cref="IDisposable"/>: when constructed over a directory (the common case) it
/// owns the underlying <see cref="FilePersistence"/> instances and disposes their locks on
/// <see cref="Dispose"/>. When constructed over pre-built stores (the test case), it disposes the stores
/// that are <see cref="IDisposable"/>.
/// </remarks>
public sealed class FileBackedPersistenceProvider : IPersistenceProvider, IDisposable
{
    private readonly IActorStore _actors;
    private readonly IActivityStore _activities;
    private readonly IFollowStore _follows;
    private readonly ILikeStore _likes;
    private readonly IAnnounceStore _announces;
    private readonly IReplyStore _replies;
    private readonly IModerationStore _moderation;
    private readonly IRelayStore _relays;
    private readonly IObjectStore _objects;
    private readonly ICreateIndex _creates;
    private readonly ICommunityStore _communities;
    private readonly IKeyStore _keys;
    private readonly IMediaStore _media;

    /// <summary>
    /// Initializes a new file-backed provider with one JSON file per store under
    /// <paramref name="directory"/>. The directory must already exist.
    /// </summary>
    /// <param name="directory">The directory that holds the per-store files. Created on first write;
    /// the directory itself must already exist.</param>
    /// <exception cref="ArgumentNullException">When <paramref name="directory"/> is null or empty.</exception>
    public FileBackedPersistenceProvider(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);

        _actors = new FileBackedActorStore(Path.Combine(directory, "actors.json"));
        _activities = new FileBackedActivityStore(Path.Combine(directory, "activities.json"));
        _follows = new FileBackedFollowStore(Path.Combine(directory, "follows.json"));
        _likes = new FileBackedLikeStore(Path.Combine(directory, "likes.json"));
        _announces = new FileBackedAnnounceStore(Path.Combine(directory, "announces.json"));
        _replies = new FileBackedReplyStore(Path.Combine(directory, "replies.json"));
        _moderation = new FileBackedModerationStore(Path.Combine(directory, "moderation.json"));
        _relays = new FileBackedRelayStore(Path.Combine(directory, "relays.json"));
        _objects = new FileBackedObjectStore(Path.Combine(directory, "objects.json"));
        _creates = new FileBackedCreateIndex(Path.Combine(directory, "creates.json"));
        _communities = new FileBackedCommunityStore(Path.Combine(directory, "communities.json"));
        _keys = new FileBackedKeyStore(Path.Combine(directory, "keys.json"));
        _media = new FileBackedMediaStore(Path.Combine(directory, "media.json"));
    }

    /// <summary>
    /// Initializes a new provider over the given stores (used by tests to pre-seed data or to point at
    /// specific files).
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
    public FileBackedPersistenceProvider(
        IActorStore actors,
        IActivityStore activities,
        IFollowStore follows,
        ILikeStore likes,
        IAnnounceStore announces,
        IReplyStore replies,
        IModerationStore moderation,
        IRelayStore relays,
        IObjectStore objects,
        ICreateIndex creates,
        ICommunityStore communities,
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
    /// Releases the file locks of the underlying stores. The per-store files on disk are left in place
    /// (the data is durable); this only frees the <see cref="FilePersistence"/> locks that serialize
    /// reads/writes.
    /// </summary>
    public void Dispose()
    {
        DisposeIf(_actors);
        DisposeIf(_activities);
        DisposeIf(_follows);
        DisposeIf(_likes);
        DisposeIf(_announces);
        DisposeIf(_replies);
        DisposeIf(_moderation);
        DisposeIf(_relays);
        DisposeIf(_objects);
        DisposeIf(_communities);
        DisposeIf(_keys);
        DisposeIf(_media);
    }

    private static void DisposeIf(object store)
    {
        if (store is IDisposable disposable)
        {
            disposable.Dispose();
        }
    }
}
