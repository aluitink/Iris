using System.Net;
using Iris.Core;

namespace Iris.Server.Http.Proxy;

/// <summary>
/// An <see cref="IProxyTargetPolicy"/> that allows a proxy target only when its host is on a
/// configured allowlist.
/// </summary>
/// <remarks>
/// The allowlist is a set of hostnames (e.g. <c>b.domain.local</c>). Matching is case-insensitive and
/// exact (no wildcards, no subdomain matching). An empty allowlist allows every target — the default,
/// so a host that does not configure an allowlist gets a working proxy out of the box and a production
/// host tightens it. A target with no host (a relative IRI, or one that does not parse to an absolute
/// <c>http</c>/<c>https</c> URI) is never allowed: the proxy forwards only to absolute web targets.
/// </remarks>
public sealed class AllowlistProxyTargetPolicy : IProxyTargetPolicy
{
    private readonly HashSet<string> _allowedHosts;

    /// <summary>
    /// Initializes a new allowlist policy.
    /// </summary>
    /// <param name="allowedHosts">The hostnames a proxy target may use (case-insensitive). An empty
    /// collection allows every target.</param>
    public AllowlistProxyTargetPolicy(IReadOnlyCollection<string>? allowedHosts)
    {
        _allowedHosts = allowedHosts is null
            ? []
            : [.. allowedHosts.Where(h => !string.IsNullOrWhiteSpace(h)).Select(h => h.Trim().ToLowerInvariant())];
    }

    /// <inheritdoc/>
    public Task<bool> TryAuthorizeAsync(Iri actorIri, Iri target, out string? reason, CancellationToken ct = default)
    {
        if (!Uri.TryCreate(target.Value, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
            || string.IsNullOrEmpty(uri.Authority))
        {
            reason = "Proxy targets must be absolute http(s) IRIs.";
            return Task.FromResult(false);
        }

        var host = uri.IdnHost.ToLowerInvariant();
        if (_allowedHosts.Count == 0 || _allowedHosts.Contains(host))
        {
            reason = null;
            return Task.FromResult(true);
        }

        reason = $"Target host '{uri.IdnHost}' is not in the proxy allowlist.";
        return Task.FromResult(false);
    }
}
