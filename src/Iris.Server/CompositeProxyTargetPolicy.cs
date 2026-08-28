using Iris.Core;

namespace Iris.Server;

/// <summary>
/// An <see cref="IProxyTargetPolicy"/> that composes a sequence of policies: the target is allowed
/// only when every composed policy allows it.
/// </summary>
/// <remarks>
/// The default proxy composition is an allowlist (which hosts may be reached) followed by a per-actor
/// rate limit (how often). Policies are consulted in the order given; the first rejection wins and its
/// <see cref="IProxyTargetPolicy.TryAuthorizeAsync"/> reason is propagated. Ordering matters for the
/// rate limiter: it counts a request only when consulted (i.e. after the allowlist passed), so a
/// request to a disallowed host does not consume the actor's rate-limit budget.
/// </remarks>
public sealed class CompositeProxyTargetPolicy : IProxyTargetPolicy
{
    private readonly IReadOnlyList<IProxyTargetPolicy> _policies;

    /// <summary>
    /// Initializes a new composite policy over the given policies (consulted in order).
    /// </summary>
    /// <param name="policies">The policies to compose. At least one is required.</param>
    public CompositeProxyTargetPolicy(IReadOnlyList<IProxyTargetPolicy>? policies)
    {
        _policies = policies is { Count: > 0 }
            ? policies
            : throw new ArgumentException("At least one policy is required.", nameof(policies));
    }

    /// <inheritdoc/>
    public Task<bool> TryAuthorizeAsync(Iri actorIri, Iri target, out string? reason, CancellationToken ct = default)
    {
        // The interface method carries an out parameter, which async methods forbid, so the loop is
        // synchronous-over-async: each composed policy is awaited on the thread-pool (the default
        // policies — allowlist + in-memory rate limit — are synchronous, so this never blocks).
        foreach (var policy in _policies)
        {
            if (!policy.TryAuthorizeAsync(actorIri, target, out var policyReason, ct)
                    .GetAwaiter().GetResult())
            {
                reason = policyReason;
                return Task.FromResult(false);
            }
        }

        reason = null;
        return Task.FromResult(true);
    }
}
