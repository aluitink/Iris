using Iris.Core;
using KristofferStrube.ActivityStreams;

namespace Iris.Server.Services;

/// <summary>
/// Searches an instance's own (local) actors and content objects (F-13 global search / directory).
/// </summary>
/// <remarks>
/// The gap (F-13): only per-community search (<c>GET /c/{name}/search</c>) exists — a user cannot
/// discover actors or content instance-wide. This service is the instance-wide counterpart: it searches
/// the <em>local</em> surface this instance stores — the actors it hosts (its directory) and the content
/// objects it has stored (via the inbound <c>Create</c> path, including copies federated in from remote
/// authors) — so the server can serve a <c>GET /search</c> endpoint.
/// </remarks>
/// <para>
/// <strong>Search surface.</strong> A query matches:
/// <list type="bullet">
/// <item>a <em>local actor</em> (its <c>name</c>, <c>preferredUsername</c>, or IRI), and</item>
/// <item>a <em>stored content object</em> (its <c>content</c> or <c>name</c>), skipping
/// <see cref="Tombstone"/>s (a deleted object has no searchable content) and objects that are actors
/// (those are matched by the actor pass, not duplicated as content).</item>
/// </list>
/// Matching is a case-insensitive substring over the relevant string fields (the same shape as the
/// community search, <see cref="ICommunityFeedService.SearchCommunityAsync"/>). An empty/whitespace
/// query matches <em>all</em> actors and content objects (the endpoint then serves as an unfiltered
/// directory / listing).
/// </para>
/// <para>
/// <strong>Ordering.</strong> Results are deterministic: actors first, then content objects, each
/// sub-list sorted by IRI (ordinal). The endpoint slices the combined list into pages (the shared
/// <c>limit</c>/<c>offset</c> pagination shape).
/// </para>
/// <para>
/// <strong>Scope.</strong> This searches the instance's own store only. It does <em>not</em> query remote
/// instances (a cross-instance search would require a relay / WebFinger fan-out, which is out of scope
/// for F-13 — it matches the per-community search, which also searches only the local surface).
/// </para>
public interface IGlobalSearchService
{
    /// <summary>
    /// Searches the instance's local actors and content objects for <paramref name="query"/>.
    /// </summary>
    /// <param name="query">The search query (case-insensitive substring). An empty/whitespace query
    /// matches all actors and content objects.</param>
    /// <param name="type">An optional item-type filter (case-insensitive). When set (e.g.
    /// <c>"Actor"</c>), only items of that ActivityStreams type are returned — so the directory searches
    /// actors only (no content). When null/whitespace, both actors and content are returned.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <param name="localOnly">When true, the actor pass is restricted to this instance's own actors
    /// (the directory); a cached remote actor is excluded. When false (the default), the whole stored
    /// actor surface is searched. Content is unaffected (it is always the instance's stored content).</param>
    /// <param name="requesterIri">The requesting actor's IRI, or null for an anonymous / unsigned
    /// request. When set, non-public content (followers-only or direct) not addressed to that actor is
    /// excluded from the content results; when null, only public content is returned. Actors are
    /// unaffected (a directory entry is about a person, not a specific post). This is the
    /// audience/visibility filter (closes the Phase 136.18 / 139.2-s5 gap for the search surface). When
    /// set AND the implementation has a followed-feed service (S96 cross-instance post search), the
    /// content results additionally include the requester's followed <em>remote</em> posts that match the
    /// query (walked from the follows' outboxes over the wire), de-duplicated against the local content.
    /// When null (anonymous), the search is local-only (no cross-instance pass — there is no requester
    /// whose follows to walk).</param>
    /// <returns>A task that completes with the matching items (actors first, then content objects, each
    /// sub-list sorted by IRI). Each item is an <see cref="IObjectOrLink"/>; callers pattern-match
    /// (an <see cref="Actor"/> or a content <see cref="IObject"/>).</returns>
    public Task<IReadOnlyList<IObjectOrLink>> SearchAsync(string? query, CancellationToken ct = default, string? type = null, bool localOnly = false, Iri? requesterIri = null);

    /// <summary>
    /// Searches the instance's local actors and content objects for <paramref name="query"/> and returns
    /// a single page of the result plus the full match count (57.4 — pushes pagination into the search so
    /// a global search does not materialize every matching item in memory).
    /// </summary>
    /// <param name="query">The search query (case-insensitive substring). An empty/whitespace query
    /// matches all actors and content objects.</param>
    /// <param name="type">An optional item-type filter (case-insensitive). When set (e.g.
    /// <c>"Actor"</c>), only items of that ActivityStreams type are returned. When null/whitespace, both
    /// actors and content are returned.</param>
    /// <param name="limit">The maximum number of items to return (the page size).</param>
    /// <param name="offset">The number of matching items to skip before the page starts.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <param name="localOnly">When true, the actor pass is restricted to this instance's own actors
    /// (the directory); a cached remote actor is excluded. When false (the default), the whole stored
    /// actor surface is searched.</param>
    /// <param name="requesterIri">The requesting actor's IRI, or null for an anonymous / unsigned
    /// request. When set, non-public content (followers-only or direct) not addressed to that actor is
    /// excluded from the content results and the total; when null, only public content is returned.
    /// Actors are unaffected. This is the audience/visibility filter (closes the Phase 136.18 /
    /// 139.2-s5 gap for the search surface).</param>
    /// <returns>A task that completes with the page of matching items (actors first, then content objects,
    /// each sub-list sorted by IRI) and the full match total (for a search page's <c>totalItems</c>).</returns>
    public Task<(IReadOnlyList<IObjectOrLink> Items, int Total)> SearchPagedAsync(
        string? query,
        CancellationToken ct,
        string? type,
        int limit,
        int offset,
        bool localOnly = false,
        Iri? requesterIri = null);
}
