using Iris.Core.Identity;
using KristofferStrube.ActivityStreams;

namespace Iris.Server.Media;

/// <summary>
/// Pre-fetches (warms) the cross-origin media attachments of a stored object so the media proxy
/// (Phase 20.4 (d)) serves them instantly (eager-warm, ON by default).
/// </summary>
/// <remarks>
/// A seam so the inbound store path (the <c>Create</c> handler, followed/community content, synced
/// peers) can warm an object's attachments without depending on the concrete fetcher/store. The warm
/// is best-effort and non-blocking in spirit: a failure (a dead URL, a fetch error) is never thrown —
/// it is a no-op (the proxy still fetches lazily on the first hit). Only <em>cross-origin</em>
/// attachments (whose URL's host is not the instance's own base host) are warmed; same-origin
/// attachments (the instance's own <c>/ap/v1/media/{id}</c>) are already served locally and need no
/// warm.
/// </remarks>
public interface IMediaWarmer
{
    /// <summary>
    /// Warms the cross-origin media attachments of a stored object.
    /// </summary>
    /// <param name="obj">The stored object whose <c>attachment</c> media is warmed (may be null).</param>
    /// <param name="instanceBase">The instance's base IRI (e.g. <c>https://a.test</c>); an attachment
    /// whose URL is on this host is same-origin and skipped.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <remarks>
    /// Best-effort: never throws. A null or attachment-less object is a no-op. Eager-warm may be
    /// disabled (<see cref="MediaOptions.EagerWarm"/> = <see langword="false"/>), in which case this
    /// is a no-op (the proxy still fetches lazily on the first hit).
    /// </remarks>
    public Task WarmAsync(IObject? obj, Iri instanceBase, CancellationToken ct = default);
}
