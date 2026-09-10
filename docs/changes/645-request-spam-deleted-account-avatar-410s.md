# 64.5 — deleted-account avatar 410s on /notifications (request spam, topic #7)

Phase 64 continues cutting the *number* of calls the WASM client makes. 64.5 is the
final non-deferred slice: stop the **deleted-account notifications** on
`/notifications` from firing a doomed proxy fetch for the (gone) actor's avatar —
each such fetch 410-Gones, adding pure network noise + a console error.

## Problem (620 tracker topic #7)

On `/notifications`, every "deleted their account" notification fired a
`POST /ap/v1/proxy/{remote-actor-iri}` fetch for the actor's avatar that **410-Gone**
(the remote account no longer exists). The tracker counted **15** such 410s on page 1
(28 total in 62.1). The UI already degraded gracefully (an initial-letter avatar
fallback + a "deleted their account" caption), so the 410s were pure network noise and
console errors — exactly the kind of redundant call Phase 64 is cutting.

## Root cause

Two fetch paths, one per row:

1. `NotificationRow.OnInitializedAsync` fetches the row's actor document via
   `UiContext.GetActorAsync` (to render the avatar + display name). For an
   account-deletion, that actor is gone → the proxy 410s.
2. `ActorAvatar.OnInitializedAsync`, when given no icon override, **also** fetches the
   actor document to resolve the icon. For a deletion (no icon known), that is a
   **second** 410.

An account-deletion notification is a `Delete` activity whose `object` is the actor's
**own** IRI (the remote instance sends `object == actor` — the account itself is the
deleted object). The row already detected this (`isSelfDelete`, B-010) to render the
"deleted their account" caption, but neither fetch path consulted it.

## Fix

`apps/Iris.Web.Client/Components/NotificationRow.razor`:

- Added a shared `IsSelfDelete(activity)` helper (a `Delete` whose `object` IRI equals
  its `actor` IRI). The markup's local `isSelfDelete` now calls it (no behavior change
  to the caption logic).
- `OnInitializedAsync` now **skips** the actor-document fetch when the activity is an
  account deletion (`IsSelfDelete`) — the row renders the initial-letter avatar
  fallback and the "deleted their account" caption, no document needed. This kills
  fetch path (1).

`apps/Iris.Web.Client/Components/ActorAvatar.razor`:

- Added a `SkipFetch` parameter. When `true`, `OnInitializedAsync` does not fetch the
  actor document — it renders the initial-letter fallback from `DisplayName`/the
  handle, using `IconIriOverride` for the icon when one is supplied. This kills fetch
  path (2).

`NotificationRow` passes `SkipFetch="true"` (plus the IRI-derived fallback name as
`DisplayName`) for account-deletion rows, so `ActorAvatar` short-circuits to the
fallback without a second fetch.

**Why `SkipFetch` instead of widening the existing skip condition:** the existing
skip is `IconIriOverride is not null && DisplayName is not null` — both must be known.
For a gone account there is *no* icon IRI to supply, so that condition can't be
satisfied. A dedicated `SkipFetch` flag is the explicit, intention-revealing way to say
"the account is gone; do not fetch." It is **only** set by `NotificationRow` for
deletions, so every other `ActorAvatar` caller (post cards via `ActorBar`/`ObjectView`,
`ActorCard`, `ActorProfile`, `CommunityDetail`) keeps its existing fetch behavior
unchanged — no regression to avatars that rely on the component's own fetch.

## Verification

**Build + fast suite:** `dotnet build` clean (0 warnings, 0 errors,
`TreatWarningsAsErrors` on). `Iris.Web.Tests` 62/62. (WASM-side change →
Playwright-verified on the live app per the Phase-45+ web-test policy; no new coded web
test.)

**Live (fresh origin `:8140`, signed in as `andrew`, `GET /notifications`):**

- **Before:** 15 (page 1) / 28 (total) `POST /ap/v1/proxy/{gone-actor}` **410-Gone**
  avatar fetches (one per "deleted their account" row).
- **After:** **0** 410 responses anywhere in the request log. The only proxy fetch is
  the *live* remote actor RayvenMX (`POST .../users/RayvenMX` → `200 OK`, for a
  non-deletion notification). No `proxy` request to a gone actor at all.
- All **17** "deleted their account" rows on page 1 render the **initial-letter avatar
  fallback** (0 `<img>` avatars among them) + the "deleted their account" caption + the
  IRI-host name fallback (e.g. `fairy.id`, `m0il`, `donnabug`).
- **0 console errors** (the 410s were the prior console-error source).

**No regression:** `/home` renders with 0 console errors; its post-card avatars are
unchanged (the local test user `andrew` has no icon set, so the initial-letter fallback
is the pre-existing, correct rendering — `ActorBar`/`ObjectView` pass no `SkipFetch`,
so their fetch path is untouched).

## Phase 64 closeout

With this slice, **all 7 non-deferred Phase 64 topics are fixed** (#1–#6 here, #7 here).
The only remaining item is the **optional minted-id extension** (render the minted
Like/Announce activity IRI on the object so the per-engaged-card id-recovery walk
disappears — the residual 5+5 from 64.2). That is a bounded server addition + a new
client extension read and is explicitly **deferred** (it would remove a small,
engagement-proportional residual, not a feed-size N+1). Phase 64 is therefore
**functionally complete**; the minted-id extension can be picked up as a follow-up or
deferred indefinitely.
