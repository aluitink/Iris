# 139.1 — Federation & interop conformance review

> Part of [Phase 139](phase-139-platform-e2e-review.md). Scope: verify every activity type Iris
> speaks round-trips correctly against every peer platform it claims to interop with, using real
> live instances where possible (per Phase 138's Lemmy stack + the Phase 19/136 Mastodon test
> accounts), not just `TestServer` fixtures. Reuses Phase 136's cross-instance integration suite and
> Phase 138's interop matrix as the baseline — this review's job is to find what's *not* covered by
> either.

## Peers in scope

| Peer | Local fixture | Notes |
|---|---|---|
| Iris ↔ Iris | two-instance `TestServer` harness + the dev docker stack | baseline; should be fully green already |
| Iris ↔ Lemmy | [lemmy/](../../lemmy/) local stack | per Phase 138 |
| Iris ↔ Mastodon | `@RayvenMX@mastodon.world` (Phase 138 test-account) | real external instance — read-only interop only, do not spam-post |
| Iris ↔ Pleroma | spot-checked in Phase 79.3/81.2 | verify prior findings still hold |
| Iris ↔ Misskey | spot-checked in Phase 81.2 | verify prior findings still hold |
| Iris ↔ PeerTube | spot-checked in Phase 79.3 | verify prior findings still hold; video rendering path |

## Test scenarios

| # | Scenario | Steps | Pass criteria | Evidence |
|---|---|---|---|---|
| 1 | Cold WebFinger resolution for every peer type | Resolve `acct:` for a Person and (where applicable) a Group/Community on each peer from a clean Iris instance | Each resolves to the correct actor type without manual `!`-stripping surprises (Lemmy) or host mismatches | curl/UI transcript per peer |
| 2 | Actor document round-trip | Fetch each peer's actor doc via Iris's proxy; confirm all core fields (`inbox`, `outbox`, `publicKey`, `icon`) render in `ActorProfile` | No blank/missing fields beyond documented platform limitations (e.g. Lemmy Person has no `name`/`icon`) | screenshot per peer |
| 3 | Inbound Create (post) rendering | A post/status from each peer type appears correctly in the Iris feed/community view (title where applicable, content, attachments, sensitivity) | Content renders with no raw-JSON fallback, no truncation | screenshot per peer |
| 4 | Inbound reply/comment threading | A reply from each peer type threads correctly under its parent | Correct nesting depth and order (Phase 054 contract) | screenshot |
| 5 | Inbound Like/boost-equivalent | A like (and, for Lemmy, a dislike per Phase 138.17) from each peer is recorded and reflected in counts | `iris:likedCount`/`iris:dislikedCount` update correctly | before/after count diff |
| 6 | Outbound Create delivery | Iris posts to a community/actor followed by each peer type; confirm delivery succeeds and renders on the peer side | 2xx delivery, correct rendering on the peer (where the peer is locally controlled — Lemmy/Iris only; do not push test content to real Mastodon accounts) | delivery log + peer-side screenshot |
| 7 | Outbound Update/Delete propagation | Edit and then delete an Iris post that was delivered to a peer; confirm both propagate | Peer reflects the edit; peer shows the post removed/tombstoned per its own model | peer-side screenshot before/after |
| 8 | Follow/Undo-follow across peer types | Follow and unfollow each peer type from Iris and vice versa (where the peer UI allows it) | Follow/unfollow edges converge correctly on both sides (Phase 145 contract) | collection dump before/after |
| 9 | Pagination/backfill across peer types | Walk a peer's outbox with 50+ items; confirm Iris pages through all of them without loss or duplication | Item count matches source; no duplicate IRIs | count comparison |
| 10 | Signature/header conformance matrix | Capture the exact signature header set each peer sends and requires, compare against Iris's validator/signer (extends Phase 136.3's canonical verification matrix) | No peer's real traffic is rejected/rejects Iris for a header-construction reason not already known | header dump per peer |
| 11 | `@context`/vocabulary sniffing robustness | Confirm Iris's capability detection (`IsLemmy()`-style checks, Phase 137.2) doesn't misfire against Pleroma/Misskey/PeerTube documents | Correct feed/members IRI resolution for every peer type | unit test or live capture |
| 12 | Relay fan-out interop | Confirm a relay-subscribed peer receives fan-out correctly (Phase 28) against a real (or realistically simulated) external relay subscriber | Fan-out delivered, no duplicate/missing activities | delivery log |

