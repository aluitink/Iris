namespace Iris.Client;

/// <summary>
/// Options for a collection enumeration request.
/// </summary>
/// <param name="Limit">The maximum total number of items to enumerate, or null for no limit (follow all pages).</param>
/// <param name="BypassCache">When true, the client skips the collection-page cache for the reads (forces a re-fetch).</param>
/// <remarks>
/// All Iris collections share one <c>limit</c>/<c>offset</c>-style pagination shape
/// (Resolved Decision #6). v1 exposes <see cref="Limit"/> and <see cref="BypassCache"/>; an
/// <c>offset</c> is not part of the forward-follow enumeration (which starts at the collection's
/// <c>first</c> page and follows <c>next</c> links).
/// </remarks>
public sealed record CollectionQuery(int? Limit = null, bool BypassCache = false);
