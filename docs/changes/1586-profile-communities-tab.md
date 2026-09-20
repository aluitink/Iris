# 1586 — Phase 6: Profile Communities tab

**Workstream:** Unified home feed (③④)
**Phase:** 6 of 7

## What was built

The `/profile` page gains a **Communities** tab listing the signed-in actor's followed
communities. Each row shows the community card with a `FollowButton` (Join/Leave) and a
"Manage communities →" link to the `/communities` management page.

**`Profile.razor`:**
- New "Communities" tab in the tab bar (between "Following" and "Requests").
- Communities panel: "Manage communities →" header link, lazy-loaded followed communities
  (local `Group`s whose IRI is in the following collection), each with a `FollowButton`
  (unfollow/leave) + stats.
- `LoadFollowedCommunitiesAsync`: reads the following IRIs via `Ui.GetFollowingActorIrisAsync`,
  searches local actors (`Type = "Actor", LocalOnly = true`), filters to `Group`s in the
  following set, sorts by handle.
- `CommunitiesFollowToggledAsync`: re-reads following IRIs after a follow/leave toggle and
  re-renders the list (the `FollowButton` invalidates the following cache before this runs).
- Lazy load: the list loads on first tab open (mirrors the Requests tab pattern).

## Files changed

- `apps/Iris.Web.Client/Components/Pages/Profile.razor` — Communities tab + panel + code.

## Verification

- Build clean (0 warnings, 0 errors).
- `Iris.Web.Tests` 106/106 pass.
- `Iris.Server.Tests` 1385/1385 pass.
- Live-verified (fresh browser context, `s7test`):
  - `/profile` → Communities tab: "Manage communities →" link + empty state.
  - Joined "interop" community via `/communities`.
  - `/profile` → Communities: "interop" card with Leave button.
  - Click Leave: community removed, empty state restored.
  - 0 console errors.
