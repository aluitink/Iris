# Community Simplification — Unify Members with Followers (Lemmy-shape)

Forward-looking scope doc. Iris currently models a community with **two separate relationship sets** — `members` (a `Join` activity edge) and `followers` (a `Follow` activity edge) — plus a third, `community-follow` (peering). Lemmy has only **one**: followers, branded "members." This workstream collapses Iris to the Lemmy shape: **members = followers**, one button, one approval gate, one feed population — and keeps peering (community-follows-community) as the single owner-only extra.

**No data migration needed.** This is early development; there is no real data with `CommunityMember` edges. We delete the member axis rather than migrate it.

## Current state (confirmed in code)

A community has **three** relationship sets:

| Concept | Edge kind | Activity | Endpoint | UI today |
|---|---|---|---|---|
| **Member** | `CommunityMember` (8) — `EdgeEntity.cs:84` | `Join`/`Leave` → `MembershipActivityHandler` → `AddMemberAsync` | `GET /c/{name}/members` (`ActivityPubServerExtensions.cs:9611`) | `JoinButton` (detail page only) |
| **Follower** | `CommunityFollower` (11) — `EdgeEntity.cs:99` | `Follow`/`Undo` → `FollowActivityHandler` → `AddFollowerAsync` | `GET /c/{name}/followers` (`:1233`) | `FollowButton` (card + detail) |
| **Community-follow** (peering) | `CommunityFollow` (10) — `EdgeEntity.cs:94` | `Follow` authored *by the community* → `CommunityFollowHandler` (`:10451`) | `GET /c/{name}/following` | "Peers" tab (owner-only) |

**The problems:**
1. **Follow and Join are independent.** Joining does *not* follow (`FollowActivityHandler.cs:38-41` is explicit: "a follow of a community is not a membership grant… membership is a separate, local-administered relationship"). So a *member* doesn't see the community's posts in their home feed unless they *also* follow — the opposite of Lemmy.
2. **Two different populations feed two different surfaces.** The community's own `/feed` reads **members'** outboxes (`CommunityFeedService.cs:123` → `GetMembersAsync`); a user's home feed reads their **follows**. Member ≠ follower means the community feed and the home feed are populated by different people.
3. **The card is broken.** `CommunityCard.razor:10` shows only `FollowButton`, with a stale comment (16-22) claiming it "doubles as Join/Unjoin" — but the server never grants membership on follow. From the card you can follow but not join.
4. **`manuallyApprovesMembers` gates the Join, not the Follow** (`MembershipActivityHandler.cs:125-142`) — so the approval flow sits on the membership axis, the one users don't use to see content.
5. **Internally inconsistent with Lemmy interop.** For *remote Lemmy* communities, Iris already treats members = followers (`ResolveMembersIri` → `/followers`, `IrisDocumentExtensions.cs:286`). So Iris's own communities (separate members/followers) don't match the shape it reads from Lemmy.

## Target shape (Lemmy-correct)

**One relationship: follow. "Members" = the community's followers, branded.**

- A user **follows** a community (`Follow` activity). No separate `Join`.
- **Members** = the community's **followers collection** (`/c/{name}/followers`), labeled "Members" in the UI.
- Following a community → the user sees its posts in their home feed (already true via the follow edge).
- The community's own `/feed` is populated by its **followers'** outboxes (not a separate member set).
- **`manuallyApprovesMembers`** (Lemmy: "requires approval") gates the **Follow**: a follow becomes a *pending request* until an owner accepts. The existing `/c/{name}/requests` (join requests) becomes **follow requests**.
- **Peering** (community-follows-community) stays as a separate **owner-only** feature — the community's *outgoing* follow, distinct from a person following the community. Keep the `CommunityFollow` edge; just make the control surface clear (owner-only).
- The `members` **document term** is kept (Lemmy-compatible) but points at the followers collection.

### What's removed
- The `CommunityMember` edge kind (8) — stop using it (keep the enum value reserved to avoid renumbering).
- `Join`/`Leave` handling as a *membership* grant — a `Join` to a community is **mapped to `Follow`** (back-compat for any client that sends Join); `Leave` → `Undo`.
- The separate `/c/{name}/members` *store* path — it serves the **followers** collection.
- The `CommunityJoinRequest` edge (9) as a *join* request — it becomes a **follow request** (same edge, re-gated on Follow).
- `JoinButton.razor` — deleted. `FollowButton` is the only button (card + detail).
- The stale `CommunityCard` comment — fixed.

