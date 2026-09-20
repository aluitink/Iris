# Unified Home Feed — Two Tabs + Bottom Strip + Community IA Rework

Forward-looking scope doc. Three related changes, one workstream:

1. **Two feed tabs** on `/home` (Posts / Communities) backed by a server-side `?source=` filter on the existing merged feed.
2. **A bottom control strip** (signed-in counterpart to the auth-bar) hosting the feed tabs + a notifications shortcut. **No compose button** — composing stays in the top menu.
3. **Community IA rework:** the top-menu "Communities" link becomes a **community management** page (my communities: create / delete / manage); the **Profile** page gains a **Communities** tab (followed communities, with unfollow + a jump-to-manage). The create form moves out of the old `/communities` browse page.

## Current state

- **`/home`** (`HomeTimeline.razor`) renders a single `PagedCollection` at `{actor}/feed` (the server-side merged followed feed).
- **The server already merges everything.** `FeedService.BuildFeedAsync` (`src/Iris.Server/Services/FeedService.cs:139`) walks every follow's outbox — local follows from the store, remote follows (including followed Lemmy communities, which are remote `Group` follows) over the wire — merged newest-first, de-duplicated, capped. It already applies block/mute (F-07), reply filtering (117.1), and the visibility filter (139.2-s5). **Community content is already in `{actor}/feed`.**
- **The gap is client-side.** `IsContentItem` keeps only `Create(Note|Article)`, `Announce`, bare `Note`/`Article` — **not `Page`**. Lemmy community posts are `Page`, so **they're silently dropped from the home feed today**.
- **The top menu** (`MainLayout.razor:18`) is: Home · New post · Notifications · Directory · Communities · Profile · Settings · Search · Log out. "New post" and "Communities" are both being repurposed (see §IA rework).
- **`/communities`** (`Communities.razor`) is a browse page: a create form (name/handle/description) + two tabs (Following / All on this instance) of `CommunityCard`s.
- **`/profile`** (`Profile.razor`) has tabs: Your posts · Replies · Likes · Followers · Following (+ Requests). No Communities tab.
- **No "delete community" server endpoint exists** (only `RemoveCommunityMember`). Creating a community is `CreateCommunityAsync`; there is no `DeleteCommunityAsync` / `DELETE /local/v1/c/{name}`. This is a gap the management page needs.

## Resolved design decisions

### Feed: server-side `?source=` filter (Option A)

Add `?source=people|communities` to `GET /u/{handle}/feed`:
- `people` → items whose `attributedTo` does **not** include a `Group`.
- `communities` → items whose `attributedTo` **does** include a `Group`.
- absent → full merged feed (back-compat).

