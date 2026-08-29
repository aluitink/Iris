using Microsoft.AspNetCore.Http;

namespace Iris.Server.Security;

/// <summary>
/// Validates the HTTP signature on an inbound ActivityPub request.
/// </summary>
/// <remarks>
/// This is the server-side (inbound) counterpart to the client's <c>SigningHandler</c>. It reads the
/// <c>Signature</c> header, resolves the signing key via an <see cref="IInboundKeyResolver"/> (the key
/// is a *remote* public key, not in the local key store), and verifies the signature cryptographically
/// via <see cref="Iris.Core.Signing.ISignatureVerifier"/>. It accepts both the
/// <see cref="Iris.Core.Signing.SigningProfile.ClientToServer"/> and
/// <see cref="Iris.Core.Signing.SigningProfile.ServerToServer"/> profiles (the verifier reconstructs the
/// signature base from the <c>headers</c> list actually present).
/// </remarks>
public interface ISignatureValidator
{
    /// <summary>
    /// Validates the signature on the given request.
    /// </summary>
    /// <param name="context">The HTTP context. The request body is buffered so downstream handlers can
    /// re-read it.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>
    /// A <see cref="SignatureValidationResult"/> when the request carried a <c>Signature</c> header
    /// (valid or not), or null when the request was unsigned (the caller decides the policy — e.g.
    /// allow unauthenticated GETs, reject unsigned inbox POSTs).
    /// </returns>
    /// <remarks>
    /// A null result means *unsigned*, not *invalid*. A non-null result with <c>IsValid == false</c>
    /// means the signature was present but failed (unknown key, bad crypto, or the actor binding
    /// could not be established).
    /// </remarks>
    public ValueTask<SignatureValidationResult?> ValidateAsync(HttpContext context, CancellationToken ct = default);
}
