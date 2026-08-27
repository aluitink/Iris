using Iris.Core;
using KristofferStrube.ActivityStreams;

namespace Iris.Server.Tests;

/// <summary>
/// An <see cref="IActorDocumentFetcher"/> that defers resolution of its inner fetcher until the first
/// request. Used to break the A↔B federation wiring chicken-and-egg (A's fetcher needs B's
/// <c>TestServer</c> handler; B's delivery transport needs A's handler) — both servers exist by the
/// time any request flows.
/// </summary>
/// <remarks>
/// Unlike the plain <see cref="IrisActorDocumentFetcher"/> (which casts a fetched object to
/// <see cref="Actor"/>), this also recognizes <see cref="Group"/> documents: a community is a
/// <see cref="Group"/> (a <see cref="Group"/> is a remote <c>actor</c> for follow/signature purposes),
/// so key resolution for a community-as-follower (or -followee) succeeds. The cast is
/// <c>value as Actor ?? value as Group</c>, which is a no-op for a <see cref="Person"/> and returns the
/// <see cref="Group"/> for a community.
/// </remarks>
public sealed class LazyActorDocumentFetcher(Func<IActorDocumentFetcher> innerFactory) : IActorDocumentFetcher
{
    private readonly Func<IActorDocumentFetcher> _innerFactory = innerFactory;
    private IActorDocumentFetcher? _inner;

    /// <inheritdoc/>
    public Task<Actor?> GetActorAsync(Iri actorIri, CancellationToken ct = default)
    {
        _inner ??= _innerFactory();
        return _inner.GetActorAsync(actorIri, ct);
    }
}
