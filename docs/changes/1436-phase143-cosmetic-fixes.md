# 143.6 — Cosmetic consistency fixes (F-142.12/20)

**Commit:** `a3fb8c1`
**Findings:** [F-142.12](../plans/phase-142-consistency-review.md) (S3) + [F-142.20](../plans/phase-142-consistency-review.md) (S3)

## F-142.12 — Object page heading `@handle` → bare handle

The object page (`/object?iri=...`) heading showed `@andrew` while the actor page (`/actor?iri=...`) showed `andrew`. Removed the `@` prefix in `ObjectDetail.PageHeading` (both the `PreferredUsername` path and the IRI-fallback path) for consistency.

## F-142.20 — Redundant member count on community page

The community page header showed "N members" AND the Members tab showed "Members (N)". Removed the header count (the `<span class="community-member-count">`); the tab remains the single source of the member count.

## Skipped findings (debatable / intentional)

- **F-142.10** ("following" label reads as a verb): standard on Mastodon/Pleroma; no change.
- **F-142.15** (actor Posts tab has no search): community feeds are large and benefit from search; actor feeds are typically small. Could be intentional; not a bug.
- **F-142.18** (empty state structure varies): the community feed has a dedicated H2 + subtitle because it's a major surface; other tabs use the compact empty state. Reasonable differentiation.

## Verification

- `dotnet build` clean.
- `dotnet test` green (1283/1283 Iris.Server.Tests on re-run; 1 known-flaky background-delivery test failed on full-suite run, passed in isolation).
- Per web test policy: no new coded tests; verification is manual via Playwright (live Docker app).
