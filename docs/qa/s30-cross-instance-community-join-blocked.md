# S30 — Cross-instance community join/view is blocked (remote community unreachable from a peer instance)

- **Class:** bug / federation — **Severity:** S2
- **Status:** **PARTIALLY FIXED (Pass 136, build `f9ca2d2`)** — the **direct-view facet (A8.2)**, the **federated-community-discovery facet (A8.2)**, and **A8.3 (follow/join)** all now work: `GET B /ap/v1/c/{name}` serves the cached remote Group (200, `aebe420`); B's **Directory "All known" Communities tab + Search page** now **list the remote community** with a **Join** button (`f9ca2d2`); and **Join federates** the Follow to the owner (A's community `/followers` = ii-b1). The **community-feed facet (A8.4) is still OPEN**: `GET B /ap/v1/c/ii-a8-community/feed` → **404**, and there is **no compose UI to post into a community** (no community selector) — so community-post federation is the only remaining facet. **S30 now reduces to A8.4 community-post federation.**
- **Found:** Interop suite A8 (Iris↔Iris), 2026-09-20, QA federation stack (Iris A `qa-iris-a.luit.ink`, Iris B `qa-iris-b.luit.ink`)
- **Related:** [S29](s29-community-webfinger-404.md) (community WebFinger 404) — S30 is the downstream consequence for **join/view** from a peer instance.

## Symptom

`ii-a1` (A) created community `ii-comm` (Group IRI `https://qa-iris-a.luit.ink/ap/v1/c/ii-comm`). The Group's `Create` **did federate** to B:
- B `qa-iris-b` log: `Inbox accepted: Create from …/ii-a1 targeting …/c/ii-comm. Recipient: …/ii-b1, Peer: qa-iris-a.luit.ink` and `Refreshed cached remote community https://qa-iris-a.luit.ink/ap/v1/c/ii-comm`. So **B has the remote Group cached**.

**But B cannot join or view the community:**
- **Directory "All known" → Communities**: empty — `ii-comm` is **not listed** (no federated community discovery; consistent with S29 WebFinger 404).
- **Communities page (`/communities`)**: only "Following" / "My communities" / "All on this instance" tabs. No path to follow/join a **remote** community.
- **Direct view `GET B /c/ii-comm`** → the client resolves it to a **local** IRI (`https://qa-iris-b.luit.ink/ap/v1/c/ii-comm`) and shows **"Community not found."** — B has no *local* community named `ii-comm`, and the cached remote Group is not served at that route.

So even though B received and cached the remote Group, there is **no way from B to join (follow) the community or open its page** — the cross-instance community join (A8.2) and community-post federation (A8.3) are blocked.

## Root cause (suspected)

Multiple gaps combine:
1. **No remote-community follow entry point** — the Directory/Communities surfaces only handle local communities ("All on this instance") and person-remote-follow; a remote **Group** cannot be followed. (Blocked by S29: the `acct:!name@host` WebFinger that would let a user resolve the remote community 404s.)
2. **`/c/{name}` resolves to a local community only** — opening `/c/ii-comm` on B looks up a *local* community by name and 404s, instead of recognizing the name refers to a cached **remote** Group and rendering it (or resolving via the full remote IRI).
3. **No federated community discovery** — "All known" Communities is empty, so a remote community is never surfaced for joining.

No `file:line` yet — needs a code pass on (a) the remote-follow path (does it handle Groups?), (b) the `/c/{name}` route (local-only vs. remote-IRI lookup), (c) the "All known" community discovery source.

## Fix (agreed approach)

- A remote community must be followable/joinable from a peer instance: (a) fix WebFinger for `acct:!name@host` (S29) so the remote Group is resolvable; (b) add a remote-community follow entry point (Directory/Communities) that issues a `Follow` to the remote Group IRI; (c) make `/c/{name}` (or a remote-IRI route) render the cached remote Group and its feed instead of 404'ing on "no local community".

## Re-verify (clean entry)

