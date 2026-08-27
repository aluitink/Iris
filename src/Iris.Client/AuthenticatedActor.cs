using KristofferStrube.ActivityStreams;

namespace Iris.Client;

/// <summary>
/// The result of authenticating a session: the owner's actor document (including the
/// owner-only <c>privateKey</c> property) plus the loaded private key material.
/// </summary>
/// <param name="Actor">The authenticated actor document, including its <c>privateKey</c> extension field.</param>
/// <param name="Key">The loaded private key. Owned by the caller; dispose when the session ends.</param>
public sealed record AuthenticatedActor(Actor Actor, Iris.Core.KeyPair Key);