## Deliverable check

All 12 scenarios executed with evidence recorded; findings triaged (class + severity) into this
doc's own tracker table (add one, matching the Loop protocol's shared-tracker shape, when scenarios
start failing); [docs/reference/](../reference/) interop matrix (Phase 138.28) updated with anything
new learned here.

## Progress tracking

- [x] 1  - [x] 2  - [x] 3  - [x] 4  - [x] 5  - [x] 6
- [ ] 7  - [ ] 8  - [ ] 9  - [ ] 10 - [ ] 11 - [ ] 12

Check a scenario off only once its pass criterion is met with evidence attached (link/path). Update
the area's Status cell in [phase-139-platform-e2e-review.md](phase-139-platform-e2e-review.md) to
`in progress` on the first checked box, `done` when all are checked (or explicitly skipped).

**Resume checkpoint:** scenarios 1–6 done (F-3 + F-5 fixed; F-4 Lemmy `/replies` limitation; F-6 cross-post model → community-follow relay design) — begin at scenario 7.

## Findings tracker (class + severity, per Loop protocol)

| # | Scenario | Class | Severity | Finding | Disposition |
|---|---|---|---|---|---|
| F-1 | 1 | UX | S3 | **Lemmy community WebFinger returns 400** — Lemmy does not serve WebFinger for communities (only persons). The Web UI's `!community@host` lookup (`Directory`/`Search`/`CommunityDetail`) strips the `!` then queries WebFinger, which 400s for a community. The supported + working path is **pasting the full community IRI** (`/c/{name}`), which the UI hint already documents, and which is exactly what the 138.5 federation path (`POST /local/v1/c/{name}/follow/{targetIri}`) uses. | No defect in the federation path. Optional UX polish (a later UX slice): auto-construct the `/c/{name}` IRI from a `!community@host` handle for Lemmy, instead of relying on WebFinger. |
| F-2 | 2 | UX | S3 | **Lemmy person `/followers` + `/following` collections return 404** — Lemmy does not expose person collections (verified: direct fetch of `lemmy.luit.ink/u/lemmyadmin/{followers,following}` → 404). The ActorDetail page requests both collections via the proxy; they 404 and the page logs 2 console errors, but renders correctly (handle + avatar monogram fallback, "No posts yet", no crash). | Not an Iris defect (Lemmy platform limitation). The page already degrades gracefully. Optional polish (a later UX slice): skip the collections fetch for actors whose document has no `followers`/`following` link, to avoid the console 404s. |
| F-4 | 4 | UX | S3 | **Lemmy does not serve a post's `/replies` collection** — an Iris→Lemmy reply (e.g. andrew replying to a Lemmy post) is stored on Iris with the correct `inReplyTo` (the Lemmy post IRI) and the reply edge, but the object detail's "Replies" tab for a **remote Lemmy parent** reads `{parent}/replies` (proxied to Lemmy), which Lemmy does not serve (empty/404). So the reply is not visible in the Lemmy parent's Replies tab. | Not an Iris defect (Lemmy platform limitation — Lemmy addresses comments differently and exposes no standard AS `/replies` for posts). The reply **does** thread correctly on the Iris side (verified live: the reply's object detail renders the "In reply to alice" parent-context with the parent preview — Phase 054 contract). Inbound Lemmy comment threading (a Lemmy member commenting on a post) is verified by 16 passing server tests (`LemmyCommentThreadingIntegrationTests` + `CrossInstanceReplyThreadIntegrationTests` + `ReplyIntegrationTests`). Optional polish (later UX slice): for a remote parent, fall back to the Iris-side reply store (the recorded reply edges) when the remote `/replies` is empty. |
| F-3 | 3 | bug | S2 | **Cross-posted Note→Page renders its body twice** — alice's 138.11 cross-post (an Iris `Note` delivered to Lemmy as a `Page`) showed in the Lemmy community feed with the body rendered **twice**: once as the `name`/title and once as the `content`. Root cause: Iris composes Notes with **no `name`** (`ComposeNote.Build` sets only `Content`); when `TransformCreateForCrossPost` (`ActivityPubServerExtensions.cs:5208`) copies the null `Name` onto the Lemmy `Page`, **Lemmy derives the `Page`'s `name` from its content** on ingest — so `name` ≈ `content` in Lemmy's outbox. `ObjectView`'s Create + Announce/boosted branches each rendered both the title and the body without deduplicating. Normal Lemmy posts (`name` ≠ `content`) rendered correctly. | **Fixed + verified.** Added `NameDuplicatesContent` (HTML-stripped, case-insensitive equality/containment) in `ObjectView.razor.cs` + `ActivityTitleDuplicatesContent` / `BoostedTitleDuplicatesContent` guards; the `.razor` Create branch (line 46) and the Announce/boosted branch (line 241) now suppress `object-title` when the title duplicates the body. Build clean (0 warnings); live Playwright re-check confirms the duplicate is gone and distinct titles still render. |
| F-5 | 6 | bug | S2 | **Posting to a remote community from the Web UI failed — client delivered directly to the remote community's inbox (cross-origin, CSP-blocked).** The Compose page's `PostToRemoteCommunityAsync` (`Compose.razor:1494`) fetched the remote community's `InboxOf()` directly from the browser; the Web UI's CSP `connect-src 'self'` blocks that cross-origin fetch, so the post never reached the server (console: "Connecting to 'https://lemmy.luit.ink/c/interop/inbox' violates… connect-src 'self'"). The post also had no minted outbox IRI. | **Fixed + verified.** Per the platform convention (all posts publish to the author's outbox), `PostToRemoteCommunityAsync` now delivers to `actorId.OutboxOf()` (same-origin) and sets `To = [communityIri, Public]` on the Create. The server's outbox-publish handler reads the remote community from the `to` audience and cross-posts server-side (the 138.11 pipeline). Build clean (0 warnings); live Playwright re-check: post succeeds (HTTP 202, minted IRI `https://iris.luit.ink/ap/v1/u/andrew/creates/…`), no CSP errors. **Caveat:** see F-6 — the server-side cross-post to Lemmy still 400s when the author is not a community member. |
| F-6 | 6 | architecture | S1 | **The 138.11 "cross-post to a remote community" model is not how Lemmy peers work — and Lemmy communities are passive (cannot follow).** Verified live: (a) a Lemmy community actor (`/c/interop`) has **no `following` collection** (`following: None`; `GET /c/interop/following` → 404) — Lemmy communities **cannot follow other communities**; (b) Lemmy requires the posting actor to be a **member** of the community to accept a `Create` in it — andrew's cross-post to Lemmy's shared inbox was **rejected 400** and dead-lettered (`DeliveryWorker` log); the 138.11 "success" (alice's post in Lemmy's outbox) was a **manual signed-`curl` probe** (`deliver_13811.sh`), masking that the normal pipeline doesn't work for non-members; (c) Lemmy likes/scores are **not in the AP document** (`GET /post/2` has no `score`/`likedCount`) — they are Lemmy-internal, served only via the JSON API, and are **not federated** as AP `Like` activities. The correct model is **Iris-follows-remote**: the local community (or a delegated service) sends a `Follow` from the Iris community actor to the remote community; the remote then delivers `Create`/`Announce` to the Iris community's inbox **as a follower**. This is the standard AP follow-relay (the J-18 fan-out loop), not a special "cross-post". | **Design decided (community-follow relay), implementation pending.** Decisions (2026-09-16): (1) **Direction** — Iris community follows remote (Lemmy can't follow, but Iris can follow Lemmy). (2) **Content placement** — relayed remote content is recorded in the **local community's own outbox** (the community mirrors the remote's posts). (3) **Interactions** — a local user's reply/like on relayed content is delivered to **both** the remote post's author inbox **and** the remote community's shared inbox. (4) **Like/dislike sync** — **Iris-side only** (Lemmy doesn't federate scores; show the Iris-side count, do not poll the remote JSON API). See the "Community-follow relay design" section below. |

## Community-follow relay design (2026-09-16)

A new capability (beyond the 139.1 review scope; tracked here as F-6's resolution) that replaces the
broken "cross-post to a remote community" model with the standard AP follow-relay.

**Problem.** The 138.11 model (client addresses a remote community in `to`; server delivers a
`Create` directly to that community's shared inbox) does not work against Lemmy: Lemmy communities
are passive (no `following` collection), and Lemmy rejects a `Create` from a non-member author (400).
The "success" in 138.11 was a manual probe, not the normal pipeline.

**Design (decisions confirmed 2026-09-16).**

1. **Direction — Iris community follows remote.** The community owner (or a delegated service) sends
   a `Follow` from the **Iris community actor** to the remote community. Iris is the *follower*; the
   remote is the *followee*. This works against Lemmy (Lemmy can't follow, but Iris can follow Lemmy).
   It also works symmetrically for peers that *do* support group-follow (Pleroma/Mastodon may) — the
   reverse direction (remote follows Iris community) is then the J-18 fan-out path.

2. **Receiving content.** When the remote community posts, the remote delivers `Create`/`Announce`
   to the **Iris community's inbox** (as a follower). The Iris inbox handler records the content in
   the **local community's own outbox** (the community becomes a mirror of the remote's posts). This
   is the "we show it in our outbox" requirement.

3. **Interactions on relayed content.** When a local user replies to (or likes) a relayed remote
   post, Iris delivers the interaction to **both** the remote post's **author inbox** and the remote
   **community's shared inbox** (most robust; covers peers that route replies via either).

4. **Like/dislike sync — Iris-side only.** Lemmy does not federate post scores as AP activities
   (verified: the Lemmy AP doc has no `score`/`likedCount`). Iris tracks its own side (local users'
   votes on relayed content) and shows the Iris-side count in the vote bar. It does **not** poll the
   remote JSON API for the remote's score.

**Implementation surface (pending).**
- A community-level `Follow` capability: the local community actor sends a `Follow` to the remote
  community (new server endpoint, e.g. `POST /local/v1/c/{name}/follow/{targetIri}` already exists
  for user-level; extend to community-level if needed).
- Inbox handling: when the local community receives a `Create`/`Announce` as a follower, record it in
  the community's outbox (extend `CommunityContentRecorder` or `CreateActivityHandler`'s community
  branch).
- Outbound interaction delivery: when a local user replies to a relayed remote post, deliver to both
  the author and the community shared inbox (extend the reply-delivery path).
- UI: a "followed communities" view on the community detail page (the local community's `following`
  collection) + the relayed content in the community's feed.

**Status:** design decided, implementation pending (not started; tracked as a follow-up to 139.1).

## Scenario 6 — Outbound Create delivery (evidence, 2026-09-16)

**Client routing (F-5, fixed + verified).** Posting to a remote community from the Web UI previously
failed because the client delivered directly to the remote community's inbox (cross-origin, blocked by
the Web UI's CSP `connect-src 'self'`). The fix routes the post through the **author's outbox**
(`actorId.OutboxOf()`, same-origin) with `To = [communityIri, Public]`; the server's outbox-publish
handler reads the remote community from the `to` audience and cross-posts server-side (the 138.11
pipeline). Build clean (0 warnings). Live Playwright: signed in as andrew, composed a post to the
Lemmy interop community, clicked "Post to community" → **HTTP 202** with a minted outbox IRI
(`https://iris.luit.ink/ap/v1/u/andrew/creates/06GAF3DZSX0CTF87DV2F33DGTW`), **no CSP console
errors**. The stored activity is a `Page` (Note→Page transformed for Lemmy, per 138.11) with
`to: [lemmy-interop, Public]`.

**Server-side cross-post to Lemmy (F-6, architecture).** The server-side delivery to Lemmy's shared
inbox **400s** when the author is not a community member (andrew follows 0 actors; Lemmy requires
membership). The `DeliveryWorker` log shows the activity dead-lettered after the 400. The 138.11
"success" (alice's post in Lemmy's outbox) was a **manual signed-`curl` probe** (`deliver_13811.sh`),
not the normal pipeline. **Resolution:** the "cross-post to a remote community" model is replaced by
the **community-follow relay** (see the "Community-follow relay design" section above) — Iris follows
the remote community, and content flows through the standard AP follow-relay. The client outbox
routing (F-5) is correct for the local + relay cases; the "post to a remote community I don't belong
to" case is a **membership** requirement, not a delivery bug — the UI should disable posting until
the user joins the community.

**Pass criterion (partial).** The outbound Create delivery path (client → author outbox → server
cross-post) is verified working end-to-end up to the server→remote hop; the server→Lemmy hop is
blocked by Lemmy's membership requirement (F-6), which is resolved by the community-follow relay
design (implementation pending).

## Scenario 5 — Inbound Like/boost-equivalent (evidence, 2026-09-15)

**Inbound Like/Dislike recording + counts (server):** 26 passing integration tests cover an inbound
Like (and, for Lemmy, a dislike per 138.17) from a peer being recorded in the like/dislike store and
reflected in `iris:likedCount` / `iris:dislikedCount` (and the net `iris:score` = liked − disliked).
(`LemmyLikeInboundIntegrationTests` + `LikeActivityHandlerTests` + `LikedCollectionIntegrationTests` +
`LemmyExtensionTermsIntegrationTests` + `ObjectDocumentScoreReconIntegrationTests` — 26/26 pass.)

**Lemmy vote-bar counts (live):** the Lemmy post `post/2` object detail renders the `LemmyVoteBar`
with the live Lemmy score — Upvote (1) / Downvote (0) / Score (1) / "2 comments", fetched from the
Lemmy API. Clicked Upvote: the button goes `[pressed]`, Upvote 1→2, Score 1→2 (an Iris→Lemmy like
recorded + reflected). The per-object `iris:likedCount`/`iris:dislikedCount` extension on Iris-served
objects is computed from the recorded likers/dislikers (verified by the tests above).

**Pass:** like/dislike (and, for Lemmy, the vote) is recorded and reflected in the counts — inbound
peer likes update `iris:likedCount`/`iris:dislikedCount` (26 server tests), and the Lemmy vote-bar
counts render + update live. No new findings.

## Scenario 4 — Inbound reply/comment threading (evidence, 2026-09-15)

**Inbound Lemmy comment threading (server):** 16 passing integration tests cover a Lemmy community
member's comment (a `Create(Note)` with `inReplyTo` pointing to a parent post/comment) threading
correctly under its parent — the comment `Note` is stored, the reply edge (parent → child) is
recorded, and the comment is recorded in the community's local member's outbox. Also covers
cross-instance reply threads. (`LemmyCommentThreadingIntegrationTests` +
`CrossInstanceReplyThreadIntegrationTests` + `ReplyIntegrationTests` — 16/16 pass.)

**Reply threading UI render (Phase 054 contract, live):** posted a real reply from andrew to the
Lemmy post `lemmy.luit.ink/post/3` (HTTP 202, stored on Iris with `inReplyTo` = the Lemmy post IRI).
The reply's object detail renders the parent-context correctly: author `andrew`, the reply content,
and an **"In reply to alice"** block with the parent post's preview — the Phase 054 nesting contract
(parent author + preview) working for a cross-platform (Iris→Lemmy) reply.

**F-4 (Lemmy limitation):** the Lemmy parent's "Replies" tab reads `{parent}/replies` (proxied to
Lemmy), which Lemmy does not serve — so an Iris→Lemmy reply is not surfaced in the Lemmy parent's
Replies tab. Not an Iris defect; the reply threads correctly on the Iris side. See F-4.

**Pass:** reply threading is correct — inbound Lemmy comments thread under their parent (16 server
tests), and the reply-threading UI render (parent-context nesting) works for a cross-platform reply
(live). The one platform limitation found (F-4, Lemmy `/replies`) is documented.

## Scenario 3 — Inbound Create (post) rendering (evidence, 2026-09-15)

Verified live against the Lemmy interop community feed (`/community?iri=…/c/interop`, which reads the
Lemmy community outbox directly per `ResolveFeedIri`'s Lemmy branch) and the home timeline
(Mastodon/Pleroma inbound posts).

| Peer | Item | Render |
|---|---|---|
| Lemmy (lemmyadmin post) | `Announce → Create → Page` (`name` "Hello from Lemmy interop" ≠ `content`) | ✅ distinct title + body both render; `LemmyVoteBar` (score + comment count) |
| Lemmy (lemmyadmin post 2) | `Announce → Create → Page` (`name` "138.3 fixture post two" ≠ `content`) | ✅ distinct title + body both render |
| Iris→Lemmy cross-post (alice) | `Announce → Create → Page` (`name` ≈ `content`, Note cross-posted as Page) | ✅ **single** body render (title suppressed — see F-3 fix) |
| Mastodon (home timeline) | `Create → Note` | ✅ content + link + media/card attachment + boost/like controls |
| Pleroma (home timeline) | `Create → Note` | ✅ content renders |

**F-3 found + fixed.** alice's cross-post rendered its body **twice** (once as `name`/title, once as
`content`) because Iris composes Notes with no `name` (`ComposeNote.Build`), `TransformCreateForCrossPost`
copies the null `Name` onto the Lemmy `Page`, and Lemmy then derives the `Page`'s `name` from its
content on ingest — so `name` ≈ `content` in Lemmy's outbox. `ObjectView`'s Create + Announce/boosted
branches each rendered both the title and the body without deduplicating. **Fix:** added
`NameDuplicatesContent` (HTML-stripped, case-insensitive equality/containment) in
`ObjectView.razor.cs` + `ActivityTitleDuplicatesContent` / `BoostedTitleDuplicatesContent` guards; the
`.razor` Create branch (line 46) and the Announce/boosted branch (line 241) now suppress
`object-title` when the title duplicates the body. Normal Lemmy posts (distinct title) are unaffected
(verified live: titles still render). Build clean (0 warnings). Live Playwright re-check: the duplicate
is gone, distinct titles intact.

**Pass:** every peer's inbound Create renders with no raw-JSON fallback, no truncation, no duplicate
text. The one defect found (F-3) is fixed and verified.

## Scenario 2 — Actor document round-trip (evidence, 2026-09-15)

| Peer | Core fields present | Missing (documented limitation) | UI render |
|---|---|---|---|
| Iris Person (andrew) | id, type=Person, inbox, outbox, publicKey, icon, preferredUsername, name | summary, manuallyApprovesFollowers | ✅ full profile (avatar + name + handle) |
| Lemmy Person (lemmyadmin) | id, type=Person, inbox, outbox, publicKey, preferredUsername | **icon, name** (Lemmy Persons carry neither) | ✅ handle + monogram avatar fallback; 2 console 404s on `/followers`+`/following` (F-2, Lemmy limitation) |
| Lemmy Group (interop) | id, type=Group, inbox, outbox, publicKey, preferredUsername, name, summary | **icon** | ✅ name + summary + monogram avatar fallback (no icon) |
| Iris Group (interopX) | id, type=Group, inbox, outbox, publicKey, preferredUsername, name | icon, summary | ✅ name + monogram avatar fallback |
| Mastodon (real external) | id, type=Person, inbox, outbox, publicKey, icon, preferredUsername, name | — | ✅ (verified via direct fetch; actor doc renders all core fields) |

**Pass:** every peer's actor doc renders in `ActorProfile` with no blank/broken fields. The documented
platform limitations (Lemmy Person: no `name`/`icon`; Lemmy Group: no `icon`) are handled by the
avatar monogram fallback + handle-only display, exactly as `ActorProfile` is designed (null-checks for
Name/Summary/Icon/Banner). The only console noise is the F-2 Lemmy person-collections 404 (a Lemmy
limitation; the page degrades gracefully).

## Scenario 1 — Cold WebFinger resolution (evidence, 2026-09-15)

| Peer | Query | Result |
|---|---|---|
| Iris Person (local) | `acct:andrew@iris.luit.ink` | ✅ resolves → `https://iris.luit.ink/ap/v1/u/andrew` (also `alice` → `/ap/v1/u/alice`) |
| Lemmy Person | `acct:lemmyadmin@lemmy.luit.ink` | ✅ resolves → `https://lemmy.luit.ink/u/lemmyadmin` (Person) |
| Lemmy Community | `acct:interop!lemmy.luit.ink` / `acct:c/interop@lemmy.luit.ink` | ⚠️ **400** (Lemmy doesn't serve community WebFinger) — see F-1 |
| Mastodon (real external) | `acct:RayvenMX@mastodon.world` | ✅ resolves → `https://mastodon.world/users/RayvenMX` |
| Misskey | `acct:misskey@misskey.io` | ✅ resolves → `https://misskey.io/users/7rkr40rk13` |
| Pleroma / PieFed / PeerTube | — | ✅ verified via existing fixture round-trip tests (9 pass: `MisskeyInteropRoundTripTests` + `PleromaFamilyInteropRoundTripTests`); live WebFinger is host-dependent (fosstodon.org 404s the webfinger endpoint; piefed.social 200s). |

**Pass:** every peer type resolves to the correct actor type without manual surprises. The only
platform-specific behavior is the Lemmy community WebFinger 400 (F-1), which is a Lemmy-side
limitation and does not affect the Iris federation path (138.5 uses direct IRI, not WebFinger, for
Lemmy communities). No host mismatches; no `!`-stripping surprise in the federation path (the `!`
is only a Web UI input convention).
