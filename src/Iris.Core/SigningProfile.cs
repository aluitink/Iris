namespace Iris.Core;

/// <summary>
/// The set of request headers covered by an ActivityPub HTTP signature. Two profiles exist
/// because browser <c>fetch</c>/XHR (Blazor WASM) can only set a restricted header set.
/// </summary>
/// <remarks>
/// <see cref="ClientToServer"/> signs only <c>(request-target) host date</c> — the headers a
/// browser may set. <see cref="ServerToServer"/> additionally signs <c>digest content-type</c>
/// for request-body integrity. The verifier accepts either, reconstructing the signature base
/// from the <c>headers</c> list actually present in the <c>Signature</c> header.
/// </remarks>
public enum SigningProfile
{
    /// <summary>
    /// Restricted profile for browser-based clients: <c>(request-target) host date</c>.
    /// </summary>
    ClientToServer = 0,

    /// <summary>
    /// Full profile for server-to-server delivery:
    /// <c>(request-target) host date digest content-type</c>.
    /// </summary>
    ServerToServer = 1,
}
