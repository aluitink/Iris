using Iris.Core;
using Microsoft.AspNetCore.Http;

namespace Iris.Server.Security;

/// <summary>
/// ASP.NET Core middleware that validates the HTTP signature on inbound ActivityPub requests and
/// stores the outcome on the <see cref="HttpContext.Items"/> for downstream handlers.
/// </summary>
/// <remarks>
/// The middleware is deliberately permissive about *which* routes require a signature: it validates
/// any request that carries a <c>Signature</c> header and always stores the outcome (or a
/// "no signature" marker). Route-level policy (e.g. "inbox POSTs require a valid signature") is
/// applied by the endpoint handler, which reads the outcome via
/// <see cref="GetResult(HttpContext)"/>. This keeps the middleware simple and lets each endpoint
/// decide its own auth policy (GETs may be anonymous, inbox POSTs must be signed).
/// </remarks>
public sealed class SignatureValidationMiddleware(RequestDelegate next)
{
    private readonly RequestDelegate _next = next;

    /// <summary>
    /// The <see cref="HttpContext.Items"/> key under which the validation outcome is stored.
    /// The value is a <see cref="SignatureValidationResult"/> (signed request) or
    /// <see cref="SignatureValidationResult.None"/> (unsigned request).
    /// </summary>
    public const string OutcomeItemKey = "iris.signature-validation";

    /// <inheritdoc/>
    public async Task InvokeAsync(HttpContext context, ISignatureValidator validator)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(validator);

        // Middleware Invoke* parameters other than HttpContext are resolved from DI; a
        // CancellationToken is not a DI service, so the request-aborted token is taken from the
        // context instead (the standard middleware pattern).
        var ct = context.RequestAborted;

        SignatureValidationResult outcome;
        // Only POST requests are signature-validated here. GETs (object/actor-document fetches,
        // including the key-resolution bootstrap the inbound validator performs) are left to their
        // endpoints' own policy. Validating GETs in the middleware would recurse in a federating loop:
        // the inbound key resolver's key-resolution fetch is itself a signed request, and in a
        // two-instance setup each instance's fetcher routes to the other, so validating those GETs
        // bounces the resolution across instances until the request's cancellation token fires (the
        // inbox POST's validation is then canceled and the activity drops).
        //
        // The object-document handler serves the per-requester iris:isLiked extension from a signed
        // GET's authenticated identity. It validates the signature inline (calling the validator
        // directly) rather than relying on the middleware, because the object-document catch-all is
        // only reached for NON-actor-document paths (actor documents are dispatched by their own
        // /u/{handle} route first) — so an inline validation there never re-enters on a
        // key-resolution fetch, and the loop is broken.
        if (HttpMethods.Post == context.Request.Method
            && context.Request.Headers.ContainsKey(Signatures.SignatureHeaderName))
        {
            var result = await validator.ValidateAsync(context, ct).ConfigureAwait(false);
            outcome = result ?? SignatureValidationResult.None;
        }
        else
        {
            // Unsigned or non-POST: don't buffer or evaluate; record the marker so handlers can
            // apply policy.
            outcome = SignatureValidationResult.None;
        }

        context.Items[OutcomeItemKey] = outcome;
        await _next(context).ConfigureAwait(false);
    }

    /// <summary>
    /// Reads the validation outcome stored by the middleware, or <see cref="SignatureValidationResult.None"/>
    /// when the middleware did not run (e.g. the request bypassed it).
    /// </summary>
    /// <param name="context">The HTTP context.</param>
    /// <returns>The outcome, or <see cref="SignatureValidationResult.None"/> when absent.</returns>
    public static SignatureValidationResult GetResult(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return context.Items.TryGetValue(OutcomeItemKey, out var value)
            && value is SignatureValidationResult result
            ? result
            : SignatureValidationResult.None;
    }
}
