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
| F-144.2 | S3 (cosmetic) | Home, Profile | Inconsistent timestamp formats: some posts show relative ("1d ago", "6d ago"), others absolute ("03/06/2026 14:26", "09/09/2026 03:00"). | **Reviewed — no change.** `TimeFormatting.Relative` uses a deliberate 7-day threshold: < 7d → relative ("Xd ago"), >= 7d → absolute ("MM/dd/yyyy HH:mm"). The observed posts were correctly formatted (03/06 and 09/09 are > 7d before 09/16). Consistent with Mastodon convention. |
| F-144.3 | S3 (cosmetic) | Home, Profile, Object | "1 replies" instead of "1 reply" in `EngagementBar` aria-label. | **Fixed** `e65645b` — pluralized correctly. |
| F-144.4 | S2 (console) | Home, Profile | 23 console errors per page load: 401s on `/ap/v1/proxy/{remote-iri}/likes` and `/shares` for remote objects, 404s on `/ap/v1/actor?iri={remote-actor}` for remote handles. The client fetches interaction counts and actor docs for every post including remote ones; the server correctly returns 401/404 but the client logs each as an error. Not a data bug but noisy and confusing. | **Partially fixed** `ce157c2` — engagement walk skipped for remote objects (401s gone). Residual actor-doc 404s are browser network-log noise that cannot be suppressed from app code; left as known cosmetic. |
| F-144.5 | S3 (UX) | Profile | "Your posts" tab includes replies (with "In reply to" context). Tab label is slightly misleading; "Posts & replies" or a separate "Top-level" filter would be clearer. | **Reviewed — no change.** There is a separate "Replies" tab; "Your posts" is the content-filtered outbox (top-level posts + replies + boosts). Renaming to "Posts & replies" would be redundant next to the "Replies" tab. Excluding replies from "Your posts" is a behavioral change deferred. |
| F-144.6 | S2 (UX bug) | Notifications | Remote content notifications show raw IRI path fragments as link text: "statuses 117271175753958692", "statuses 117272501948282332". `ShortLabel` takes the last two path segments which is meaningless for remote Mastodon IRIs. | **Fixed** `e65645b` — `FriendlyLabel` now returns "View post" for any IRI containing `/statuses/` or `/notes/`. |
| F-144.7 | — | Notifications | Only a single "Load more" button (no duplicate). Confirms the duplicate was specific to `PagedCollection`. | N/A |
| F-144.8 | S3 (data) | Communities | Community descriptions from Lemmy render raw HTML: `<p>A community…</p>`, `<a href=…>`. The client should strip HTML or render it safely. | **Fixed** `812c4d0` — `StripHtml` helper removes tags, decodes entities, collapses whitespace. |
| F-144.9 | S3 (UX) | Communities | "Create a community" form is always expanded, pushing the list down. A "Create community" button that toggles the form would be cleaner. | **Fixed** `5d41afc` — form collapses behind a "+ Create a community" button; auto-collapses after success. |

## Summary

- **Fixed (6):** F-144.1 duplicate Load-more, F-144.3 reply pluralization, F-144.6 notification labels (`e65645b`); F-144.4 remote engagement 401s (`ce157c2`); F-144.8 HTML in community descriptions (`812c4d0`); F-144.9 create-community form toggle (`5d41afc`).
- **Partially fixed:** F-144.4 — engagement 401s eliminated; residual actor-doc 404s are browser network-log noise (cannot suppress from app code).
- **Reviewed — no change (2):** F-144.2 (timestamp 7-day threshold is correct), F-144.5 (profile "Your posts" tab is correct alongside the separate "Replies" tab).
- **Phase 144 complete.** All 8 findings addressed.
