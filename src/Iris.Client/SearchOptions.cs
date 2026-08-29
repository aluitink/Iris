namespace Iris.Client;

/// <summary>
/// Options for a global-search request (F-13).
/// </summary>
/// <param name="Limit">The maximum number of items to return (the <c>?limit</c> query parameter).
/// Defaults to 100 (the server's default page size).</param>
/// <param name="BypassCache">Reserved for symmetry with <see cref="CollectionQuery"/>; a search is a
/// fresh query (not a stable collection), so the client does not cache the response regardless.</param>
/// <param name="Offset">The zero-based item offset to start from (the <c>?offset</c> query parameter;
/// default 0, the first result page).</param>
/// <remarks>
/// Global search uses the <c>limit</c>/<c>offset</c> pagination shape (Resolved Decision #6) rather than
/// the forward-follow <c>first</c>/<c>next</c> shape of a stable collection, so a search request fetches a
/// single page of up to <see cref="Limit"/> items at <see cref="Offset"/>.
/// </remarks>
public sealed record SearchOptions(int? Limit = null, bool BypassCache = false, int Offset = 0);
