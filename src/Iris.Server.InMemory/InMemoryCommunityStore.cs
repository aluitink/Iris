using Iris.Core;
using Iris.Server;
using KristofferStrube.ActivityStreams;

namespace Iris.Server.InMemory;

/// <summary>
/// An in-memory <see cref="ICommunityStore"/> backed by a concurrent dictionary.
/// </summary>
/// <remarks>
/// Ephemeral: communities vanish on restart. Thread-safe.
/// </remarks>
public sealed class InMemoryCommunityStore : ICommunityStore
{
    private readonly System.Collections.Concurrent.ConcurrentDictionary<Iri, Group> _communities = new();

    /// <inheritdoc/>
    public Task<bool> TryGetCommunityAsync(Iri communityIri, out Group? community, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var found = _communities.TryGetValue(communityIri, out community);
        return Task.FromResult(found);
    }

    /// <inheritdoc/>
    public Task PutCommunityAsync(Group community, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(community);
        if (string.IsNullOrWhiteSpace(community.Id))
        {
            throw new ArgumentException("Community must have a non-null Id.", nameof(community));
        }

        ct.ThrowIfCancellationRequested();
        _communities[new Iri(community.Id)] = community;
        return Task.CompletedTask;
    }
}