1. Create community `ii-comm` on A.
2. From B, resolve `!ii-comm@qa-iris-a.luit.ink` (WebFinger 200 — requires S29 fixed) and **follow** it.
3. A: the community's `members`/`followers` collection contains `ii-b1`'s actor IRI.
4. B: `/c/ii-comm` (or the remote-IRI community page) **renders** the community (name "II Comm") and its feed (no "Community not found").
5. (A8.3) A posts to the community → B's community feed shows it; B posts to the same remote community → A's community feed shows it.

**Re-verification evidence (Interop A8, 2026-09-20, QA stack):** B log shows the Group `Create` federated + "Refreshed cached remote community …/c/ii-comm". Yet: Directory "All known" Communities = empty; Communities page = local-only tabs; `GET B /c/ii-comm` → "Community not found" (client resolved to local IRI `…/qa-iris-b…/c/ii-comm`). **S30 OPEN — blocks A8.2/A8.3.**

## Re-test (fresh rebuild, 2026-09-20)

**CONFIRMED — reproduces (both facets).** `ii-a1` created `ii-comm` on A (fresh stack).
- B's directory → **Communities / "All known"** = **"No communities yet."** (remote `ii-comm` not discoverable for joining).
- `GET B /ap/v1/c/ii-comm` → **HTTP 404** (non-JSON "Community not found" body) — B has no local community by that name and does not serve the cached remote Group at that route.

No remote-community follow entry point, no federated community discovery, `/c/{name}` local-only. S30 OPEN (reproduces on a fresh build) — still blocks A8.2/A8.3.

## Re-test (interop A8, 2026-09-21, fresh QA cluster)

**PARTIALLY IMPROVED — remote-community follow now works via the actor page; discovery still blocked.** `ii-a1` created community `ii-a8-community` (Group `…/qa-iris-a.luit.ink/ap/v1/c/ii-a8-community`) on A.

- **A8.2 (discoverability): still BLOCKED.** `GET B /ap/v1/c/ii-a8-community` → **404**; B **search** for `ii-a8-community` → **0 results**; B Communities "All on this instance" / directory → no remote community. So a user **cannot discover** the remote community from B's surfaces.
- **A8.3 (follow): now WORKS via the full-IRI actor page.** Navigating directly to `GET B /actor?iri=https://qa-iris-a.luit.ink/ap/v1/c/ii-a8-community` **renders** the community (name "II-A8 Test Community", "Community" badge) — B lazily fetches the remote Group by IRI. Pressing **Follow**: `GET A /ap/v1/c/ii-a8-community/followers` → **`[ii-b1@B]`** (the follow edge is recorded on A). (B `ii-b1/following` collection showed only 1 of 2 relationships — the S24 facet-1 under-report.)
- **A8.4 (community post → follower feed): BLOCKED.** There is **no UI to post into a community** (compose offers only Public/Followers/Direct, no community selector). A plain-public note posted by ii-a1 was **not** community-addressed (`to`=Public, `cc`=ii-a1/followers, no community IRI) and did **not** appear in ii-b1's B home feed (which is empty — also S25). Community-post federation is not exercisable.

Net: the **follow** path is fixed (remote Group resolvable via S29 + followable via the actor page), but **discovery** (directory/search/`/c/{name}`) and **community-post federation** remain broken. **S30: partially improved (A8.3 fixed) — OPEN on discovery + community-post (A8.2/A8.4).**

## Re-test (Pass 111, 2026-09-21, build `11fbec6` — dev's S30 direct-view fix deployed)

**Dev committed `11fbec6`: "serve cached remote Group at `/ap/v1/c/{name}` (direct-view facet)"** — the `/c/{name}` route now falls back to a stored remote community whose IRI's last path segment matches the name (and whose origin differs from this instance), serving the doc AS-IS (mirrors the S24 remote-actor fallback). Rebuilt + redeployed the QA cluster to `11fbec6`.

**A8.2 (direct-view) — FIXED.** `GET B /ap/v1/c/ii-a8-community` → **200** (was 404), serving the **remote** community doc: `type`=Group, `name`="II-A8 Test Community", `id`=`https://qa-iris-a.luit.ink/ap/v1/c/ii-a8-community`, `inbox`=`…/qa-iris-a…/c/ii-a8-community/inbox` (remote IRIs preserved, as the fix intends). **B UI** (`/c/ii-a8-community` → `/community?iri=…`): renders the community page — name "II-A8 Test Community" + "Community" badge + "QA community test" summary, a **Join** button, a **"＋ Post to this community"** link, and **Feed** + **Members (1)** tabs. (No more "Community not found.")

