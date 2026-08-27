using Iris.Core;

namespace Iris.Client;

/// <summary>
/// A cached WebFinger resolution result: the account IRI that was resolved and the actor IRI it
/// resolved to.
/// </summary>
/// <param name="Account">The account IRI (the cache key).</param>
/// <param name="ActorId">The resolved actor IRI.</param>
public sealed record WebFingerHit(Iri Account, Iri ActorId);
