using Iris.Core;
using KristofferStrube.ActivityStreams;

namespace Iris.Server;

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
}