**A8.4 (community feed) — STILL OPEN.** The community page's Feed tab shows "No posts in this community yet" and the **community feed endpoint 404s**: `GET B /ap/v1/c/ii-a8-community/feed` → **404** (1 console error on the page). Dev's `11fbec6` fixed only the `/c/{name}` direct-view, **not** the `/feed` endpoint for a remote community — so the community's post feed is not yet viewable on the peer. (Community-post federation A8.4 is a separate, larger gap.)

**A8.4 endpoint detail (Pass 118):** for the remote community `ii-a8-community`, the community sub-endpoints work on **A (owner)** but 404 on **B (peer)**:

| endpoint | A (owner) | B (peer) |
|---|---|---|
| `/c/ii-a8-community` (doc) | 200 | **200** (S30 fix) |
| `/c/ii-a8-community/feed` | 200 | **404** |
| `/c/ii-a8-community/members` | 200 (totalItems 1) | n/a |
| `/c/ii-a8-community/outbox` | 200 | n/a |

So dev's S30 fix served the remote community **doc** on B, but the **`/feed`** (and likely other community sub-endpoints) still 404 for a remote (cached) community on the peer. **Suggested dev follow-up:** make the community `/feed` (and sub-endpoints) resolve a remote (cached) community the same way the `/c/{name}` route now does.

**A8.3 (follow) — already working** (via the actor page, per the 2026-09-21 re-test above).

**Verdict (build `11fbec6`): S30 PARTIALLY FIXED — A8.2 (direct-view) + A8.3 (follow) now work; A8.4 (community feed) still OPEN** (`/ap/v1/c/{name}/feed` 404s for a remote community). **Suggested dev follow-up:** make the community `/feed` endpoint resolve a remote (cached) community the same way the `/c/{name}` route now does. **Status: OPEN (narrowed) — A8.4 community feed.**

## Re-test (Pass 136, 2026-09-21, build `f9ca2d2` — dev's S30 A8.2 discovery fix deployed)

**Dev committed `f9ca2d2`: "S30 A8.2: 'All known' directory surfaces cached remote communities"** — `GlobalSearchService`'s mixed (`localOnly=false`) actor pass now merges the community-store cached remote community Groups (local communities excluded, de-duplicated, query-filtered; `localOnly=true` unchanged; the content pass excludes stored `Group`s so a community is surfaced exactly once). +3 tests; `Iris.Server.Tests` 1424 pass / 0 fail. Rebuilt + redeployed the QA cluster to `f9ca2d2`.

**A8.2 (federated community discovery) — FIXED.** On B (ii-b1), the **Directory → Communities tab → "All known"** scope now lists **`ii-a8-community` / "II-A8 Test Community"** (Community, "QA community test") with a **Join** button — the exact facet that was **empty** before (a peer instance could not discover a remote community to join it). The **Search page** (`/search?q=ii-a8-community`) also surfaces it ("1 result(s)" — `c/ii-a8-community` → "II-A8 Test Community", linking to `/community?iri=…/c/ii-a8-community`).

**A8.3 (join) — FIXED (federates).** Clicking **Join** on the directory entry federated the Follow to A — A's log: `Inbox received Follow …/ii-b1/follows/06GC5EPV… from …/ii-b1 to …/c/ii-a8-community` + `FollowActivityHandler processed … ok` + `Inbox accepted: Follow from …/ii-b1 targeting …/c/ii-a8-community`; and `GET A /ap/v1/c/ii-a8-community/followers` → totalItems=1, **ii-b1 present**. (A8.3 also works via the full-IRI actor page, per the 2026-09-21 re-test.)

