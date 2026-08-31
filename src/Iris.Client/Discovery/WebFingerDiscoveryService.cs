using Iris.Core;

namespace Iris.Client.Discovery;

/// <summary>
/// An <see cref="IDiscoveryService"/> backed by a <see cref="WebFingerClient"/>.
/// </summary>
public sealed class WebFingerDiscoveryService : IDiscoveryService
{
    private readonly WebFingerClient _webFinger;

    /// <summary>
    /// Initializes a new <see cref="WebFingerDiscoveryService"/>.
    /// </summary>
    /// <param name="webFinger">The WebFinger client used for lookups.</param>
    public WebFingerDiscoveryService(WebFingerClient webFinger)
    {
        _webFinger = webFinger ?? throw new ArgumentNullException(nameof(webFinger));
    }

    /// <inheritdoc/>
    public Task<Iri?> ResolveActorAsync(string account, string dialScheme = "https", CancellationToken ct = default)
        => _webFinger.ResolveActorAsync(account, dialScheme, ct);

    /// <inheritdoc/>
    public Task<Iri?> ResolveActorAsync(string account, Uri dialBaseUri, CancellationToken ct = default)
        => _webFinger.ResolveActorAsync(account, dialBaseUri, ct);
}
