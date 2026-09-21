# Iris ↔ Iris Interop — Manual Test Suite

> Two **fresh** Iris instances (A = primary at `https://qa-iris-a.luit.ink`, B = a second Iris.Web
> deployment at its own FQDN, e.g. `https://qa-iris-b.luit.ink` — substitute the real B URL at run
> time). No prior accounts or content exist. All federation is Iris-native (both sides speak the
> same dialect), so failures here are unambiguous Iris defects — no peer-side excuse.
>
> **Accounts created by this suite:** instance A: `ii-a1`, `ii-a2` (password `Password1` for
> both); instance B: `ii-b1` (password `Password1`). Community: `ii-comm` on instance A.
> **Token convention:** post bodies embed `II-<test>-<n>` (e.g. `II-A2-1`).
> **Run order matters:** A1 → A2 → A3 → … A10.

---

## A1 — Bootstrap accounts on both instances (self-provisioning)

**Preconditions:** fresh stack (see README).

**Steps:**
1. 📸 visual — Navigate to `https://iris.luit.ink/` (instance A). Screenshot: landing page renders
   without error, no console errors (🌐 network: `browser_console_messages` = 0 errors).
2. Navigate to `https://iris.luit.ink/register`. 📸 visual — form renders (username + password
   fields visible).
3. Fill username `ii-a1`, password `Password1`, submit. ⟳ reload → expect to be signed in
   (navigation bar shows the account, no redirect to `/login`).
4. Log out (sign-out control in nav). Navigate to `https://iris.luit.ink/register` again; create
   `ii-a2` / `Password1`. Same assertions.
5. Navigate to instance B's origin. 📸 visual — B's landing page renders (proves B is up + its
   TLS/proxy path works). Create `ii-b1` / `Password1` on B; signed-in assertion.
6. 🔌 wire — From the browser (signed in on A):
   `fetch('https://iris.luit.ink/.well-known/webfinger?resource=acct:ii-a1@iris.luit.ink')` →
   200, JSON `subject` ends with `/ap/v1/u/ii-a1`. Repeat for `ii-a2` (A) and `ii-b1` (B host).

**Assertions:**
- A1.1 📸 landing pages of A and B render (no error text, no blank page).
- A1.2 🌐 zero console errors on first load of A and B.
- A1.3 behavioral — all three accounts can register + log in on their own instance.
- A1.4 🔌 webfinger resolves all three handles to actor IRIs on the correct host.

---

## A2 — Cross-instance person-follow (A → B), both directions

**Preconditions:** A1 done.

**Steps:**
1. Signed in as `ii-a1` on A. Navigate to `https://iris.luit.ink/` (home). Use the
   follow/remote-lookup flow to follow `ii-b1@<B-host>` (the UI's "follow a remote account"
   entry point — search/directory or the compose mention bar; record which control is used).
   Submit the follow.
2. ⟳ reload A's home. 📸 visual — `ii-b1` appears in A's following surface (profile page
   `/profile` → following list, or home feed indicator).
3. 🔌 wire — `fetch('https://<B-host>/ap/v1/u/ii-b1?select=followers')` or the followers
   collection IRI from B's actor doc (`endpoints`/`followers` field) → `ii-a1`'s actor IRI is
   present in the collection.
