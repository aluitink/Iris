using Iris.Core;

namespace Iris.Server;

/// <summary>
/// The default <see cref="ILocalActorResolver"/>: an actor is local when it is present in the
/// instance's actor store.
/// </summary>
/// <remarks>
/// The actor store holds the instance's own actors (the documents served by the actor document
/// endpoint). A remote actor is never in the local store, so its IRI resolves to
/// <see langword="false"/>. The resolver takes the aggregate <see cref="IPersistenceProvider"/>
/// (which the host registers) and reaches the actor store through it, so it is resolvable from the
/// same DI container as the rest of the server.
/// </remarks>
public sealed class DefaultLocalActorResolver : ILocalActorResolver
{
    private readonly IPersistenceProvider _persistence;

    /// <summary>
    /// Initializes a new <see cref="DefaultLocalActorResolver"/>.
    /// </summary>
    /// <param name="persistence">The persistence provider (its actor store is consulted).</param>
    /// <exception cref="ArgumentNullException">When <paramref name="persistence"/> is null.</exception>
    public DefaultLocalActorResolver(IPersistenceProvider persistence)
    {
        ArgumentNullException.ThrowIfNull(persistence);
        _persistence = persistence;
    }

    /// <inheritdoc/>
    public Task<bool> IsLocalActorAsync(Iri actorIri, CancellationToken ct = default)
        => _persistence.Actors.TryGetActorAsync(actorIri, out _, ct);
}
