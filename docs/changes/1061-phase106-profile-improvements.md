# Phase 106 — Profile Improvements

## What was built

Two improvements to the Profile page:

1. **Followers and Following tabs**: The profile page now has Followers and Following tabs (between Likes and Requests) that render hydrated `ActorCard` components (avatar + name) via the existing `ActorListPanel`. This matches the ActorDetail page's follower/following views, so a user can see who follows them and who they follow directly from their own profile.

2. **Hydrated Likes tab**: The Likes tab previously showed only "Liked {actor}" with no link to the liked object. It now shows the liked target as a clickable link to the object view (with a content preview when the `Like` activity carries an embedded object, falling back to the IRI when only a link is present).

## Key changes

- **`Profile.razor`**:
  - Added "Followers" and "Following" tabs to the tab bar (between "Likes" and "Requests").
  - Added `FollowersIri` and `FollowingIri` properties derived via `actor.FollowersOf()` / `actor.FollowingOf()`.
  - Each tab renders an `ActorListPanel` with the signed-in client and the appropriate collection IRI.

- **`ObjectView.razor.cs`**:
  - New `LikeTargetIri` property: resolves the target of a `Like` activity (the liked object's IRI), mirroring the existing `FollowTargetIri` pattern.
  - New `LikedContent` property: renders the target's content as safe HTML (pre-rendered or Markdown-to-HTML), for use in the Like branch's content preview.

- **`ObjectView.razor`**:
  - The `Like` branch now includes a `.object-like-target` div with a link to the liked object. When `LikedContent` is non-empty, it shows the content preview; otherwise it falls back to the IRI string.

## Test counts

- 0 new coded web tests (WASM manual-test policy, Phase 45+).
- Full fast suite: **1419 passed / 0 failed** (unchanged — no server-side changes).

## Live verification (Playwright)

- Profile page shows 5 tabs: "Your posts", "Replies", "Likes", "Followers", "Following".
- **Followers tab**: shows andrew ("Andrew Luitink") and bob — hydrated `ActorCard` components with correct links.
- **Following tab**: shows bob — hydrated `ActorCard` with correct link.
- **Likes tab**: shows the liked note with a clickable link to the object view. Clicking the link navigates to `/object?iri=...` and renders the note correctly.
- 0 console errors.

## Decisions

- **Reused `ActorListPanel`**: The Followers/Following tabs use the existing `ActorListPanel` component (introduced in Phase 103 for the ActorDetail page) rather than building a new component. This keeps the rendering consistent and avoids duplication. The panel already handles pagination, hydration, and the anonymous/authenticated client split.
- **Like target via `ActivityEmbeddedObject`**: The content preview for a liked object uses the existing `ActivityEmbeddedObject` property (which reads `Activity.Object?.FirstOrDefault() as IObject`). When the server stores a `Like` with an embedded target (the common case for local likes), the content is available for preview. When only a link is stored (e.g. a federated like from another server), the IRI is shown instead. No server-side changes were needed.
- **No new CSS**: The `.object-like-target` class reuses the existing `.object-post-link` and `.object-content` styles from the Create branch, so no new CSS was needed.
