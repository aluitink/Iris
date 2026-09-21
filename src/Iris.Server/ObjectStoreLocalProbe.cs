using Iris.Core.Identity;
using Iris.Server.Stores;

namespace Iris.Server;

/// <summary>
/// The production <see cref="ILocalObjectProbe"/>: delegates to the
/// <see cref="IObjectStore"/> for the exact-IRI existence check that keeps the reply-side dial-base
/// IRI normalization safe (a canonical-IRI miss is a no-op, so a genuinely foreign object on a
/// coincidentally local-looking host is never rewritten).
/// </summary>
internal sealed class ObjectStoreLocalProbe(IObjectStore objectStore) : ILocalObjectProbe
{
    /// <inheritdoc/>
    public Task<bool> TryGetAsync(Iri objectIri, CancellationToken ct)
        => objectStore.TryGetObjectAsync(objectIri, out _, ct);
}
