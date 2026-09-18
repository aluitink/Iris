# Phase 114.1 — Empty-State Iconography & Inline-Style Consistency

## What

Two visual-consistency fixes from the Phase D (polish) pass:

1. **Empty states now consistently use the centered-icon treatment.** The
   main pages (timeline, directory, search, notifications, communities) already
   rendered a rich `.empty-state` (48px feather icon + message + optional action
   link). But the reusable `PagedCollection` and `ActorListPanel` components —
   which back every tab panel (posts, replies, likes, followers, following) —
   fell back to a plain `<p class="muted">` with no icon. So a user on the
   timeline saw an illustrated empty state, but the moment they switched to the
   "Followers" tab they saw bare text. This makes the tab empty states match.

2. **Removed all inline `style="font-size: …"` from the client.** Seven spans
   (Settings muted/member/relay/blocked/reported badges + the Communities
   handle hint) used inline font-size, bypassing the type scale. A new
   `.text-sm`/`.text-xs` utility pair replaces them.

## Changes

### `app.css`
- `.empty-state--compact` — compact variant of the empty state for in-card /
  tab-panel contexts: 32px icon (vs 48px), `--space-4/--space-3` padding (vs
  `--space-6/--space-4`), `--font-size-sm` message with no bottom margin.
- `.directory-card-empty` — small, light message for the expanded
  directory-card "No recent posts." row (sits inside an already-nested card, so
  lighter than a page-level empty state).
- `.text-sm` / `.text-xs` — type-scale utilities mapping to
  `--font-size-sm` / `--font-size-xs`.

### Razor
- `PagedCollection.razor` — plain empty fallback now renders a compact
  empty-state with a message-bubble icon (feather `message-circle`).
- `ActorListPanel.razor` — plain empty fallback now renders a compact
  empty-state with a people icon (feather `users`).
- `DirectoryCard.razor` — "No recent posts." uses `.directory-card-empty`
  instead of an inline style.
- `Settings.razor` (5 badges) + `Communities.razor` (1 hint) — inline
  `font-size` → `text-sm`.

## Why

The UI guidelines call for a single consistent `EmptyState` treatment (icon +
message + optional action) across every empty collection. The main pages had
it; the tab panels didn't. This closes the gap without forcing every tab to
carry a bespoke `EmptyContent` fragment — the default fallback is now
visually coherent on its own.

The inline-style removal is a direct follow-through from the 113 token work:
inline `font-size` is exactly the kind of raw value the token system exists to
eliminate.

## Icon set

All empty-state icons are 24px-viewBox feather-style line icons
(`fill="none" stroke="currentColor" stroke-width="1.5"`), consistent with the
existing timeline/directory/search icons. The new compact state reuses the same
icons at a smaller size rather than introducing a new set.

## Verification

- `dotnet build` clean (0 errors, warnings-as-errors on).
- `Iris.Web.Tests` 95/95 (no new coded tests — WASM manual-test policy).
- Rebuilt the Docker app. Live Playwright (fresh context): the served
  `app.css` contains `.empty-state--compact`, `.empty-state--compact svg`,
  `.text-sm`, `.text-xs`, `.directory-card-empty`; `grep` confirms zero
  inline `font-size` styles remain in the client; login page renders fully
  intact (header/nav/form/footer).
- The tab empty states themselves require an authenticated session, which the
  headless WASM login can't establish in this environment (known limitation);
  the markup + served-CSS presence is verified, and the compact state is the
  same DOM structure the main pages already render correctly.

## Files

- `apps/Iris.Web.Client/wwwroot/css/app.css`
- `apps/Iris.Web.Client/Components/PagedCollection.razor`
- `apps/Iris.Web.Client/Components/ActorListPanel.razor`
- `apps/Iris.Web.Client/Components/DirectoryCard.razor`
- `apps/Iris.Web.Client/Components/Pages/Settings.razor`
- `apps/Iris.Web.Client/Components/Pages/Communities.razor`
