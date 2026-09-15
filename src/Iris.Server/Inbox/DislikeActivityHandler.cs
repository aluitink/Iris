using Iris.Core;
using Iris.Server.Stores;
using KristofferStrube.ActivityStreams;
using Microsoft.Extensions.Logging;

namespace Iris.Server.Inbox;

/// <summary>
/// Handles inbound <see cref="Dislike"/> activities: records the dislike edge when the
/// <em>disliked object</em> is stored locally (so the object's dislike count is served), mirroring
/// <see cref="LikeActivityHandler"/>. Used for Lemmy-style downvotes.
/// </summary>
public sealed class DislikeActivityHandler : ActivityHandlerBase<Dislike>
{
    private readonly IPersistenceProvider _persistence;
    private readonly ILocalActorResolver _localActors;

    /// <summary>
    /// Initializes a new <see cref="DislikeActivityHandler"/>.
    /// </summary>
    /// <param name="persistence">The persistence provider (provides the <see cref="IDislikeStore"/>,
    /// <see cref="IObjectStore"/>, and <see cref="ICommunityStore"/>).</param>
    /// <param name="localActors">Resolves whether the recipient is a local actor.</param>
    /// <param name="logger">The logger (records the handler outcome). May be null.</param>
    /// <exception cref="ArgumentNullException">When any argument is null.</exception>
    public DislikeActivityHandler(
        IPersistenceProvider persistence,
        ILocalActorResolver localActors,
        ILogger<DislikeActivityHandler>? logger = null)
        : base(logger)
    {
        ArgumentNullException.ThrowIfNull(persistence);
        ArgumentNullException.ThrowIfNull(localActors);
        _persistence = persistence;
        _localActors = localActors;
    }

    /// <inheritdoc/>
    public override async Task HandleAsync(InboxDelivery delivery, Dislike dislike, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(delivery);
        ArgumentNullException.ThrowIfNull(dislike);

        var dislikerIri = dislike.Actor?.FirstOrDefault().ResolveObjectIri();
        if (!dislikerIri.HasValue)
        {
            return;
        }

        var objectIri = dislike.Object?.FirstOrDefault().ResolveObjectIri();
        if (!objectIri.HasValue)
        {
            return;
        }

        if (await _persistence.Objects.TryGetObjectAsync(objectIri.Value, out _, ct).ConfigureAwait(false))
        {
            await _persistence.Dislikes
                .RecordDislikeAsync(dislikerIri.Value, objectIri.Value, ct)
                .ConfigureAwait(false);
        }

        if (await _persistence.Communities
                .TryGetCommunityAsync(delivery.RecipientIri, out _, ct)
                .ConfigureAwait(false))
        {
            await CommunityContentRecorder.RecordToMembersAsync(
                _persistence,
                _localActors,
                delivery.RecipientIri,
                dislike,
                ct).ConfigureAwait(false);
        }
    }
}
