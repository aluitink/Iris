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

## Re-test (fresh QA cluster, 2026-09-21)

Full A1–A10 run on the rebuilt QA cluster (A `qa-iris-a.luit.ink`, B `qa-iris-b.luit.ink`). Accounts `ii-a1`/`ii-a2` (A), `ii-b1` (B); community `ii-a8-community` (A).

| Test | Result | Evidence |
|---|---|---|
| A1 | PASS | webfinger `/.well-known/webfinger?resource=acct:ii-a1@qa-iris-a.luit.ink` → 200 (×3 handles); 0 console errors on fresh cluster |
| A2 | PARTIAL | A2.1 **FAIL → S24 f1** (Following tab "Not following anyone yet" both instances despite correct `following`); A2.2 **PASS** (wire: each side's followers contains the other); A2.3 **PASS** (`GET A /ap/v1/u/ii-b1`=200, `GET B /ap/v1/u/ii-a1`=200); S24 f2 variant (foreign Follow stored in B's ii-b1 outbox) |
| A3 | **PASS (S34 fixed)** | A3.1 PASS (Requests tab "ii-a2 wants to follow you", Accept/Reject); A3.2 **PASS** (public `ii-a1/followers` = `[ii-b1@B]` only **before** accept — gated follower **withheld**); A3.3 PASS (after Accept: `ii-a1/followers` = `[ii-b1, ii-a2]`, `ii-a2/following` = `[ii-a1]`; ~5 s to persist) |
| A4 | PARTIAL | A4.1 **PASS** (A outbox `Create→Note` `II-A4-1`, `to`=Public, `cc`=ii-a1/followers); A4.2 **FAIL → S25** (note stored on B — `GET B <note>` 200 — but B `/home` = "Your timeline is empty"; 0 console errors) |
| A5 | **PASS (S26 fixed)** | A5.1 PASS (B outbox reply `inReplyTo`=parent Note IRI); A5.2 PASS (`GET A <parent>/replies` includes the remote reply; A object-detail renders it nested under the parent) |
| A6 | PARTIAL | A6.1 **PASS** (A UI Like `pressed` count 1; A outbox `Like` object = remote Note IRI); A6.2 **FAIL → S27** (B note `likes` count 0, Likes tab "No likes yet", button not pressed). **Note: the prior "UI blocks remote like" no longer applies — remote Like is now UI-testable.** |
| A7 | PARTIAL | A7.1 **PASS** (B UI Boost `pressed` count 1; B outbox `Announce` object = remote Note IRI); A7.2 **PARTIAL → S28** (A object-detail shows "1 boost" + Shares tab lists ii-b1 — **improvement** — but Boost button count stays 0 and `GET A <note>/shares` wire = 0). **Remote Boost now UI-testable.** |
| A8 | PARTIAL | A8.1 **PASS** (Group doc `GET A /ap/v1/c/ii-a8-community` 200; **S29 fixed** — webfinger `acct:!ii-a8-community@…` = 200); A8.2 **FAIL → S30** (B search = 0 results, `GET B /ap/v1/c/ii-a8-community` = 404 — no discovery); A8.3 **PASS** (B actor page `?iri=…/c/ii-a8-community` renders the remote Group; Follow → `GET A /ap/v1/c/ii-a8-community/followers` = `[ii-b1@B]`); A8.4 **FAIL → S30** (no UI to post into a community; a plain-public note was not community-addressed and not in B's feed) |
| A9 | PARTIAL | A9.1 **PARTIAL → S31** (edit saves on A wire: content + `updated`, but **`published` absent** and UI did not re-render); A9.2 **FAIL → S31/S32** (the `Update` removed B's copy — `GET B <note>` = 404 after the edit); A9.3 **PASS** (`GET A <note>` = Tombstone; A outbox `Delete`); A9.4 **PASS (vacuous)** (B note already 404 from the Update) |
| A10 | PARTIAL | A10.1 **PASS** (A `following` drops ii-b1; A outbox `Undo` object = bare `follows/` IRI); A10.2 PASS (A side); A10.3 **FAIL → S33** (`GET B /ap/v1/u/ii-b1/followers` still = `[ii-a1@A]` after ~12 s — Undo not propagated) |

**Net (2026-09-21 fresh cluster):**
- **FIXED / not reproduced:** **S26** (reply threading), **S29** (community webfinger), **S34** (gated follow withheld until accept).
- **STILL OPEN (reproduced):** **S24 f1** (Following tab omits remote), **S24 f2** (variant: foreign activity in local outbox), **S25** (remote post not in home feed), **S30** (community discovery + community-post federation — though A8.3 follow now works).
- **FIXED on rebuilt cluster (Pass 100, HEAD `27b1ba6`):** **S27** (remote Like now applied on author — `likedCount` 0→1), **S31** (edit preserves `published` + stamps `updated`; the peer-copy-dropped side-effect is S32), **S33** (unfollow `Undo` now propagates via shared-inbox bare-IRI routing). These three were reproduced in the run above **only because the QA cluster images pre-dated the fixes** — they were confirmed fixed after rebuilding the two Iris services from HEAD.
- **PARTIALLY IMPROVED:** **S28** (remote Boost now lands in the Shares tab, but button count + `/shares` endpoint still 0), **S32** (local delete correct/Tombstone; peer propagation still broken — peer copy already lost to the Update).
- **NEW observations:** (a) remote Like/Boost are now **UI-testable** (the prior `UiContext.cs:704` remote-object block is gone on this build) — unblocks S27/S28 regression; (b) the `Update` activity **removes the peer's copy** of the note (B 404 after edit) — a new S31 side-effect; (c) the edit **clears `published` entirely** (note has `updated` but no `published`); (d) the profile Following tab omits remote actors (S24 f1) remains the most user-visible follow-state bug.

**Findings from this run:** S24, S25, S26, S27, S28, S29, S30, S31, S32, S33, S34 (all in `docs/qa/`, indexed in `docs/qa/README.md`). Cross-cutting themes: (1) remote objects are delivered+stored but not **surfaced** (S25 feed, S26 replies, S28 shares); (2) outbound `Like`/`Update`/`Delete`/`Undo` are **dropped or rejected on the peer** (S27 shared-inbox drop; S32/S33 `unknown recipient` on bare-IRI/note-IRI objects); (3) **discovery** gaps (S29 community webfinger); (4) **state** inconsistencies (S24 follow, S34 gated-follow exposure). Note: the working webfinger route is `/.well-known/webfinger` (the `/ap/v1/webfinger` path 404s for everyone — earlier suite references to `/ap/v1/webfinger` were a test-route error).

---

## Re-verify pass (Pass 101, 2026-09-21, build `27b1ba6` == HEAD)

Re-verified the still-open S2-sev items deferred to the two-instance stack: **S25** (remote post in follower feed), **S24** (D1 Following tab / D2 outbox / D3 remote-actor GET), **S32** (delete propagation). B was restarted first to clear the in-memory feed cache.

**Headline — NEW S36 (S2, broad home-feed regression).** The home timeline renders **"Your timeline is empty"** even for an actor's **own** posts:
- **(a) own post:** B's own note `…/06GC41BA…` is in B's outbox but **not** in B's `/home`.
- **(b) local follow:** A's `ii-a2` note `…/06GC4225…` is in ii-a2's outbox but **not** in ii-a1's `/home` (ii-a1 follows ii-a2 locally).
- **(c) remote follow (S25):** A→B note `…/06GC40F0…` was delivered+accepted+stored on B, but **not** in ii-b1's `/home`.
- Captured the UI's authenticated `GET /ap/v1/u/{me}/feed` (16–17 items): dominated by **actor-document activity noise** (`Update`/`Add`/`Remove` on the actor IRI, self `Follow`, `Undo`/`Delete`/`Like`) with **only one content `Create` (a community Group join)** + one `Announce`. The real post `Create`s are absent.
- Restarting B (clearing the feed cache) did **not** change it → a **build regression**, not a stale cache. **Supersedes/blocks S25** and re-opens the S18 local-timeline symptom. Dev code pass on `FeedService.BuildFeedUncachedAsync` (feed source over-inclusive of actor-doc activity / under-inclusive of content `Create`s). → [s36](../s36-home-feed-omits-posts-and-is-polluted-with-actor-document-activity.md)

**S24 — D3 FIXED, D1 CONFIRMED (sharper root cause), D2 persists.**
- **D3 (remote-actor direct GET):** **FIXED** — `GET A /ap/v1/u/ii-b1` = **200** (was 404).
- **D1 (Following tab omits remote actor):** **CONFIRMED** with a sharper root cause. A `ii-a1`'s **`following` collection = `[c/ii-a8-community]` only — ii-b1 is MISSING**, even though A's outbox has the `Follow → ii-b1` activity and A's **`followers`** lists ii-b1. So the follow **edge never materialized in A's `following`** after an unfollow/re-follow cycle → **`following` and `followers` are out of sync**. The Following tab (showing only the community) is rendering that missing edge.
- **D2 (spurious/foreign activities in local outbox):** **persists** — A's outbox still stores B-authored `Follow` activities. → [s24](../s24-cross-instance-follow-state-inconsistent.md)

**S32 — OPEN (cleanest repro yet).** `ii-a1` posted `II-A9-3 reverify S32 delete propagation` (note `…/06GC44QSENG1RQEXRGB9QEP9F0`, `to`=Public). B **received+stored a live copy** (pre-delete `GET B <note>` = 200, `type`=Note). After A deleted it: `GET A <note>` = **Tombstone** ✓; `GET B <note>` = **Tombstone** — **but** B's log shows the Delete was **rejected** (`unknown recipient <note-IRI>`), so B's Tombstone is a **lazy refetch** of A's current doc, **not** a propagated/applied delete. A non-refetching peer would keep a stale live copy. **S32 OPEN.** → [s32](../s32-delete-not-propagated-peer-stale-copy.md)

**Net (Pass 101):**
- **NEW:** **S36** (broad home-feed regression — omits all post `Create`s, polluted with actor-doc activity; supersedes S25).
- **FIXED:** **S24 D3** (remote-actor direct GET now 200).
- **STILL OPEN:** **S24 D1** (Following tab / `following`-edge sync) + **S24 D2** (foreign activities in local outbox); **S32** (Delete rejected at peer, note-IRI-addressed).
- **SUPERSEDED:** **S25** (by S36).
- **CHECKPOINT:** S36 is top priority (home feed is the primary surface; empty for own + followed posts). S28 re-verify deferred (dev's S28 fix not yet committed/deployed). S30 (community join/view) not re-exercised this pass.

---

## Re-verify pass (Pass 102, 2026-09-21, build `27b1ba6` == HEAD)

Re-verified **S28** (A7 remote Boost → note `shares`), which is now UI-testable (the remote-boost block is gone). Build unchanged since Pass 101; dev's S28 fix is in **uncommitted WIP**, not deployed → this is a **pre-fix** re-verify.

**S28 — OPEN, REGRESSED.** `ii-a1` (A) posted `II-A7-3 reverify S28 remote boost shares` (Note `…/ii-a1/notes/06GC48G96XE3WTTV3KK0D39QQ8`, `to`=Public). `ii-b1` (B) pressed **Boost** from B:
- **B (booster):** outbox `Announce`, `actor`=ii-b1, `object`=the Note IRI ✓; B UI Boost pressed, count 1 (local side ✓).
- **A (author) log:** `Shared inbox: no local recipient; accepting and dropping. Peer: …/ii-b1#key-1` — the Announce is **DROPPED**, not accepted, and **no `AnnounceActivityHandler processed` line** (the original run logged `Handler AnnounceActivityHandler processed Announce … ok` + `Inbox accepted`).
- **A note wire:** `GET A <note>` → `shares.totalItems`=0, `sharedCount`=None (no local increment); `GET A <note>/shares` → totalItems 0.
- **A object-detail UI (ii-a1):** Boost count **0**, Shares tab = **"No boosts yet."**

So the remote Boost no longer reaches the author at all on this build — the shared-inbox recipient resolution drops the inbound `Announce` (same class as S27, Like dropped). The earlier "partially improved" (Shares-tab-populated) state from the 2026-09-20 fresh cluster is **gone** on `27b1ba6`. **S28 needs the dev WIP fix (`AnnounceActivityHandler` + `/shares` + shared-inbox Announce→author routing) committed + deployed before a meaningful re-verify.**

**S36 re-confirmed:** B (ii-b1) `/home` still renders **"Your timeline is empty. Follow people to see their posts here."** despite having posts + follows → S36 OPEN.

**Net (Pass 102):**
- **REGRESSED:** **S28** (remote Announce now dropped at the shared inbox; author `shares`/`sharedCount`/Shares-tab all empty — pre-fix build).
- **RE-CONFIRMED:** **S36** (home feed empty for own + followed posts).
- **CHECKPOINT:** S36 top priority (dev code pass on `FeedService.BuildFeedUncachedAsync`). S28 re-verify deferred until the dev WIP fix is committed + deployed. S24 D1/D2 + S32 still open. M2–M12 + L2–L12 blocked on operator accounts.
