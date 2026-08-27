using Iris.Core;
using KristofferStrube.ActivityStreams;

namespace Iris.Server;

/// <summary>
/// An <see cref="IActorDocumentFetcher"/> that always returns null. The fallback when an instance has
/// no configured <see cref="ActivityPubServerOptions.InstanceActorId"/> (it cannot sign outbound
/// fetches, so it cannot resolve remote actor documents).
/// </summary>
/// <remarks>
/// Inbound signature validation degrades gracefully: remote keys cannot be resolved, so remote
/// signatures fail validation (401). This is the safe default — an instance that has not configured
/// its federation identity does not accidentally trust remote keys.
/// </remarks>
public sealed class NoopActorDocumentFetcher : IActorDocumentFetcher
{
    /// <inheritdoc/>
    public Task<Actor?> GetActorAsync(Iri actorIri, CancellationToken ct = default)
        => Task.FromResult<Actor?>(null);
}
