using Iris.Core;
using KristofferStrube.ActivityStreams;

namespace Iris.Client.Auth;

/// <summary>
/// The result of authenticating a session: the owner's actor document (including the
/// owner-only <c>privateKey</c> property) plus the loaded private key material.
/// </summary>
/// <param name="Actor">The authenticated actor document, including its <c>privateKey</c> extension field.</param>
/// <param name="Key">The loaded private key (an RSA / EC <see cref="KeyPair"/> or an Ed25519
/// <see cref="Ed25519Key"/>). Owned by the caller; dispose when the session ends (when it is
/// <see cref="IDisposable"/>).</param>
public sealed record AuthenticatedActor(Actor Actor, ISigningKey Key);
