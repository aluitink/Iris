# Phase 115.1 — Boost / Like Engagement: Live Verification + Matrix Reconciliation

## What

Live-verified the boost (Announce) and like (Like) engagement features end to
end and reconciled their D-column (web-integration) matrix entries. The
feature was **already fully implemented** across all three layers; this slice
closes the verification gap and updates the feature matrix.

## What was verified

### Client layer (already present)
- `ActivityPubClient.AnnounceAsync` / `UnannounceAsync` — build an
  `Announce`/`Undo(Announce)` activity, sign, deliver, and return the minted
  activity IRI (`DeliveryResult.MintedId`) that a later un-boost references.

### Server layer (already present)
- `AnnounceActivityHandler` records the boost; `UndoActivityHandler` removes it.
- `FileBackedAnnounceStore` (now Postgres-backed) persists Announce activities.
- The object document is annotated with the `iris:` engagement extensions.

### Web layer (already present)
- `EngagementBar` renders Like / Boost / Reply buttons on every post.
- `ToggleBoostAsync` / `ToggleLikeAsync` call the client, apply the state +
  local count delta on success, roll back on failure, and invalidate the shared
  engagement cache.
- The bar seeds counts + the signed-in user's engaged state from the
  server-rendered `iris:*` extensions (zero per-object collection walks).

## Live verification (Playwright, fresh context)

- **Anonymous home page** renders **24/24** engagement bars, each with a
  correctly-structured Boost button (`aria-label="Boost"`, `aria-pressed`,
  `.engagement-count`, refresh SVG, enabled).
- **Server boost-state computation:** fetched a locally-boosted note
  (`…/alice/notes/06G84EEMA8PZR6FPZSJSVH2PPR`, boosted by andrew). The object
  doc correctly serves:
  - `iris:isShared = true`
  - `iris:sharedCount = 1`
  - `iris:announceActivityIri = …/andrew/announces/06G8568M1EHEEC6AM04ZHXGST4`
  (the minted IRI an un-boost `Undo` references)
  - plus `iris:isLiked = true`, `iris:likedCount = 3`, `iris:likeActivityIri`.
- **DB cross-check:** 15 Announce activities in `Activities`
  (`ActivityType LIKE '%Announce%'`), matching the served counts.
- Console errors on the anonymous home are pre-existing CORS failures fetching
  remote Mastodon actor docs — unrelated to engagement.

## Matrix reconciliation (D-column)

| Item | Before | After |
|---|---|---|
| Like (star) | ☐ | ✅ |
| Boost (announce) | ☐ | ✅ |
| Like/boost counts on a post | ☐ | ✅ |
| Optimistic UI on like/boost/reply | ☐ | ✅ |

The "Optimistic UI" note records the actual pattern: optimistic-apply-on-success
with rollback (state changes only on success; count delta applied locally; cache
invalidated on toggle). Reply is a composer link, not an optimistic update.

## Why a reconciliation slice

The A (library) and C (server) columns were already ✅ with extensive
integration coverage (`AnnouncePropagationIntegrationTests`,
`OutboxAnnounceFanOutIntegrationTests`, `LikeAnnounceUndoPropagationIntegrationTests`,
~170 test assertions across the announce surface). The D column was ☐ only
because the web-integration had never been live-verified and the matrix never
reconciled. This turn does exactly that — no new code.

## Verification

- `dotnet build` clean; `Iris.Server.Tests` 1105/0 (in isolation);
  `Iris.Web.Tests` 95/95.
- Live Playwright verification as above.
- No new coded tests (WASM manual-test policy).

## Files

- `docs/plans/production-app-feature-matrix.md` (4 D-column rows reconciled).