4. Signed in as `ii-b1` on B: navigate to B, open profile/followers. 📸 visual — `ii-a1`
   (remote actor, host = A's host) is listed with a sane remote profile (name, not a 404 card).
5. Now reverse: `ii-b1` on B follows `ii-a1@iris.luit.ink`. Repeat steps 2–3 mirrored (A's
   followers collection contains `ii-b1`; B's following shows `ii-a1`).
6. 🌐 network — on both A and B profile loads: the remote actor's document is fetched exactly
   once per load (no N+1 spam of the same actor IRI — record counts of requests to
   `…/ap/v1/u/ii-*`).

**Assertions:**
- A2.1 behavioral — follow edge appears on **both** sides' UI within one reload (+1 retry after 10 s).
- A2.2 🔌 each side's followers collection contains the other's actor IRI (wire truth > UI).
- A2.3 📸 remote actor renders correctly on the remote instance (name/avatar/summary, no error
  card, no "unknown actor").
- A2.4 🌐 no request spam: each distinct actor IRI fetched ≤ 1× per page load.

---

## A3 — Follow request gating (manually-approved account)

**Preconditions:** A2 done.

**Steps:**
1. As `ii-a1` on A: set the account to **manually approve followers** (settings page,
   📸 visual — the toggle exists and flips; note the exact control). If Iris has no such
   per-account gate, record as GAP and skip A3.
2. As `ii-a2` on A: follow `ii-a1@iris.luit.ink` (same instance, but exercise the gate path).
3. 📸 visual — on `ii-a1`'s A UI: a **pending follow request** is visible (notifications or
   profile requests surface), and `ii-a2` is **not** yet in the accepted followers list.
4. 🔌 wire — `ii-a1`'s followers collection: `ii-a2` absent or marked provisional.
5. `ii-a1` **Accepts** the request (notification Accept button). ⟳ reload.
6. 📸 visual — `ii-a2` now in accepted followers; the request is gone from the pending list.

**Assertions:**
- A3.1 📸 gated follow produces a visible pending request, not an immediate accepted edge.
- A3.2 🔌 before accept: edge not finalized (absent from public followers collection).
- A3.3 behavioral — after Accept, edge is finalized on both sides (UI + collection).

---

## A4 — Cross-instance post federation (A posts → B sees)

**Preconditions:** A2 done (ii-a1 ↔ ii-b1 follow each other).

**Steps:**
1. As `ii-a1` on A: compose a **public** post with body exactly `II-A4-1 hello cross-instance`
   (visibility = public / federated — the default public option; 📸 visual — visibility picker
   shows the option used). Publish.
2. 🔌 wire — `ii-a1`'s outbox (A) contains a `Create` activity whose `object` is a `Note` with
   `content` containing `II-A4-1` and `to` including `as:Public` (or the public audience IRI).
3. As `ii-b1` on B: ⟳ reload home (the follow-feed tab). 📸 visual — the post appears: body
   `II-A4-1 hello cross-instance`, author shown as `ii-a1@iris.luit.ink` (remote handle form).
4. Click the post → object detail page. 📸 visual — full content, author link, and (if Iris
   shows it) community/origin metadata render; author's profile page is reachable and shows
   `ii-a1`'s profile from B's perspective (📸 visual — profile renders, following/followers
   counts present).
5. 🌐 network — B's home load: the remote `Create`/`Note` was fetched (record which IRI, via
   proxy or direct — note the pattern: B fetches remote objects through A's proxy
   (`/ap/v1/proxy/…`) or directly; record which).

**Assertions:**
- A4.1 🔌 outbox `Create` + `Note` shape correct (content, audience, `attributedTo`).
- A4.2 📸 post appears in B's follow feed with correct body + remote author identity.
- A4.3 📸 object detail + author profile render on B with no error cards / no console errors.
- A4.4 🌐 zero console errors on B while the remote content renders.

---

## A5 — Cross-instance reply threading (B replies → A sees thread)

**Preconditions:** A4 done.

**Steps:**
1. As `ii-b1` on B: open `ii-a1`'s post (`II-A4-1`) from the home feed → object detail. Use the
   reply control. Compose `II-A5-1 reply from B` and send.
2. 🔌 wire — `ii-b1`'s outbox (B) contains a `Create` whose `object.inReplyTo` is the IRI of
   `II-A4-1`'s Note (A's host).
3. As `ii-a1` on A: ⟳ reload object detail of `II-A4-1`. 📸 visual — the reply is rendered in
   the thread: `II-A5-1 reply from B`, author `ii-b1@<B-host>`, visually nested/indented under
   the parent.
4. Two-level thread: as `ii-a1` on A reply to the reply: `II-A5-2 reply to reply from A`.
   ⟳ reload B's object detail for `II-A5-1`. 📸 visual — A's reply appears nested under B's
   reply (3-level thread renders on B).

**Assertions:**
- A5.1 🔌 `inReplyTo` chains are correct IRIs (wire).
- A5.2 📸 thread renders on **both** platforms, nested, with remote authors labeled correctly.

---

## A6 — Like / unlike cross-instance

**Preconditions:** A4 done.

**Steps:**
1. As `ii-b1` on B: on `II-A4-1`'s detail (or feed card), click **Like**. 📸 visual — the like
   button shows the "liked" state.
2. 🔌 wire — `ii-b1`'s outbox (B) contains a `Like` activity with `object` = the Note IRI.
3. As `ii-a1` on A: ⟳ reload the post's detail. 📸 visual — like count increased (shows 1),
   and if Iris shows it, the liker's identity (`ii-b1@<B-host>`) is attributable.
4. As `ii-b1` on B: **Unlike**. ⟳ reload A. 📸 visual — like count back to 0.

