# Phase 144 — General UI/UX Review and Improvements

**Date:** 2026-09-16
**Status:** In progress (findings cataloged; first fixes shipped)

## Method

Systematic Playwright walkthrough of every major page while signed in as `andrew`:
home timeline, profile (all tabs), notifications (all filters), compose, communities,
directory, search, settings, actor detail, community detail, object detail.

## Findings

| ID | Severity | Page | Description | Status |
|----|----------|------|-------------|--------|
| F-144.1 | S2 (bug) | Home, Profile | Duplicate "Load more" buttons: `PagedCollection` renders both a 1px sentinel div (with `role="button" aria-label="Load more items"`) and a visible "Load more" fallback button. Both appear in the a11y tree. Phase 143 fixed a *different* duplicate (actor Posts tab inline markup) but the shared component still had both. | **Fixed** `e65645b` — sentinel no longer carries `role`/`aria-label`/`tabindex`; it is a pure scroll trigger. |
| F-144.2 | S3 (cosmetic) | Home, Profile | Inconsistent timestamp formats: some posts show relative ("1d ago", "6d ago"), others absolute ("03/06/2026 14:26", "09/09/2026 03:00"). Root cause: remote objects carry a `published` datetime that the client renders as `MM/dd/yyyy HH:mm` when it is "old enough" but falls back to relative for others. The threshold or format choice is inconsistent. | Open — needs a unified timestamp formatter. |
| F-144.3 | S3 (cosmetic) | Home, Profile, Object | "1 replies" instead of "1 reply" in `EngagementBar` aria-label. | **Fixed** `e65645b` — pluralized correctly. |
| F-144.4 | S2 (console) | Home, Profile | 23 console errors per page load: 401s on `/ap/v1/proxy/{remote-iri}/likes` and `/shares` for remote objects, 404s on `/ap/v1/actor?iri={remote-actor}` for remote handles. The client fetches interaction counts and actor docs for every post including remote ones; the server correctly returns 401/404 but the client logs each as an error. Not a data bug but noisy and confusing. | Open — client should suppress or downgrade these expected failures, or the server should return 204/empty for proxy endpoints when unauthenticated. |
| F-144.5 | S3 (UX) | Profile | "Your posts" tab includes replies (with "In reply to" context). Tab label is slightly misleading; "Posts & replies" or a separate "Top-level" filter would be clearer. | Open — low priority. |
| F-144.6 | S2 (UX bug) | Notifications | Remote content notifications show raw IRI path fragments as link text: "statuses 117271175753958692", "statuses 117272501948282332". `ShortLabel` takes the last two path segments which is meaningless for remote Mastodon IRIs. | **Fixed** `e65645b` — `FriendlyLabel` now returns "View post" for any IRI containing `/statuses/` or `/notes/`. |
| F-144.7 | — | Notifications | Only a single "Load more" button (no duplicate). Confirms the duplicate was specific to `PagedCollection`. | N/A |
| F-144.8 | S3 (data) | Communities | Community descriptions from Lemmy render raw HTML: `<p>A community…</p>`, `<a href=…>`. The client should strip HTML or render it safely. | Open — strip tags in `Communities.razor` list rendering. |
| F-144.9 | S3 (UX) | Communities | "Create a community" form is always expanded, pushing the list down. A "Create community" button that toggles the form would be cleaner. | Open — low priority. |

## Summary

- **Fixed this turn:** F-144.1 (duplicate Load-more), F-144.3 (reply pluralization), F-144.6 (notification labels) — commit `e65645b`.
- **Open (next turns):** F-144.2 (timestamp consistency), F-144.4 (console error noise), F-144.5 (profile tab label), F-144.8 (HTML in community descriptions), F-144.9 (create form UX).
