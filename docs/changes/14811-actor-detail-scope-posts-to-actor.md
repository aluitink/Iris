# 148.11 — Actor page "Posts" tab: scope to the actor's own content

> 2026-09-16 · Slice 148.11 · Phase 148 (UI follow-up)

## What was built

The actor detail page (`/actor?iri=…`) "Posts" tab rendered foreign content:
boosts of remote notes and community-relayed posts that the server had mirrored
into a local actor's outbox. The tab filtered with
`OutboxFilter.IsContentItem` — which keeps any `Create` (Note/Article) or
`Announce` regardless of who authored it. Because Iris's server deliberately
pollutes a member's outbox with inbound content (`CommunityInboxActivityHandler`
records external content in local members' outboxes; `AnnounceActivityHandler`
records inbound boosts in the recipient's outbox, for community/feed merging),
an unscoped content filter surfaced all of that on the actor's public profile.

The fix scopes the Posts tab to the actor's own content. `ActorDetail.razor` now
passes a per-actor filter, `OutboxItemFilter`, which delegates to
`OutboxFilter.IsOwnContentItem(item, ActorIri)` — the same author-scoped filter
the signed-in user's profile "Your posts" tab already uses
(`Profile.razor`). An item is shown only if it is a content item **and** its
`actor` matches the page's actor IRI; a mirrored/boosted item whose actor is the
remote author (or the booster) is filtered out. When the actor IRI is unavailable
the filter degrades gracefully to the unscoped behavior.

The server's outbox write/merge behavior is unchanged — it is intentional for
community and feed merging. The fix is purely client-side, matching the existing
profile-page treatment.

## Key types & files

- `apps/Iris.Web.Client/Components/Pages/ActorDetail.razor` — added
  `OutboxItemFilter(IObjectOrLink item) => OutboxFilter.IsOwnContentItem(item,
  ActorIri)` and pointed the outbox `PagedCollection`'s `ItemFilter` at it
  (was `OutboxFilter.IsContentItem`). `ActorIri` is the page's existing
  query-derived actor IRI.
- `apps/Iris.Web.Client/Components/OutboxFilter.cs` — unchanged;
  `IsOwnContentItem` already existed and is now reused here (previously only the
  profile page called it).

## Tests

No new coded tests (UI-filter change; `OutboxFilter` is `internal` and the loop
protocol for this phase is Playwright-driven, no new coded tests). Build verified:
`Iris.Web.Client` compiles clean (0 warnings, 0 errors). Re-verify from a clean
entry: open a local actor's profile whose outbox contains mirrored/boosted remote
content and confirm the Posts tab shows only that actor's own notes and their own
boosts — no foreign-authored notes and no boosts of others' notes.

## Decisions

- **Client-side filter, not server-side outbox cleanup.** The outbox pollution is
  by design (community outbox merging, inbound-boost recording). Removing or
  tagging those items server-side would break the community/feed merge and the
  `totalItems`/collection semantics other surfaces rely on. Filtering at the
  render boundary is the least-invasive fix and mirrors what the profile page
  already does.
- **Reuse `IsOwnContentItem` rather than a new filter.** It already implements the
  exact semantics needed (content item + actor IRI match, `null`-IRI fallback) and
  was previously underused (only the profile page). Reusing it keeps a single
  source of truth for "actor's own content."
- **Scope by the page's actor IRI, not the signed-in user's.** The actor page can
  be viewed for any actor (including signed-out visitors reading a public actor).
  Scoping by `ActorIri` (the actor whose page this is) is correct for both local
  and remote actors and for both signed-in and signed-out readers.