**A8.4 (community feed / post federation) — STILL OPEN.** `GET B /ap/v1/c/ii-a8-community/feed` → **404** (unchanged from Pass 111/118 — `f9ca2d2` fixed the *discovery* path, not the `/feed` endpoint), and there is **no compose UI to post into a community** (compose offers only Public/Followers/Direct, no community selector). So community-post federation remains the only unaddressed facet (a separate, larger surface, per dev's scope note in change doc 14827).

## Re-test (Pass 143, 2026-09-21, build `38ae87c`) — A8.4 "no compose UI" claim was STALE; the cross-post leg works but the post is NOT surfaced in the owner community feed

Dev's `e07faa5` (PLAN-only, no code change) flagged that the A8.4 finding's **"no compose UI to post into a community" claim is stale**: the compose `?community=` selector, the local cross-post path (community-tagged `attributedTo` + feed filter), and the remote cross-post leg (`GetCrossPostTargetsAsync`) all exist (4 passing `CrossPostToRemoteCommunityIntegrationTests`). **Live re-verify on `38ae87c` confirms dev is right about the UI + the cross-post leg, and narrows A8.4 to a feed-surfacing facet.**

1. **The "＋ Post to this community" entry point EXISTS (my Pass 138 "no compose selector" observation was stale).** On B, the **CommunityDetail page** for the remote A community (`/community?iri=https://qa-iris-a.luit.ink/ap/v1/c/ii-a8-community`) renders the community + a **"＋ Post to this community"** link → `/compose?community=https://qa-iris-a.luit.ink/ap/v1/c/ii-a8-community`. (I had checked the *generic* compose page in Pass 138, which has no community selector — the entry point is the community page's button, which is present.) The generic `/compose` (no `?community=`) still shows only Note/Article/Poll + Public/Followers/Direct, consistent with dev's design (the selector is via the community page).
2. **The cross-post leg WORKS (B → A).** Clicking the button opens a compose pre-labeled "Posting to II-A8 Test Community" + "Post to community" button. Posting **`II-S30-4 cross-post from B into remote A community`** produced a B document (`…/u/ii-b1/documents/06GC5WN0QDRV288ZWDN57NN09G`) that is **correctly attributed to the A community**: `type: Page`, `attributedTo: […/u/ii-b1, …/c/ii-a8-community]`, `to: […/c/ii-a8-community, as:Public]`, `cc: […/c/ii-a8-community/followers]`. **A received it** (`Inbox received/processed/accepted: Create from …/ii-b1 targeting …/documents/06GC5WN0` — recipient `ii-a1`, peer `qa-iris-b`). So the **cross-instance cross-post leg (B posts to a remote A community; the note federates to A attributed to the community) WORKS.**
3. **RESIDUAL (the narrowed A8.4): the cross-post is NOT surfaced in the OWNER community's feed.** After the cross-post federated to A, `GET A /ap/v1/c/ii-a8-community/feed` → **200** (the endpoint now works — was 404 in Pass 138) with 20 items, but **II-S30-4 is NOT among them** (the feed's attributedTo breakdown = 8× `u/ii-a1` + 2× `u/ii-b1` = ii-a1's own community posts + ii-b1's earlier **cross-instance replies**; the cross-post is absent). The **UI community feed on A also doesn't show II-S30-4** (top item = ii-a1's II-S32-4). So although the note **federates to A attributed to the community**, the **community feed's query does not surface it** — a **feed-surfacing** facet (the community feed filter likely matches ii-a1's `attributedTo`=community posts + ii-b1's replies, but not this community-attributed cross-post from a member). 

**Verdict (build `38ae87c`): S30 A8.4 NARROWED.** (a) The **"no compose UI" claim is STALE** — the community-page "＋ Post to this community" entry point + compose `?community=` selector + the **cross-post leg (B → A, attributed to the community) all WORK**; (b) the **`/ap/v1/c/{name}/feed` endpoint now returns 200** (was 404 in Pass 138); (c) **RESIDUAL: a cross-posted note from a member is NOT surfaced in the owner community's feed** (federated + stored attributed to the community, but the feed query doesn't include it). **S30 now reduces to the A8.4 community-feed-surfacing facet (a member cross-post not in the community feed).** **Status: OPEN (narrowed) — A8.4 community-feed-surfacing of a member cross-post.** (Dev's `e07faa5` code pass was correct that the cross-post *leg* is implemented; the remaining gap is the feed *surface*.)
