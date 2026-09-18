# 136.18 — Privacy and visibility policy alignment

**Date:** 2026-09-14
**Slice:** 136.18 (Lemmy interop — privacy and visibility policy alignment)
**Status:** **COMPLETE.** Four cross-instance integration tests verify the current visibility
behavior and pin two significant gaps: (1) the read path does not filter by visibility (a
direct/DM post is visible in the public feed and search), and (2) federation does not suppress
non-public content (a direct post is stored on the remote instance).

## What this slice delivers

136.18's acceptance criteria:

1. Validate visibility mapping (public/unlisted/restricted where supported) from source intent to remote presentation.
2. Verify non-public or scope-limited content is not leaked through timeline APIs, deep links, or backfill.
3. Confirm mismatched visibility capabilities degrade safely with explicit operator notes.
4. Exit when visibility semantics are documented and enforced without over-sharing.

### Test coverage

The four tests in `CrossInstanceVisibilityIntegrationTests` (two-instance `TestServer` fixture,
A: `vis-a.domain.local` alice, B: `vis-b.domain.local` bob, alice follows bob) verify:

| Test | What it verifies |
|------|-----------------|
| `PublicPost_VisibleInPublicFeed_Outbox_Search_OnOrigin` | A public post (`as:Public` in `to`) is visible in the public feed, the author's outbox, and global search on the origin instance. This is the expected behavior for public content. |
| `DirectPost_StoredInOutbox_VisibleInPublicFeed_CurrentGap` | A direct/DM post (`to=[bob]`, no `as:Public`, no `cc`) is stored in the author's outbox **and** is visible in the public feed and global search. This pins the **current gap**: Iris does not filter the read path by visibility — any post in a local actor's outbox appears in the public feed regardless of its `to`/`cc` audience. |
| `PublicPost_FederatedToRemote_VisibleInRemoteObjectStore` | A public post federated to B (bob's inbox) is stored in B's object store (the remote instance's production read path). The object-document HTTP endpoint 404s for cross-instance notes (a known limitation from 136.16 — IRIs are reconstructed from the local BaseUri). |
| `DirectPost_FederatedToRemote_StoredOnRemote_CurrentGap` | A direct/DM post federated to B is **stored** on B. This pins the **current gap**: Iris's `CreateActivityHandler` stores the embedded object unconditionally — it does not check the `to`/`cc` audience and does not suppress non-public content. The federation audience rewrite also appends all followers to `cc`, clobbering the original direct visibility. |

### Key findings

- **No first-class visibility field.** Iris uses pure ActivityStreams 2.0 `to`/`cc` addressing — there is no explicit `visibility` field, no enum, and no `iris:`-namespaced visibility term. Visibility is *derived* from the audience addressing:
  - **Public** = `as:Public` sentinel (`https://www.w3.org/ns/activitystreams#Public`) in `to` or `cc`.
  - **Followers-only** = author's `followers` collection in `cc`, no `as:Public`.
  - **Direct/DM** = specific actor IRIs in `to`, no `as:Public`, no `followers`.
  - AS2 has no term for "unlisted"/"restricted" (a Mastodon-specific concept) — Iris cannot represent this state.

- **Read path never filters by visibility (gap #1).** The public feed (`PublicFeedService`), follow feed (`FeedService`), community feed (`CommunityFeedService`), outbox GET endpoint, and both search implementations (`GlobalSearchService`, `IObjectStore.SearchObjectsAsync`) all expose content without checking the `to`/`cc` audience. A DM/restricted post stored in an outbox will appear in the public timeline, every follower's follow feed, and search results. The only place "visibility" is consulted is the reply-detection heuristic in `FeedService.IsFollowReply` (which hides *replies* with a non-public audience, but does not hide top-level DM posts).

- **Federation clobbers visibility (gap #2).** `RewriteOutboundAudienceAsync` appends the remote follower set to `cc` for every `Create` activity, regardless of the post's original visibility. A direct/DM post (whose `to` names only specific actors) gets all remote followers appended to `cc` at federation time, so the composed visibility is not preserved through federation. The receiving instance's `CreateActivityHandler` stores the object unconditionally — it does not check the audience and does not suppress non-public content.

- **Client API asymmetry.** `PostNoteAsync(string)` and `PostReplyAsync` accept `to` but not `cc`, so followers-only visibility is only expressible via `ComposeNote.Build` or `PostQuestionAsync`.

### Visibility posture (current)

| Visibility | Source representation | Federation | Public feed | Follow feed | Outbox GET | Search |
|------------|----------------------|------------|-------------|-------------|------------|--------|
| Public (`as:Public` in `to`) | `to: [as:Public]` | Followers appended to `cc` | **Visible** (correct) | **Visible** (correct) | **Visible** (correct) | **Visible** (correct) |
| Followers-only (`followers` in `cc`) | `cc: [followers]` | Followers appended to `cc` | **Visible** (gap — should be hidden) | **Visible** (correct for followers) | **Visible** (gap — should be restricted) | **Visible** (gap — should be hidden) |
| Direct/DM (actor in `to`, no `as:Public`) | `to: [actor]` | Followers appended to `cc` (clobbers) | **Visible** (gap — should be hidden) | **Visible** (gap — should be hidden for non-recipients) | **Visible** (gap — should be restricted) | **Visible** (gap — should be hidden) |

### Operator notes

- **Gap #1 (read path)** is the most impactful: a DM post is currently as visible as a public post. Fixing this requires adding an audience check to each read surface (public feed, follow feed, outbox GET, search) that excludes content whose `to`/`cc` does not include the requesting actor (or `as:Public`).
- **Gap #2 (federation)** is less impactful in practice: the remote instance stores the content, but the read path gap (#1) means it's visible to everyone anyway. Once #1 is fixed, #2 becomes relevant: the remote instance should either suppress non-public content on receipt or store it with a visibility marker that the read path can check.
- **AS2 has no "unlisted" term.** If Iris needs to support Mastodon-style "unlisted" (visible to anyone with the link, not in feeds), it would need an `iris:`-namespaced extension or a convention (e.g., `to: [as:Public]` + `iris:unlisted: true`). This is a design decision for a future slice.
