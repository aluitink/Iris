using Iris.Core;
using KristofferStrube.ActivityStreams;

namespace Iris.Server;

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
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A task that completes with the matching items (actors first, then content objects, each
    /// sub-list sorted by IRI). Each item is an <see cref="IObjectOrLink"/>; callers pattern-match
    /// (an <see cref="Actor"/> or a content <see cref="IObject"/>).</returns>
    public Task<IReadOnlyList<IObjectOrLink>> SearchAsync(string? query, CancellationToken ct = default);
}