**Assertions:**
- A6.1 🔌 `Like` activity on B's outbox, correct object IRI.
- A6.2 📸 like count updates on A within one reload (+1 retry); unlike decrements it.

---

## A7 — Boost / reblog cross-instance (Announce)

**Preconditions:** A4 done.

**Steps:**
1. As `ii-b1` on B: on the `II-A4-1` post, use the boost/repost control. 📸 visual — boosted
   state shown.
2. 🔌 wire — `ii-b1`'s outbox (B) contains an `Announce` activity with `object` = Note IRI.
3. As `ii-a1` on A (who is… wait — direction: A4 post is by `ii-a1`; B boosts it. Check the
   **author's** surface): as `ii-a1` on A, ⟳ reload post detail / notifications. 📸 visual —
   boost is visible (count or "boosted by" indicator).
4. As `ii-a1` on A: also check own home feed shows the boost event (if Iris surfaces boosts in
   the home feed). Record what is shown.
5. Unboost from B; ⟳ reload A; 📸 visual — boost gone.

**Assertions:**
- A7.1 🔌 `Announce` on B's outbox with correct object IRI.
- A7.2 📸 boost visible to author on A; unboost removes it. (If Iris does not surface boosts in
  any UI, record as GAP with the wire evidence — wire pass is the pass criterion, UI is bonus.)

---

## A8 — Community: create on A, join from B, post, see on B

**Preconditions:** A2 done.

**Steps:**
1. As `ii-a1` on A: create community `ii-comm` (name "II Comm", any description). 📸 visual —
   community page `/c/ii-comm` renders, `ii-a1` shown as owner.
2. 🔌 wire — webfinger `acct:!ii-comm@iris.luit.ink` → Group document; the Group doc contains
   `iris:manuallyApprovesMembers: false` (open community — record actual field; if the community
   is gated by default, note it and Accept the join in step 4).
3. As `ii-b1` on B: follow/join `!ii-comm@iris.luit.ink` (same remote-follow entry point as A2).
   ⟳ reload B. 📸 visual — community appears in B's joined/followed-communities surface.
4. 🔌 wire — A: the community's followers/members collection contains `ii-b1`'s actor IRI.
5. As `ii-a1` on A: post to the community: `II-A8-1 community post` (visibility: community /
   public per the post-to-community control). 🔌 wire — outbox `Create` with `to`/`cc` including
   the community's Group IRI (or `attributedTo` = Group, per Iris's community-post shape).
6. As `ii-b1` on B: open the community `!ii-comm@iris.luit.ink` from B. 📸 visual — the feed
   shows `II-A8-1 community post` with community name rendered.
7. As `ii-b1` on B: post to the **same remote community**: `II-A8-2 post from B`.
   As `ii-a1` on A: ⟳ reload the community page. 📸 visual — `II-A8-2` appears, author
   `ii-b1@<B-host>`.

**Assertions:**
- A8.1 🔌 Group doc + webfinger for the community are correct.
- A8.2 behavioral + 🔌 cross-instance community join finalizes (UI on B + collection on A).
- A8.3 📸 community post by A appears on B's community page; post by B appears on A's (both
  directions, both render with correct author + community identity).

---

## A9 — Edit (Update) + Delete (tombstone) cross-instance

**Preconditions:** A8 done (use `II-A8-1`, posted by `ii-a1` on A).

**Steps:**
1. As `ii-a1` on A: edit `II-A8-1` → new body `II-A8-1 EDITED`. 🔌 wire — outbox (A) contains
   an `Update` activity whose `object` is the Note with the new content, `updated` > `published`.
2. As `ii-b1` on B: ⟳ reload the community page. 📸 visual — post body shows `II-A8-1 EDITED`
   (the update propagated, not the stale copy).
3. As `ii-a1` on A: **delete** `II-A8-1` (edit/delete menu on the post). Confirm.
4. 🔌 wire — fetching the Note IRI on A: either 404 or a `Tombstone` document (record which).
   Outbox (A) contains a `Delete` activity with `object` = the Note IRI.
5. As `ii-b1` on B: ⟳ reload the community page. 📸 visual — the post is gone (or shows a
   tombstone/"deleted" placeholder — record which; a stale live copy is a FAIL).

**Assertions:**
- A9.1 🔌 `Update` on A's outbox with corrected content.
- A9.2 📸 updated body visible on B (no stale cache — hard reload used).
- A9.3 🔌 `Delete` on A's outbox; Note IRI now 404 or Tombstone.
- A9.4 📸 post removed (or tombstoned) on B.

---

## A10 — Unfollow (Undo) cross-instance

