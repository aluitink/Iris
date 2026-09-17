# Phase 153 — General object card improvements

**Status:** COMPLETE (live-verified, build clean 0/0, web tests 106/106, 0 new console errors)
**Date:** 2026-09-17

## What

Three card-polish improvements to the object cards, all in the `ObjectView` card component:

1. **The entire card is the link.** Previously only the post body text was a link (`.object-post-link`),
   so hovering the text made it look like a link and the rest of the card felt inert. Now the **whole
   card** navigates to the object detail: a stretched-link overlay covers the card and the body text is
   a plain (non-link) `<div>`. Hovering anywhere on the card shows the link (pointer) cursor, signalling
   the card is clickable.
2. **Hover shows the link cursor.** The clickable card gets `cursor: pointer` on hover, so a reader
   immediately knows the card is clickable.
3. **Actor-header banner strip.** Each card's actor header (avatar + handle) now sits on a thin tinted
   gradient band for "color and splash" / personalization. The tint is a **deterministic hue derived
   from the author's IRI**, so each author's cards carry a consistent personal color. The band is a
   darker, low-saturation gradient so the handle stays legible on top.

## How

Client-only. All changes are in `apps/Iris.Web.Client/Components/ObjectView` (+ shared `app.css`).

### `ObjectView.razor.cs`

- **`CardLinkIri`** (`Iri?`) — the object IRI the whole card links to: the created object's IRI for a
  `Create`, the object's own IRI for a bare content object, else `null`. When non-null the card is made
  whole-card-clickable.
- **`CardHueIri`** (`Iri?`) — the actor IRI the banner tint is derived from: the activity author for a
  `Create`, the content author for a direct object, else `null`.
- **`CardHueStyle`** (`string`) — the inline `style` value (`--card-hue: <n>;`) set on the card, or
  empty when there is no tint.
- **`CardHue(string key)`** (`static int`) — maps a stable string (an actor IRI) to a hue in `[0, 359]`
  via FNV-1a, so the same author always gets the same banner tint.

### `ObjectView.razor`

- **Create branch** and **direct-object branch** (`Item is Create` / `Item is IObject { Id: … }`):
  - `.object-item` now carries `object-item--clickable` (when `CardLinkIri` is set) and
    `style="@CardHueStyle"`.
  - A stretched-link overlay `<a href="@ObjectHref(cardIri)" class="object-card-link"
    aria-label="Open post">` is rendered as the **first child** of `.object-item` (absolute,
    `inset:0`, `z-index:0`) — the single link for the whole card.
  - The body content is now a plain `<div class="object-content">` (the old
    `<a class="object-post-link">` wrapper was removed), so the text no longer reads as a link.
- **Announce / Like branches** intentionally left as-is this slice: their `.object-post-link` is the
  boosted/liked *target* preview, which is a different link than "open this card".

### CSS (`app.css`, kept identical in both copies)

- `.object-item--clickable { position: relative; cursor: pointer; }` — the card is the positioning
  context for the overlay and shows the link cursor on hover.
- `.object-card-link { position: absolute; inset: 0; z-index: 0; border-radius: inherit; }` — the
  stretched link filling the card.
- **Interactive children raised above the overlay** (`position: relative; z-index: 1`) so they keep
  their own click behavior: `.object-header` (moderation), `.object-recipient`, `.object-parent`,
  `.object-parent-context`, `.object-iri`, `.object-sensitive`, `.object-poll`, `.object-emojis`,
  `.engagement-bar`, `.lemmy-vote-bar`, `.object-media`, `.object-title`.
- **`.object-header-actor` banner strip:** a thin tinted gradient behind the avatar + handle,
  `linear-gradient(90deg, hsl(var(--card-hue, 220) 45% 30% / .55), hsl(var(--card-hue, 220) 45% 30% /
  .12))`, with small padding + `border-radius: var(--radius-sm)`. The darker, low-saturation band gives
  the header "color and splash" while keeping the handle legible. Only visible when the card sets
  `--card-hue` (content cards).

## Verification

- **Build:** `dotnet build -c Release` (Iris.Web + Iris.Web.Client) — 0 warnings / 0 errors.
- **Tests:** `dotnet test tests/Iris.Web.Tests -c Release --no-build` — 106/106 passed, 0 failures.
  (The full suite was green at the start of the phase: 8/10 suites confirmed passing with 0 `[FAIL]`;
  the change is client-only.)
- **Live (docker, `irisweb-iris-web-1`, `irisweb-db-1`):**
  - **Build gotcha (recurring):** `docker compose build iris-web` without `--no-cache` reuses a cached
    layer and serves a **stale WASM** (the browser then loads the old bundle and the new razor
    rendering is absent — only the un-hashed `app.css` updates). `docker compose build --no-cache
    iris-web` forces a recompile; a **fresh browser context** (close + reopen) is required to bust the
    browser's HTTP cache of the content-hashed WASM. Verified the new code is in the deployed wasm via
    a UTF-16LE string search for `object-card-link`.
  - **andrew's post cards** (profile → Posts): each `.object-item` carries
    `object-item--clickable` + `style="--card-hue: 19;"` + a `.object-card-link` overlay; the body
    content is a plain `<div>` (not a link). The actor header shows a warm orange-red tinted band
    (hue 19) behind the avatar + "andrew", with the handle clearly legible on top.
  - **Whole-card navigation:** clicking the card body (which hits the overlay link —
    `document.elementFromPoint` at the body center returns the `.object-card-link <a>`) navigates to
    `/object?iri=…`.
  - **Interactive children on top:** the engagement-bar Like button (z-index 1) is clickable on top of
    the overlay and does **not** trigger card navigation (URL stays on `/actor` after the click).
  - **Banner hue is per-author:** andrew → hue 19 (warm); a different author's cards get a different
    deterministic hue.
  - 0 new console errors (the only error seen pre-login was a pre-existing 401 on a media proxy fetch,
    unrelated; gone after sign-in).

## Files

- `apps/Iris.Web.Client/Components/ObjectView.razor.cs`
- `apps/Iris.Web.Client/Components/ObjectView.razor`
- `apps/Iris.Web.Client/wwwroot/css/app.css`
- `apps/Iris.Web/wwwroot/css/app.css`
- `PLAN.md`, `docs/ROADMAP.md`, `docs/plans/production-app-feature-matrix.md`
