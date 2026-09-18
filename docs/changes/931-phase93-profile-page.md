# 93.1 — Profile page (banner + avatar, fixed posts/replies/likes tabs, privacy fieldset)

**Phase:** 93 — Profile page
**Date:** 2026-09-12
**Status:** Complete

## Objective

From the 93 brief, the profile page had four rough edges:

1. **Banner + avatar** — "Mastodon supports an image and icon in the actor object, one is a profile header background and one is the visible user icon on all post. … When on the actors profile page or an actors detail page it should be large and layed on top of the image."
2. **"Your posts" broken** — "I no longer see items."
3. **Replies tab wrong scope** — "The Replies tab is showing me replies for other peoples posts. The Likes tab is also empty."
4. **Edit profile checkbox oddly placed** — "the checkbox for require approval is oddly placed."

Phase 93 addresses all four: a Mastodon-style **banner + overlaid avatar** in the profile header, a **filter-corrected outbox read** so "Your posts" / "Replies" / "Likes" surface the right items, and a **relocated require-approval checkbox** in a labelled fieldset.

## Root cause (93.2 / 93.3)

An actor's **outbox is a mixed collection**: the user's own activities interleaved with **mirrored remote content** (inbound federated `Create`/`Announce` activities are added to the local recipient's outbox by `CreateActivityHandler` / `AnnounceActivityHandler`), ordered **newest-first**. On a live instance the first several pages are almost entirely remote/mirrored items; the user's own notes sit many pages deep. Two separate defects compounded:

- **`PagedCollection` only read the first page.** A filtered view (`ItemFilter` set) whose first page held zero matching items rendered its empty state even though matching items existed further back.
- **`OutboxFilter.IsOwnContentItem` resolved the actor IRI from `activity.Actor.FirstOrDefault().Id` only.** The wire `actor` on an outbox activity is a **bare IRI string** (e.g. `"actor": "https://…/u/andrew"`), which the ActivityStreams library deserializes to a **`Link`** (an `ILink`, *not* an `IObject`). A `Link` exposes `Href`, not `Id`, so `.Id` was `null` and every own post was filtered out — even when the page did contain them.

## What was built

### Client — `PagedCollection.razor` (filtered top-up)

A filtered view now **top-ups** pages after the initial first page: when an `ItemFilter` is active and the rendered (post-filter) count is below the page size, it keeps pulling additional pages (capped at `FilteredTopUpMaxPages = 12`) until enough matching items are collected or the collection is exhausted. Called from both `LoadInitialAsync` and `LoadMoreAsync`, so the first screenful and each "Load more" click are relevant rather than a single (possibly all-foreign) page. Unfiltered views are unaffected.

### Client — `OutboxFilter.cs` (actor-IRI resolution)

`IsOwnContentItem` now resolves the actor IRI via a new `ResolveActorIri(IEnumerable<IObjectOrLink>?)` helper that handles **both** wire shapes: a `Link { Id }` (the typed-Link shape Iris's own server emits) and a bare `ILink { Href }`, plus an embedded `IObject { Id }`. This is the fix for "Your posts" being empty and for the Replies tab being self-scoped correctly.

### Client — `Profile.razor` (tab scoping + empty message)

- **Replies** tab filter (`IsReply`) now also requires `OutboxFilter.IsOwnContentItem(item, Session.ActorId)` — a reply is shown only when the **author is the signed-in user** and the note has an `inReplyTo`. This drops others' replies that were leaking in.
- **Likes** tab empty message corrected to "You haven't liked any posts yet." (previously the false empty state showed even when likes existed, because the actor-resolution bug above filtered them out). The "Liked X" card style is the pre-existing intended `ObjectView` design (a `Like` renders a "Liked" + author + time row; the liked object is not inlined).

### Client — banner + avatar (`ActorIdentityHelper`, `ActorProfile`, `ActorAvatar`)

- **`ActorIdentityHelper`** — added `BannerIri(IObject?)` (the first `image`'s resolved IRI — Mastodon's banner, distinct from the `icon` avatar). `IconIri` / `BannerIri` now share a single `ResolveImageOrLinkIri` that tries, in order: an `IObject` `id` (Iris-local media), an `IObject` `url` (remote), a **`Link` `id`** (the typed-Link wire shape Iris's server emits — the shape the earlier checks missed, which is why the avatar/banner never rendered), and a bare `ILink` `href`.
- **`ActorProfile.razor`** — renders a **banner strip** (`actor-profile-banner`) when the actor has a resolvable `image`, with the header body (`actor-profile-main`: avatar + handle + name + summary) laid over it. The avatar is enlarged and overlaps the banner bottom (Mastodon-style).
- **`ActorAvatar.razor`** — the render now uses `EffectiveIconIri` (which honors `IconIriOverride`) instead of the fetch-only `_iconIri`; the fallback initial uses `EffectiveDisplayName`. (Previously the `IconIriOverride` passed by `ActorProfile` was ignored by the render, so the avatar always fell back to the initial even when the caller had already resolved the icon.)

### Client — `EditProfileForm.razor` (privacy fieldset)

The require-approval checkbox is wrapped in a **`<fieldset class="edit-profile-privacy">`** with a "Follow requests" legend: the checkbox + "Require approval for follow requests" label on one row, the hint on the next — a clearer, better-positioned control than the previous inline form-group.

### CSS (`app.css`, mirrored to `apps/Iris.Web`)

- `.actor-profile--bannered` / `.actor-profile-banner` (the banner strip) / `.actor-profile-main` (the overlaid body); the profile avatar is enlarged (`margin-top: -3rem`, 6rem) so it overlaps the banner; the fallback glyph is enlarged to match.
- `.edit-profile-privacy*` fieldset styling (legend, row layout, hint).

## Verification

- **Build:** `dotnet build -c Release` → 0 warnings / 0 errors.
- **Tests:** `dotnet test -c Release --filter "Category!=Slow"` → **1852 passed / 0 failed** (no regressions; all Phase 93 changes are client/WASM, so 0 new coded web tests per the Phase 45+ WASM Manual-Test Policy).
- **Live (Playwright, signed in as `andrew`):**
  - Banner renders as the header background with the avatar overlaid (Mastodon-style).
  - "Your posts" now shows andrew's own notes (was empty).
  - "Replies" shows only andrew's **own** replies (self-scoped).
  - "Likes" shows andrew's actual "Liked …" entries (was a false empty message).
  - "Edit profile" → "Follow requests" fieldset with the require-approval checkbox well-placed.
  - **0 console errors.**

## Notes / follow-ups

- **Banner is not yet settable via update.** `UpdateActivityHandler.MergeActorFields` merges `Name` / `Summary` / `Icon` / `Endpoints` / `ExtensionData` — **not `Image`** — so the banner cannot be uploaded/changed through the edit-profile path yet. For this phase's live verification the actor document's `image` was seeded directly in the store. Adding `Image` to the merge (and a banner upload control) is a future item.
- The Likes tab's "Liked X" rows do not inline the liked object (a pre-existing `ObjectView` design decision); inlining it would be a separate enhancement.
