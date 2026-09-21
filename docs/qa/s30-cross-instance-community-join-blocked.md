# S30 — Cross-instance community join/view is blocked (remote community unreachable from a peer instance)

- **Class:** bug / federation — **Severity:** S2
- **Status:** open (blocks Interop A8.2 + A8.3)
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
