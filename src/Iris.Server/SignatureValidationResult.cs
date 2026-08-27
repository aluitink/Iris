using Iris.Core;

namespace Iris.Server;

/// <summary>
/// The outcome of validating an inbound ActivityPub request's HTTP signature.
/// </summary>
/// <param name="IsValid">Whether the cryptographic signature verified.</param>
/// <param name="KeyId">The IRI of the key that signed the request (the <c>keyId</c> in the
/// <c>Signature</c> header).</param>
/// <param name="ActorIri">The actor IRI the signature is bound to, resolved from the request body's
/// <c>actor</c> field when present and parseable; otherwise null.</param>
/// <remarks>
/// A non-null result means the request carried a <c>Signature</c> header and it was evaluated. A
/// null result (from <see cref="ISignatureValidator.ValidateAsync(Microsoft.AspNetCore.Http.HttpContext, CancellationToken)"/>)
/// means the request was unsigned and the caller decides the policy (e.g. allow unauthenticated GETs,
/// reject unsigned inbox POSTs).
/// </remarks>
public sealed record SignatureValidationResult(bool IsValid, Iri KeyId, Iri? ActorIri)
{
    /// <summary>
    /// The sentinel outcome for an unsigned request (no <c>Signature</c> header). Handlers use this
    /// to distinguish "no signature" from a present-but-invalid signature.
    /// </summary>
    public static SignatureValidationResult None { get; } = new(IsValid: false, KeyId: default, ActorIri: null);
}
