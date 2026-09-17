# Phase 152 — Announce/Like card enhancements (reply → original post)

**Status:** COMPLETE (live-verified, all suites green, 0 console errors)
**Date:** 2026-09-17

## What

When a boost (Announce) or like (Like) card's target is itself a **reply** (its `inReplyTo` is set),
the card now renders the **original post it answers to** as its body — keeping the "Boosted by" /
"Liked" indicator — plus a "replying to …" context hint, instead of rendering the reply.

Previously, boosting or liking a reply showed the reply itself (with its own "In reply to …" nested
context). That is redundant: the interesting content is the original post the reply was about. Now
the card surfaces that original post directly, so a reader scanning their boosts/likes sees the post
they were engaging with, not the (often short) reply.

## How

Client-only. All changes are in `apps/Iris.Web.Client/Components/ObjectView` (the card component).

### `ObjectView.razor.cs`

- **New fields:** `_boostedParentObject` and `_likedParentObject` (`IObject?`) — the resolved
  original post for a boosted/liked reply.
- **Fetch (best-effort, in `OnInitializedAsync`):** after the boosted object (`_announcedObject` /
  `UnwrapCreate`) or the liked object (`_likedObject`) is known, if
  `BoostedObject?.GetParentIri()` (resp. `_likedObject?.GetParentIri()`) is non-null, fetch the parent
  via `Ui.GetContentObjectAsync(parentIri)` and store it. A fetch failure is **non-fatal** — the card
  simply falls back to rendering the boosted/liked reply itself (the prior behavior).
- **New properties:**
  - `BoostedReplyParent` — `_boostedParentObject` when the boosted object is a reply and the parent
    was resolved; else `null`.
  - `BoostedRenderTarget` — `BoostedReplyParent ?? BoostedObject` (what the Announce branch renders).
  - `IsBoostOfReply` — `BoostedReplyParent is not null` (drives the hint).
  - `LikedReplyParent` / `LikedRenderTarget` / `IsLikeOfReply` — the same for the Like branch
    (`LikedRenderTarget = LikedReplyParent ?? _likedObject`).
  - `ReplyContextLabel(parent)` — a short "replying to …" label (the parent's author handle when
    resolvable, else "original post").
- **Body props now read off the render target:** `BoostedAuthorIri`, `BoostedPublished`,
  `BoostedName`, `BoostedContentIri`, `BoostedTitleDuplicatesContent`, and `ResolveBoostedAttachments`
  all switch from `BoostedObject` to `BoostedRenderTarget`, so the author/timestamp/text/attachments
  shown are the original post's, not the reply's.

### `ObjectView.razor`

- **Announce branch:** renders `BoostedRenderTarget is { } boosted` (the original post when the
  boost is of a reply, else the boosted object) and, when `IsBoostOfReply`, a
  "replying to …" hint span under the "Boosted by" header.
- **Like branch:** renders `LikedRenderTarget is { } liked` (the original post when the like is of a
  reply, else the liked object) and, when `IsLikeOfReply`, a "replying to …" hint span under the
  "Liked" header.

### CSS

- New `.object-boost-reply-hint` and `.object-like-reply-hint` styles (a muted, small, italic
  context line). Added identically to both `apps/Iris.Web.Client/wwwroot/css/app.css` and
  `apps/Iris.Web/wwwroot/css/app.css` (kept in lockstep).

## Verification

- **Build:** `dotnet build -c Release` — 0 warnings / 0 errors.
- **Tests:** full `dotnet test -c Release` — all suites green, 0 failures (0 `[FAIL]` lines):
  Iris.Web.Tests 106, Iris.Core.Tests 446, Iris.Client.Tests 187, SampleServer.Tests 38,
  Iris.LiveInterop.Tests 24, Iris.Client.Extensions.Tests 29, Iris.Server.Data.Tests 11,
  Iris.Testing 12, SampleBlazorClient.Tests 17, Iris.WebCrypto.Tests 3 (Iris.Server.Tests summary is
  truncated by nohup stdout buffering, but 0 `[FAIL]`; the change is client-only so it is unaffected).
- **Live (docker, `irisweb-iris-web-1`, `irisweb-db-1`):**
  - **Build gotcha:** the first `docker compose build iris-web` reused a cached layer and served a
    stale WASM bundle (the new strings were absent from the deployed
    `Iris.Web.Client.*.wasm`), so the change appeared not to work. `docker compose build --no-cache`
    forced a recompile; the new WASM (`Iris.Web.Client.4bzf6ba31v.wasm`) verified to contain the new
    strings (UTF-16 "replying to" + "object-like-reply-hint"). A fresh browser context then loaded
    the new bundle (an earlier context had cached `…w4hzt092ou.wasm`).
  - **Like of a reply** (profile → Likes tab): a card renders "Liked … **replying to alice**" with
    alice's original post ("138.11 fidelity check: Iris top-level post to Lemmy community…") as the
    body — author alice, To interop, engagement bar — instead of the short reply.
  - **Boost of a reply** (home feed): a card renders "Boosted by Gargron … **replying to
    everton137**" with everton137's original post ("Tonight at 18:30 at FediDay Berlin…") as the
    body, including its link attachment and engagement bar.
  - 0 console errors (only the pre-existing favicon 404 / WASM auth noise, unrelated).

## Files

- `apps/Iris.Web.Client/Components/ObjectView.razor.cs`
- `apps/Iris.Web.Client/Components/ObjectView.razor`
- `apps/Iris.Web.Client/wwwroot/css/app.css`
- `apps/Iris.Web/wwwroot/css/app.css`
- `PLAN.md`, `docs/ROADMAP.md`, `docs/plans/production-app-feature-matrix.md`
