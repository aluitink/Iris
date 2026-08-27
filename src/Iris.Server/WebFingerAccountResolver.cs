using Iris.Client;
using Iris.Core;

namespace Iris.Server;

/// <summary>
/// The default <see cref="IAccountResolver"/>, backed by an <see cref="IWebFingerResolver"/> (the
/// <see cref="WebFingerClient"/>).
/// </summary>
/// <remarks>
/// Resolutions go through the Phase 3 <see cref="WebFingerCache"/> (by account IRI), so a remote
/// account is resolved once and reused across lookups within the cache's TTL. The <c>forceRefresh</c>
/// argument of <see cref="ResolveAsync"/> maps to the cache's <c>forceRefresh</c> escape hatch (the
/// <c>?refresh=true</c> bypass). An absent result (the resolver returned null) is not cached, so a
/// later lookup retries.
/// </remarks>
public sealed class WebFingerAccountResolver(IWebFingerResolver webFinger, WebFingerCache webFingerCache)
    : IAccountResolver
{
    private readonly IWebFingerResolver _webFinger = webFinger!;
    private readonly WebFingerCache _webFingerCache = webFingerCache!;

    /// <inheritdoc/>
    public async Task<Iri?> ResolveAsync(string account, bool forceRefresh = false, CancellationToken ct = default)
    {
        var subjectIri = new Iri(WebFingerClient.NormalizeSubject(account));

        var (value, _, _) = await _webFingerCache
            .GetAsync(
                subjectIri,
                forceRefresh,
                factory: iri => ResolveFromNetworkAsync(iri.Value, ct),
                ct)
            .ConfigureAwait(false);

        return value?.ActorId;
    }

    private async Task<WebFingerHit?> ResolveFromNetworkAsync(string subject, CancellationToken ct)
    {
        var actorIri = await _webFinger.ResolveActorAsync(subject, ct).ConfigureAwait(false);
        return actorIri is null ? null : new WebFingerHit(new Iri(subject), actorIri.Value);
    }
}
