using Iris.Core.Identity;
using Iris.Server.Stores;
using KristofferStrube.ActivityStreams;
using Microsoft.Extensions.Options;

namespace Iris.Server.Media;

/// <summary>
/// The default <see cref="IMediaWarmer"/> (Phase 20.4 (d)): best-effort, pre-fetches a stored object's
/// cross-origin media attachments through the media fetcher into the media store, so the media proxy
/// serves them instantly.
/// </summary>
/// <remarks>
/// A warm is idempotent (the media store's <c>PutBySourceUrlAsync</c> is a no-op re-store for a URL
/// already stored) and non-fatal: any failure (a dead URL, a fetch error) is swallowed for that
/// attachment and the warm continues with the next — a warm is a cache-prepopulation, not a
/// correctness requirement (the proxy fetches lazily on the first hit if a warm is missed). Eager-warm
/// may be disabled (<see cref="MediaOptions.EagerWarm"/> = <see langword="false"/>), in which case the
/// warm is a no-op.
/// </remarks>
public sealed class DefaultMediaWarmer : IMediaWarmer
{
    private readonly IMediaFetcher _fetcher;
    private readonly IPersistenceProvider _persistence;
    private readonly IOptions<ActivityPubServerOptions> _options;

    /// <summary>
    /// Initializes a new media warmer.
    /// </summary>
    /// <param name="fetcher">The media fetcher (fetches a remote attachment URL's bytes).</param>
    /// <param name="persistence">The persistence provider (provides the <see cref="IMediaStore"/>).</param>
    /// <param name="options">The server options (the <c>EagerWarm</c> toggle and the instance base
    /// IRI, used to classify an attachment as same-origin).</param>
    /// <exception cref="ArgumentNullException">When any argument is null.</exception>
    public DefaultMediaWarmer(
        IMediaFetcher fetcher,
        IPersistenceProvider persistence,
        IOptions<ActivityPubServerOptions> options)
    {
        _fetcher = fetcher ?? throw new ArgumentNullException(nameof(fetcher));
        _persistence = persistence ?? throw new ArgumentNullException(nameof(persistence));
        _options = options ?? throw new ArgumentNullException(nameof(options));
    }

    /// <inheritdoc/>
    public async Task WarmAsync(IObject? obj, Iri instanceBase, CancellationToken ct = default)
    {
        // Eager-warm disabled → no-op (the proxy still fetches lazily on the first hit).
        if (_options.Value.Media is { EagerWarm: false })
        {
            return;
        }

        if (obj is null)
        {
            return;
        }

        var instanceHost = SafeHost(instanceBase);
        foreach (var (mediaIri, _) in obj.GetMediaAttachments())
        {
            // A same-origin attachment (the instance's own /ap/v1/media/{id}) is already served
            // locally; no warm needed. Only cross-origin URLs are warmed.
            if (mediaIri.IsAbsolute && SafeHost(mediaIri) == instanceHost)
            {
                continue;
            }

            try
            {
                var fetched = await _fetcher.FetchAsync(mediaIri, ct).ConfigureAwait(false);
                if (fetched is not null)
                {
                    await _persistence.Media
                        .PutBySourceUrlAsync(mediaIri, fetched.Content, fetched.ContentType, instanceBase, ct)
                        .ConfigureAwait(false);
                }
            }
            catch (Exception)
            {
                // Best-effort: a warm failure for one attachment never aborts the warm or the store
                // path. The proxy fetches lazily on the first hit if this warm is missed.
            }
        }
    }

    /// <summary>
    /// Safely extracts the host of an IRI (empty string when the IRI is relative or has no host).
    /// </summary>
    private static string SafeHost(Iri iri)
    {
        if (!iri.IsAbsolute)
        {
            return string.Empty;
        }

        try
        {
            return iri.Uri.Host;
        }
        catch (UriFormatException)
        {
            return string.Empty;
        }
    }
}