### What's kept
- `Follow`/`Undo` for persons following a community (the single relationship).
- `CommunityFollow` (peering) for community-follows-community — owner-only.
- The `members` document term (Lemmy-compatible, → followers).
- The `/c/{name}/requests` surface (now follow-requests), `/c/{name}/owners`, `/c/{name}/mutes`, `/c/{name}/blocks`.

## Relationship to the unified-home-feed workstream

This workstream **enables and simplifies** the [unified-home-feed](unified-home-feed.md) plan:

- The home feed's **Communities tab** (`?source=communities`) shows content from communities the user **follows**. Today that's already the follow edge (the merged feed walks follows' outboxes) — so the home feed is *already* correct for the Lemmy shape. The simplification makes the *rest* of the app match: the "Members" list, the approval gate, and the single button all align with "follow."
- The **management page** (unified-home-feed Phase 5) and **Profile → Communities tab** (Phase 6) are built on "follow a community" as the primitive. The simplification removes the Join/Leave duality they'd otherwise have to reconcile.
- **Sequencing:** the simplification's server changes (Phases 1-3 below) should land **before or alongside** unified-home-feed Phases 5-6 (management page + Profile tab), so those UI surfaces are built on the unified shape from the start. The feed tabs (unified-home-feed Phases 1-4) are independent and can proceed in parallel.

## Phases

### Phase 1 — Server: map Join/Leave to Follow/Undo
1. `MembershipActivityHandler`: a `Join` to a `Group` → record a **follow** (the `CommunityFollower` edge + the reciprocal `CommunityFollow`), not a `CommunityMember`. A `Leave` → remove the follow. (Back-compat: any inbound `Join` is treated as a `Follow`.)
2. Stop writing `CommunityMember` edges. Keep the enum value (8) reserved/unused.
3. **Verify:** a client sending `Join` to a local community results in a follower (appears in `/followers`), not a member.

### Phase 2 — Server: `/members` serves followers; community feed reads followers
1. `CommunityMembersHandler` (`:9611`) → serve the **followers** collection (`GetFollowersAsync`), not `GetMembersAsync`. (Or redirect `/members` → `/followers`; prefer serving followers directly so the Lemmy-compatible term works.)
2. `CommunityFeedService` (`:123`, `:342`) → read **followers'** outboxes (`GetFollowersAsync`), not members'.
3. The `members` document term → points at the followers collection IRI.
4. **Verify:** `/c/{name}/members` returns the followers; the community feed is populated by followers' posts.

### Phase 3 — Server: `manuallyApprovesMembers` gates the Follow
1. `FollowActivityHandler`: when the target is a `Group` with `manuallyApprovesMembers` set, an inbound `Follow` → record a **pending follow request** (reuse the `CommunityJoinRequest` edge, re-gated on Follow) instead of granting the follow. Owner Accept/Reject → grant/deny the follow.
2. The `/c/{name}/requests` surface now lists **follow requests** (same endpoint, same accept/reject routes — `:1414-1422`).
3. **Verify:** follow a gated community → pending (not a follower yet); owner accepts → becomes a follower + appears in the home feed; owner rejects → not a follower.

### Phase 4 — Client: single Follow button
1. Delete `JoinButton.razor`.
2. `CommunityDetail.razor:45-49` → remove the `JoinButton`; keep the single `FollowButton`.
3. `CommunityCard.razor` → fix the stale comment (the Follow button *is* the only action; there is no separate Join).
4. `UiContext.IsMemberAsync` → repoint at the followers collection (or alias to `IsFollowingAsync`); remove the members-collection walk.
5. **Verify:** card + detail show one Follow button; following a community makes the user a "member" (appears in the Members list) and feeds their home timeline.

### Phase 5 — Control surfaces (with unified-home-feed Phases 5-6)
Build on the unified shape:
- **Management page** (`/communities`, repurposed — unified-home-feed Phase 5): for owned communities — create / Delete (last owner) / Leave (co-owner) + a **Peers** section (add/remove community-follows, i.e. which remote communities this community replicates). The peer section is the owner's control over peering.
- **Detail page** (`/community?iri=…`): for owned communities — the **Peers** tab (existing, `:237-303`) is the peer-management surface (add/remove). For non-owned — the single Follow button + the **Members** (followers) list.
- **Profile → Communities** (unified-home-feed Phase 6): the user's followed communities + Unfollow + Manage link.
- **Verify:** owner follows a remote community (peer) → its content appears in the local community's feed; owner removes the peer → content stops appearing; a member follows/unfollows → appears/disappears in the Members list + their home feed.

### Phase 6 — Lemmy interop re-verification
1. Follow a **Lemmy** community → it appears in the home feed (Communities tab) + the user is in its `/followers` (read via `ResolveMembersIri`).
2. A Lemmy user follows an **Iris** community → the follow is recorded (Iris now speaks the same members=followers shape Lemmy expects).
3. **Verify:** the Iris↔Lemmy member/follow round-trip is symmetric (both sides treat members as followers).

### Phase 7 — Cleanup
1. Remove now-dead code: `AddMemberAsync`/`GetMembersAsync`/`RemoveMemberAsync` on `ICommunityStore` (or repoint them at followers), the `CommunityMember` edge usages, `RequestJoinAsync`/`RequestLeaveAsync` on the client (or alias to Follow/Undo).
2. Update doc comments that assert "membership is separate from follow" (`FollowActivityHandler.cs:38-41`, `CommunityCard.razor:16-22`).
3. Update the [INTEROP_CONFORMANCE_MATRIX](../reference/INTEROP_CONFORMANCE_MATRIX.md) (members=followers row).
4. **Verify:** `dotnet test --filter "Category!=Slow"` green; no references to the removed member axis.

## Resolved open questions

- **`Join` mapping vs rejection:** **map** inbound `Join`→`Follow` (and `Leave`→`Undo`). Rationale: a ~10-line adapter in `MembershipActivityHandler` is near-zero cost, and it's strictly more correct — a federated peer that still sends `Join` keeps its relationship instead of silently losing it. Rejecting (400/410) would be cleaner in the abstract but risks breaking an interop peer for no benefit, since we control our own client (which will send `Follow`).
- **`CommunityJoinRequest` edge reuse:** **reuse edge kind (9)** for follow-requests. Rationale: it's already wired into the existing `/c/{name}/requests` list/accept/reject routes (`ActivityPubServerExtensions.cs:1414-1422`), so reusing means **zero new endpoint work** — just re-gate the request creation on `Follow` (Phase 3) and rename in comments. A new `CommunityFollowRequest` kind would touch the EF model, the in-memory store, and the request routes for no functional gain; renumbering edges is also riskier.
- **Peering UI location:** **keep both** the management-page Peers section and the detail-page Peers tab. Rationale: they call the same `FollowAsCommunityAsync`; the management page is the "my communities" overview (see all my communities' peers at a glance), the detail tab is the per-community deep surface (manage peers while viewing that community). Duplicating a read-only list + one add/remove button is cheaper than forcing owners into a single location, and both surfaces are already partially built.

## Files to touch

**Server (Phases 1-3, 7):**
- `src/Iris.Server/Inbox/MembershipActivityHandler.cs` — Join/Leave → Follow/Undo
- `src/Iris.Server/Inbox/FollowActivityHandler.cs` — `manuallyApprovesMembers` gates Follow; fix the "membership is separate" comment
- `src/Iris.Server/ActivityPubServerExtensions.cs` — `CommunityMembersHandler` → followers; `/requests` = follow-requests; `CommunityFollowHandler` unchanged (peering stays)
- `src/Iris.Server/Services/CommunityFeedService.cs` — read followers' outboxes
- `src/Iris.Server/Stores/ICommunityStore.cs` + `EfCommunityStore.cs` + `InMemoryCommunityStore.cs` — repoint/remove member methods
- `src/Iris.Server.Data/Entities/EdgeEntity.cs` — reserve `CommunityMember` (8); reuse `CommunityJoinRequest` (9) for follow-requests

**Client (Phases 4, 7):**
- `apps/Iris.Web.Client/Components/JoinButton.razor` — **delete**
- `apps/Iris.Web.Client/Components/Pages/CommunityDetail.razor` — remove JoinButton; Peers tab (owner) stays
- `apps/Iris.Web.Client/Components/CommunityCard.razor` — fix stale comment
- `apps/Iris.Web.Client/Ui/UiContext.cs` — `IsMemberAsync` → followers
- `src/Iris.Client/ActivityPubClient.cs` + `IActivityPubClient.cs` — `RequestJoinAsync`/`RequestLeaveAsync` (alias to Follow/Undo or remove)

**UI control surfaces (Phase 5, with unified-home-feed):**
- `apps/Iris.Web.Client/Components/Pages/Communities.razor` — management page + Peers section
- `apps/Iris.Web.Client/Components/Pages/Profile.razor` — Communities tab (followed + unfollow)