**Classification (server-side, in `FeedService`):** an item is *from a community* when its `attributedTo` (or, for `Create`/`Announce`, the referenced object's `attributedTo`) includes a `Group`. **Resolved open questions:**
- **Boost (Announce) of a community post** → **community** (classify by the original object, not the announcer).
- **Local community post** (`attributedTo` [person, group]) → **community** (any `Group` in `attributedTo` wins).
- **Default tab** → **Posts** (people-first).

Server-side because the server has full documents, already does the hard parts, and each tab pages a full 20 (no short pages, no N+1).

### Bottom strip: tabs + notifications, NO compose

`FeedBar.razor` in `MainLayout` (signed-in, sticky bottom, mirrors the auth-bar):

```
┌──────────────────────────────────────────────┐
│  [ Posts ] [ Communities ]        [ 🔔 3 ]   │
│   ↑ left (/home only)            ↑ right     │
└──────────────────────────────────────────────┘
```

| Slot | Content | Visibility |
|---|---|---|
| **Left** | `Posts` · `Communities` tab pills (`tab`/`tab--active`) | `/home` only |
| **Right** | `🔔` + `NotificationBadge` → `/notifications` | **Always** (signed-in, every page) |

**No center `+ Post`** — the user wants the compose button removed from the strip. Composing stays reachable from the top menu's "New post" link and from contextual "Post to this community" buttons. **On non-`/home` pages the strip shows just `[ 🔔 ]`** (the bell is global; the tabs are home-only). The strip is never hidden while signed in.

Tab persisted in localStorage (`iris:homeTab` = `posts`|`communities`, default `posts`).

### Community IA rework

**Top menu change:** the "Communities" link → points to the **management** page (rename the nav item to "My communities" or keep "Communities" but route to manage). The "New post" link **stays** in the top menu (it's the primary compose entry; only the *strip* loses its compose button).

**`/communities` is repurposed as the management page** (no new route):
- **My communities** — the communities the signed-in actor **owns** (is in the `/c/{name}/owners` list for). "Created" is subsumed by "owned" (the creator is the initial owner; ownership can be transferred, so *owned* is the durable definition).
- **Create** — the existing create form (name/handle/description), moved here.
- **Delete / Leave** — per-row action, owner-only, **simple rule:**
  - If the actor is the **only** owner → **Delete** (tombstones the community). Confirm dialog naming the handle.
  - If the actor is **one of several** owners → **Leave** (demotes self via the existing `/c/{name}/owners/demote/{actorIri}`; the community is not deleted). No dialog needed (reversible).
  - This avoids the transfer/last-owner edge cases: you can only truly delete when you're the last owner; otherwise you just step down. **Requires a new server endpoint** `DELETE /local/v1/c/{name}` (last-owner-only) — see gap below. The demote path already exists.
- **Manage link** — each row links to `/community?iri=…` (the existing detail page with Owners/Peers/Requests/Members tabs).
- The old **Following / All-on-this-instance browse tabs are removed** from this page (browse lives in the directory's Communities tab; following is surfaced in Profile → Communities).

**`/profile` gains a Communities tab:**
- Lists the actor's **followed communities** (the `Group`s in the following collection — the same resolution logic `Communities.razor` currently has in `ResolveFollowingCommunities`, moved here).
- Each row: community name/handle + an **Unfollow** button (the existing `FollowButton` in unfollow state) + a **Manage →** link (to `/community?iri=…`, or to the management page if it's a local owned community).
- A header link: **Manage communities →** (to the management page) for create/delete.

**Why this split:** management (create/delete/own) is an *admin* concern → its own page. Following (browse/unfollow) is a *social* concern → belongs on the profile alongside Followers/Following. The old `/communities` page tried to be both and did neither well.

## The "delete community" gap

There is no server endpoint to delete a community. The management page's **Delete** action (last-owner-only) needs:
- `DELETE /local/v1/c/{name}` — **last-owner-only** (403/409 if more than one owner remains; the UI gates this, but the server enforces it). Same credential seam as the other `/local/v1/c/{name}/…` moderation routes.
- Behavior: tombstone the community actor (federated references 404/tombstone), remove it from local follows/memberships, stop serving its feed/members. Reuse the existing actor-deletion path if one exists (check `DELETE /local/v1/account` for the pattern); otherwise a community-specific delete that tombstones the `Group` + cleans up edges.
- Client: `DeleteCommunityAsync` on `ILocalModerationClient` (mirrors `RemoveCommunityMemberAsync`).
- The **Leave** path (multi-owner) needs **no new endpoint** — it uses the existing `POST /c/{name}/owners/demote/{actorIri}`.

This is a **server + client** sub-task; it's the only piece of the management page that isn't pure UI.

## Phases

### Phase 1 — Fix the `Page` drop (client)
1. Extend `IsContentItem` in `HomeTimeline.razor` and `Home.razor` to accept `Page` and `Create(Page)`.
2. Verify `ObjectView` renders `Page` (add a branch if missing).
3. **Verify:** follow a Lemmy community, post to it, confirm the post appears in `/home`.

### Phase 2 — Server `?source=` filter
1. `FollowFeedHandler` (`ActivityPubServerExtensions.cs:9170`): parse `?source=` (`people`/`communities`/empty), pass to `GetFeedAsync`.
2. `IFollowFeedService.GetFeedAsync` + `FeedService.GetFeedAsync`: add `string? source` param.
3. `FeedService`: private `IsFromCommunity(IObjectOrLink)` helper (attributedTo includes a `Group`; for `Create`/`Announce`, check the referenced object's attributedTo); apply the source filter after the existing query/type/visibility filters.
4. **Verify:** `?source=communities` → only community items; `?source=people` → only people; absent → all.

### Phase 3 — Bottom control strip (`FeedBar.razor`)
1. New `FeedBar.razor`: left tab pills (`/home` only) + right `🔔`+badge. No compose button.
2. Render in `MainLayout.razor` (signed-in branch, auth-bar position).
3. CSS: sticky bottom, border-top, auth-bar weight.
4. **Verify:** strip signed-in only; tabs only on `/home`; `🔔` → `/notifications`; hidden signed-out.

### Phase 4 — Wire the tabs
1. `HomeTimeline.razor`: read `iris:homeTab` (default `posts`), set `FeedIri` = `{actor}/feed?source=…`.
2. `FeedBar` ↔ `HomeTimeline` active-tab sync (localStorage + a re-render signal; decide mechanism during impl — a tiny scoped state service or a `CascadingValue` from `MainLayout`).
3. Per-tab empty states: Posts empty → "Follow people… [Directory →]"; Communities empty → "Follow communities… [My communities →]".
4. **Verify:** tab switch repoints the feed (no full reload); persists across reload; empty states correct.

### Phase 5 — Community management page
1. **Server:** `DELETE /local/v1/c/{name}` (**last-owner-only**) + `DeleteCommunityAsync` on `ILocalModerationClient`. (The gap — biggest piece.)
2. **`/communities` repurposed as the management page:** "My communities" list (communities the actor **owns**), create form (moved here), per-row action — **Delete** (confirm dialog) when last owner, **Leave** (demote, no dialog) when a co-owner — + Manage → link. Remove the old Following / All-on-instance browse tabs.
3. **Top menu:** "Communities" → management page. (Keep "New post" in the menu.)
4. **Verify:** create a community (appears in the list, actor is sole owner → Delete offered); add a second owner (via the detail page's Owners tab) → the row now offers Leave, not Delete; leave (demote) → gone from the list; with one owner left → Delete works (tombstoned, gone).

### Phase 6 — Profile Communities tab
1. `/profile` gains a **Communities** tab: the followed communities (move `ResolveFollowingCommunities` logic from `Communities.razor`), each row with `FollowButton` (unfollow) + Manage → link.
2. Header link: **Manage communities →** (to the management page).
3. **Verify:** follow a community → appears in Profile → Communities; unfollow from there → disappears; manage link works.

### Phase 7 — End-to-end verification
1. Follow a local community + a Lemmy community + a person; post to each.
2. `/home` Posts tab: the person's post (local + Lemmy community posts are in Communities).
3. `/home` Communities tab: the Lemmy `Page` + the local community post.
4. Reload: tab persists. `🔔` → notifications.
5. `/communities` (manage): create + delete a community.
6. `/profile` → Communities: followed communities, unfollow one, manage link.
7. Console: no 401/500 spam, no duplicate fetches.

## Resolved open questions

- **Strip on non-home pages:** shows just `[ 🔔 ]` (the bell is global; tabs are home-only). The strip is never hidden while signed in.
- **Route:** `/communities` is **repurposed** as the management page (no new route).
- **"My communities" definition:** communities the actor **owns** (is in the `/c/{name}/owners` list). "Created" is subsumed by "owned" (the creator is the initial owner; ownership can transfer, so *owned* is durable).
- **Delete vs Leave:** **any owner** can act; **Delete** (tombstone, confirm dialog naming the handle) is offered only when the actor is the **last** owner; **Leave** (demote self, reversible, no dialog) when a co-owner. Simple, no transfer/last-owner edge cases.

## Files to touch

**Feed (tabs + strip):**
- `apps/Iris.Web.Client/Components/Pages/HomeTimeline.razor` — `?source=` wiring, `Page` fix, empty states, tab read
- `apps/Iris.Web.Client/Components/Pages/Home.razor` — `Page` fix (public feed)
- `apps/Iris.Web.Client/Components/ObjectView.razor` — `Page` rendering (if missing)
- `apps/Iris.Web.Client/Components/FeedBar.razor` — **new** bottom strip
- `apps/Iris.Web.Client/Components/Layout/MainLayout.razor` — render `FeedBar`; menu "Communities" → manage
- app stylesheet — sticky-bottom strip CSS
- `src/Iris.Server/Services/IFollowFeedService.cs` — `string? source` param
- `src/Iris.Server/Services/FeedService.cs` — `IsFromCommunity` + source filter
- `src/Iris.Server/ActivityPubServerExtensions.cs` — `FollowFeedHandler` `?source=`

**Community IA:**
- `apps/Iris.Web.Client/Components/Pages/Communities.razor` — rework into the management page (create + my-communities + delete)
- `apps/Iris.Web.Client/Components/Pages/Profile.razor` — new Communities tab (followed + unfollow + manage link)
- `src/Iris.Server/ActivityPubServerExtensions.cs` — `DELETE /local/v1/c/{name}` handler
- `src/Iris.Client/ILocalModerationClient.cs` + `LocalModerationClient.cs` — `DeleteCommunityAsync`