**Preconditions:** A2 done (ii-a1 ↔ ii-b1 follow).

**Steps:**
1. As `ii-a1` on A: unfollow `ii-b1@<B-host>` (following list → unfollow control).
2. 🔌 wire — A: `ii-a1`'s outbox contains an `Undo` activity whose `object` is the original
   `Follow` IRI. B: the followers collection no longer lists `ii-a1` (⟳ fetch again after a few
   seconds).
3. As `ii-b1` on B: ⟳ reload. 📸 visual — `ii-a1` no longer in followers; the post `II-A4-1`
   should no longer appear in B's follow feed on subsequent reloads (feed is follow-driven).
4. Re-follow to restore state (keeps the suite's end state healthy for re-runs): follow again,
   verify edge is back on both sides.

**Assertions:**
- A10.1 🔌 `Undo(Follow)` published on A; B's collection drops the edge.
- A10.2 📸 UI on B reflects the removal (followers list + follow feed no longer sourced from A).

---

## End-state + re-run note

After A1–A10 pass, the stack contains: 3 accounts, 1 community (`ii-comm` on A), a small number
of posts (A4/A5/A8 remain; A9's post deleted), mutual follows. **For a clean re-run** of the
suite, wipe both Iris instances (compose `down -v` + re-seed) — the suites assume freshness at
the top; a re-run on a dirty stack will collide on handle registration (A1) and is invalid.

## Run log

| Test | Result | Evidence (screenshot paths / JSON values) | Date |
|---|---|---|---|
| A1 | PASS | webfinger `/.well-known/webfinger?resource=acct:ii-a1@qa-iris-a.luit.ink` → 200 `self` `…/u/ii-a1` (×3 handles) | 2026-09-20 |
| A2 | FAIL | edge+delivery OK both ways; **S24** (Following-tab omits remote both instances; self-follow in A `ii-a1/outbox`; remote-actor GET 404 both ways) | 2026-09-20 |
| A3 | PARTIAL | A3.1 PASS (pending request + Accept/Decline); **A3.2 FAIL → S34** (public `ii-a1/followers` = `[ii-b1, ii-a2]` before Accept); A3.3 PASS (`requests`=`[]`, edge both sides) | 2026-09-20 |
| A4 | FAIL | A outbox `Create→Note` correct; B log `Inbox accepted: Create … Recipient ii-b1`; B proxy 200; **S25** (B `/home` empty; `GET B /ap/v1/u/ii-b1/feed` = 2 Follow only) | 2026-09-20 |
| A5 | FAIL | B outbox reply `inReplyTo`=parent; A log `Inbox accepted: Create … Recipient ii-a1`; A proxy 200; **S26** (`GET A <parent>` `replies` empty) | 2026-09-20 |
| A6 | FAIL | B outbox `Like` (actor ii-b1, object Note); A log no Like; B log `Shared inbox: no local recipient; accepting and dropping`; **S27** (A note `likes` empty) | 2026-09-20 |
| A7 | FAIL | B outbox `Announce`; A log `Inbox accepted: Announce … Recipient ii-a1` (NOT dropped); **S28** (A note `shares` empty, `sharedCount`=1) | 2026-09-20 |
| A8 | PARTIAL/BLOCKED | A8.1 partial: Group doc `GET A /ap/v1/c/ii-comm` 200 (owner ii-a1) but **S29** (webfinger `acct:!ii-comm@…` 404 both routes); **S30** (B can't join/view: directory "All known" empty, `GET B /c/ii-comm` "Community not found") → A8.2/A8.3 blocked | 2026-09-20 |
| A9 | PARTIAL | A9.1 PASS (A outbox `Update`, content `II-A4-1 EDITED`); **S31** (note `published` cleared to None, Update object omits `updated`); A9.2/5 FAIL → **S32** (A Tombstone + `Delete`, but B proxy = stale live Note; A log `Inbox rejected: unknown recipient <note-IRI>`) | 2026-09-20 |
| A10 | FAIL | B `following` empty, B outbox `Undo` (object bare `follows/` IRI); **S33** (A `ii-a1/followers` still `[ii-b1]`; A log `Inbox rejected: unknown recipient <follows-IRI>`) | 2026-09-20 |

## Re-test (from-scratch rebuild, 2026-09-20)

Stack torn down (`down -v`) and rebuilt fresh; accounts + `ii-comm` re-created. Re-ran the previously-failed cases to confirm whether S24–S34 reproduce on a clean build.

**Build-timing caveat (matters for S25/S26):** the fresh Iris image was built at **2026-09-20T21:10 UTC**. The working tree at that moment contained the S25 feed fix (`src/Iris.Server/Services/FeedService.cs` `GetDeliveredContentAsync`) **only if it was present before 21:10** — it was *not* (the file's mtime is 21:58 UTC, after the build, and the running `Iris.Server.dll` has no `GetDeliveredContentAsync`). So the re-test ran the **original, unfixed** Iris code for S25. (The S26 threading fix, by contrast, is in `CreateActivityHandler`/committed code and **was** baked in — which is why S26 no longer reproduces.)

| Case | Finding | Original | Re-test (fresh) | Verdict |
|---|---|---|---|---|
| A2 | S24 facet 1 (Following tab omits remote, both) | FAIL | Following tab "Not following anyone yet" both sides | **CONFIRMED** |
| A2 | S24 facet 2 (self-follow in outbox) | FAIL | `ii-a1` A→B Follow object = `ii-b1` (correct); no self-follow | **NOT reproduced** |
| A2 | S24 facet 3 (remote-actor GET 404) | FAIL | `GET A /ap/v1/u/ii-b1` = 200, `GET B /ap/v1/u/ii-a1` = 200 | **NOT reproduced** |
| A3 | S34 (gated follower in public `followers` before accept) | FAIL | public `ii-a1/followers` = `[ii-b1, ii-a2]` **before** accept | **CONFIRMED** |
| A4 | S25 (remote post not in follower home feed) | FAIL | B `/home` empty; `GET B /ap/v1/u/ii-b1/feed` = 2 Follow only; B **does** have the note (`GET B /ap/v1/proxy/<note>` 200) — defect is feed-only | **CONFIRMED** (fresh build = original unfixed code; the `FeedService.GetDeliveredContentAsync` fix was added to the working tree *after* the build, not baked in) |
| A5 | S26 (remote reply not threaded under parent) | FAIL | `GET A <parent>/replies` includes local + remote reply | **NOT reproduced (fixed)** |
| A6 | S27 (Like dropped at shared inbox) | FAIL | n/a — cannot drive cross-instance Like via Playwright (UI blocks remote like; outbox POST needs AP-HTTP-Sign) | **NOT re-testable via UI** |
| A7 | S28 (remote Announce not in `shares`) | FAIL | n/a — same UI/sign constraint as A6 | **NOT re-testable via UI** |
| A8 | S29 (community webfinger 404) | FAIL | `acct:!ii-comm@…` webfinger = 404 (person webfinger 200) | **CONFIRMED** |
| A8 | S30 (B can't join/view remote community) | FAIL | B directory "All known" = "No communities yet"; `GET B /ap/v1/c/ii-comm` = 404 | **CONFIRMED** |
| A9 | S31 (edit clears `published`) | FAIL | post-edit `GET A <note>` `published` = None, `updated` set | **CONFIRMED** |
| A9 | S32 (Delete not propagated; peer stale) | FAIL | A Tombstone + `Delete` (to/cc None); B log `Inbox rejected: unknown recipient <note-IRI>` (×2, Update+Delete) | **CONFIRMED (wire)** |
| A10 | S33 (unfollow `Undo` not propagated) | FAIL | A outbox `Undo` (object bare `follows/` IRI); B log `Inbox rejected: unknown recipient <follows-IRI>`; B `ii-b1/followers` still `[ii-a1]` | **CONFIRMED** |

**Net (fresh build):** S24 facet 1, S25, S29, S30, S31, S32, S33, S34 **reproduce**; S26 **fixed**; S24 facets 2–3 **not reproduced**; S27/S28 **not re-testable via Playwright** (need a signed CLI client). A10 re-follow restored the edge afterward (suite left healthy).

**Findings from this run:** S24, S25, S26, S27, S28, S29, S30, S31, S32, S33, S34 (all in `docs/qa/`, indexed in `docs/qa/README.md`). Cross-cutting themes: (1) remote objects are delivered+stored but not **surfaced** (S25 feed, S26 replies, S28 shares); (2) outbound `Like`/`Update`/`Delete`/`Undo` are **dropped or rejected on the peer** (S27 shared-inbox drop; S32/S33 `unknown recipient` on bare-IRI/note-IRI objects); (3) **discovery** gaps (S29 community webfinger); (4) **state** inconsistencies (S24 follow, S34 gated-follow exposure). Note: the working webfinger route is `/.well-known/webfinger` (the `/ap/v1/webfinger` path 404s for everyone — earlier suite references to `/ap/v1/webfinger` were a test-route error).
