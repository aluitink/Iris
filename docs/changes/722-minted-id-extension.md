# 72.2 — Minted-id extension: server-rendered Like/Announce activity IRI

- **Status:** implemented (live-verified; full suite green)
- **Phase:** 72 (Efficiency + UX residuals), slice 2
- **Resolves:** the residual `/likes`+`/shares` walk the 72.1 cache left (the single id-recovery walk an engaged post still paid)

## Problem

72.1 eliminated the 2× walk per remote post but left one residual: an engaged post still fired a **single** `/likes`+`/shares` walk to recover the minted `Like`/`Announce` activity IRI (the id an `Undo` references). The minted id lives in the activity store (the `Like`/`Announce` activity document), not the edge stores — so the client had to walk the collection to find it.

## Fix

Render the minted Like/Announce activity IRI on the content object **server-side**, alongside the existing `iris:isLiked`/`iris:isShared` per-requester extensions. Two new terms:

- **`iris:likeActivityIri`** — the IRI of the `Like` activity the requester issued against the object (present only when `isLiked` is `true`).
- **`iris:announceActivityIri`** — the IRI of the `Announce` activity the requester issued against the object (present only when `isShared` is `true`).

### Server

- **`IrisExtensionTerms.LikeActivityIri`** / **`AnnounceActivityIri`** — new term constants in `Iris.Core`.
- **`BuildNamespaceDocument`** — both terms declared as `"@id"` in the namespace `@context`.
- **`GetRequesterActivityIrisAsync`** — a new helper in `ActivityPubServerExtensions.cs` that sweeps `persistence.Activities.GetAllActivitiesAsync()` once, filtering to the requester's `Like`/`Announce` activities on the page's objects. Uses `ResolveObjectIri()` for robust actor/object matching (handles both `ILink` and `IObject` references) and `string.Equals(..., OrdinalIgnoreCase)` to avoid the `object.Equals` shadowing issue with `AudienceIriComparer`.
- **`EnrichCollectionItemsAsync`** — Phase 2 batch-fetches the minted ids for all page items in a single activity sweep; Phase 3 annotates each deep-copied object with `likeActivityIri`/`announceActivityIri` when present.
- **`ObjectDocumentHandler`** — computes the minted ids via `GetRequesterActivityIrisAsync` and passes them to `ServeObjectDocument`.
- **`ServeObjectDocument`** — renders `likeActivityIri`/`announceActivityIri` alongside `isLiked`/`isShared`.

### Client

- **`IrisDocumentExtensions.GetLikeActivityIri()`** / **`GetAnnounceActivityIri()`** — new readers on `Iris.Client`.
- **`EngagementBar.razor`** — the fast path seeds `_likeIri`/`_announceIri` directly from the server-rendered extensions (no walk). The 72.1 shared-cache walk remains as a **residual fallback** only when the extension is absent (a non-Iris instance, or a read the server did not enrich with per-requester state).

## Files

- `src/Iris.Core/IrisExtensionTerms.cs` — `LikeActivityIri`, `AnnounceActivityIri` constants.
- `src/Iris.Server/ActivityPubServerExtensions.cs` — `BuildNamespaceDocument`, `GetRequesterActivityIrisAsync`, `EnrichCollectionItemsAsync`, `ObjectDocumentHandler`, `ServeObjectDocument`.
- `src/Iris.Client/IrisDocumentExtensions.cs` — `GetLikeActivityIri()`, `GetAnnounceActivityIri()`.
- `apps/Iris.Web.Client/Components/EngagementBar.razor` — fast path rewired to seed minted ids from extensions.

## Verification (MCP Playwright, live on `https://iris.luit.ink:8088`, fresh `docker compose build --no-cache` + `--force-recreate`)

- **Feed response** carries both extensions on the engaged post: `likeActivityIri: "…/likes/06G8J59H3F8ZFSY6M27MQF6H14"`, `announceActivityIri: "…/announces/06G8BTJTADVTB2Y6MYE0D5R2QR"`.
- **0 `/likes` and 0 `/shares` requests** fire for the engaged post (down from 1+1 after 72.1). The minted ids are read directly from the feed document — no collection walk at all.
- **Namespace document** declares both terms as `"@id"`.
- **Full suite** (`dotnet test Iris.slnx`): **975 passed, 17 skipped, 0 failed**. No new coded tests (WASM manual-test policy — UI verified via Playwright).

## Residual

The 72.1 shared cache remains as a fallback for non-Iris instances or un-enriched reads (the extension is absent). For a native Iris read, an engaged post now fires **zero** engagement-collection walks.
