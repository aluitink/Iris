# QA Pass Log — General UI/UX Review (recurring)

Each pass is one Playwright-driven exploratory QA pass against `https://iris.luit.ink`.
Findings live in [per-finding docs](README.md) — **not** here. This file is a **brief index** of passes, not a record of findings.

> **Format rule for NEW passes (Pass 26+):** keep each entry to a few lines so the log stays small even when passes run fast. Full repro/root-cause/console detail belongs in the `sNN-*.md` finding doc, never here.
>
> ```
> ## Pass NN (<date>) — <area>
> - **Build/Live:** deployed `<commit>` (== HEAD? y/n)
> - **Explored:** <1 line>
> - **Result:** <N> clean; re-confirmed <IDs>; new <IDs → doc>
> - **Checkpoint:** next pass continues at <area/route>
> ```
>
> Hard cap ~40 entries; archive the oldest half to `passes-archive.md` when exceeded (see [QA_LOOP.md step 6](../reference/QA_LOOP.md)).

---

## Pass 100 (2026-09-21) — Re-verify S31/S33/S27 fixes on rebuilt QA cluster (Iris↔Iris)

- **Build/Live:** QA cluster `qa-iris-a`/`qa-iris-b` images were **3h stale** (pre-dated the fixes). Rebuilt both Iris services from `interop-testing` HEAD `27b1ba6` (`docker compose -p qa up -d --build iris-a iris-b`) → now **== HEAD**. First edit attempt on the stale build still showed `published=None` (confirmed the build was behind), then the rebuild made the fixes live.
- **Explored:** Clean-entry re-verify of the three newly-committed shared-inbox/edit fixes on the two-instance stack: **S31** (A9 edit preserves `published`), **S33** (A10 unfollow `Undo` propagates, bare-IRI wire shape), **S27** (A6 remote Like applied on author).
- **Result:** **S31 FIXED** (`GET A <note>` `published` preserved `03:05:53.7578037Z` + `updated` stamped; the peer-copy-dropped side-effect is S32, tracked separately). **S33 FIXED** (B unfollowed A; bare-IRI `Undo` delivered to A's shared inbox, A resolves the Follow from its store, A `ii-a1/followers` drops `ii-b1` → `[ii-a2]`; no "unknown recipient" rejection). **S27 FIXED** (B liked A's note; A note `likedCount` 0→1 via shared-inbox routing to the note's author).
- **Checkpoint:** S24/S25/S28/S30/S32 remain OPEN (not re-exercised this pass — they pre-date these 3 fixes). M2–M12 (Mastodon) + L2–L12 (Lemmy) still **BLOCKED** pending operator-provided peer accounts. Next: re-verify S24/S25/S28/S30/S32 on the current build, or resume a peer suite once unblocked.

## Pass 98 (2026-09-21) — Iris↔Mastodon M1 (bootstrap) + NEW S35 (remote-actor discovery 404); M2–M12 BLOCKED
- **Build/Live:** fresh QA cluster — Iris A `qa-iris-a.luit.ink` ↔ Mastodon 4.7.2 `qa-mastodon.luit.ink`.
- **Explored:** M1 (bootstrap both systems) + Iris→Mastodon discovery (fediverse search + actor page) + Mastodon cluster wire probes (webfinger, public API, AP actor doc, registration config).
- **Result:** **M1 PARTIAL** — Iris `im-user` created + signed in (PASS); Mastodon `imuser` **could not be created** (`registrations: false`, `invites_enabled: true`, all signup routes 404) — a **pre-seeded** `imuser` exists (`user_count:1`, webfinger resolves) but its password is unknown → **Mastodon UI undrivable**. **M1.3 PASS** (webfinger `acct:imuser@…` → 200). **NEW S35 (S1):** Iris can't discover the remote Mastodon actor — search + actor page both 404; wire: Iris proxy webfinger 200 → `GET /ap/users/117306213651189335` **404**; **every** `/ap/users/{id}`, `/api/v1/accounts/*`, `/@{handle}` route 404s while webfinger resolves + `user_count:1` → **Mastodon-side (this cluster) provisioning defect**, not an Iris bug. **M2–M12 BLOCKED** (need a usable Mastodon account: operator supplies the pre-seeded `imuser` password, or flips `registrations` to open / re-provisions). Detail in [iris-mastodon.md](interop-tests/iris-mastodon.md) + [s35](s35-remote-actor-discovery-proxy-404.md).
- **Checkpoint:** **BLOCKED** on a usable Mastodon account (operator action) for M2–M12. Proceed to Iris↔Lemmy (L1–L12) meanwhile — check whether the Lemmy instance has the same invite-only / pre-seeded-account issue.

## Pass 99 (2026-09-21) — Iris↔Lemmy L1 BLOCKED (registration-pending); L2–L12 blocked
- **Build/Live:** fresh QA cluster — Iris A `qa-iris-a.luit.ink` ↔ Lemmy 0.19.20 `qa-lemmy.luit.ink`.
- **Explored:** L1 (bootstrap both systems) + Lemmy registration config (`/api/v3/site`, `/signup` page) + webfinger control.
- **Result:** **L1 BLOCKED(registration-pending).** **Iris `il-user`:** not created this pass (host confirmed healthy + registration open — `im-user` created on the same instance minutes earlier; will create on resume). **Lemmy `iluser`:** cannot be self-served — `/signup` shows *"you need to fill out this application, and wait to be accepted"* (registration mode **Pending**; summary reports `registration_mode: null` but UI + required **Answer** field confirm Pending). **Password policy 10–60 chars** → suite's `Password1` (9) rejected; account needs a longer password. **L1.1 PASS** (landing + `/api/v3/site` 200, BE 0.19.20). **L1.3 control PASS** (webfinger `acct:lemmyadmin@…` → 200, `self` `…/u/lemmyadmin`). **Key contrast vs Mastodon:** Lemmy's federation surface is **healthy** (site API + webfinger + actor docs all work) — **unlike the Mastodon cluster (S35, broken actor docs)**; so L2–L12 are runnable **once `iluser` is approved**. **Operator step:** approve the pending `iluser` (Lemmy admin) **or** set `registration_mode=Open` (then re-create `iluser` with a 10–60 char password). **L2–L12 BLOCKED** (need signed-in `iluser`). Detail in [iris-lemmy.md](interop-tests/iris-lemmy.md).
- **Checkpoint:** **BLOCKED** on Lemmy `iluser` approval (operator). Both peer suites (Mastodon M2–M12, Lemmy L2–L12) are gated on operator-provided accounts. See S35 + pass 98 for the Mastodon side.

## Pass 97 (2026-09-21) — Iris↔Iris interop A1–A10 re-test on fresh QA cluster
- **Build/Live:** fresh QA cluster — A `qa-iris-a.luit.ink`, B `qa-iris-b.luit.ink` (rebuilt). Accounts `ii-a1`/`ii-a2` (A), `ii-b1` (B); community `ii-a8-community`.
- **Explored:** full interop suite A1–A10 (bootstrap, follow, gated follow, post, reply, like, boost, community, edit/delete, unfollow) — Playwright + curl wire checks.
- **Result:** **FIXED:** S26 (reply threading), S29 (community webfinger), S34 (gated follow withheld until accept). **STILL OPEN:** S24 f1 (Following tab omits remote), S24 f2 (foreign activity in local outbox), S25 (remote post not in home feed), S27 (remote Like not applied), S30 (community discovery + community-post federation), S31 (edit clears `published` + UI no-refresh + drops peer copy), S33 (unfollow Undo not propagated). **PARTIALLY IMPROVED:** S28 (remote Boost now lands in Shares tab; button count + `/shares` endpoint still 0), S32 (local delete correct; peer propagation broken). **New:** remote Like/Boost now UI-testable (remote-object block gone); the `Update` removes the peer's copy (B 404 after edit). Full detail in [iris-iris.md](interop-tests/iris-iris.md) + S24–S34 finding docs.
- **Checkpoint:** next pass — Iris↔Mastodon suite (M1–M12), then Iris↔Lemmy (L1–L12).

## Pass 96 (2026-09-20) — S4/S17/S16-UX re-confirmed; S23 still open (container 14:09:44)
- **Build/Live:** container `irisweb-iris-web-1` started 14:09:44 (same as Pass 95; no new deploy).
- **Explored:** `/communities` (Following tab), `/profile` (3 tabs), object detail (poll).
- **Result:** **S4 re-confirmed OPEN** (22nd pass — remote interop missing). **S17 re-confirmed OPEN** (39 outbox requests, identical). **S16-UX re-confirmed OPEN** (1 votes, NO badge). **S23 still OPEN** (not re-tested, same container — Accept/Decline 404s). 2 console errors (ERR_NETWORK_CHANGED — environmental).
- **Checkpoint:** next pass — wait for dev to fix S23 (register accept/reject endpoints). Otherwise: S4, S17, or S16-UX.

## Pass 95 (2026-09-20) — S22 FIXED; S19 facet 2 PARTIALLY FIXED; NEW BUG S23 (Accept/Decline 404) (container 14:09:44)
- **Build/Live:** container `irisweb-iris-web-1` started 14:09:44 (7th restart; dev deployed `ee47565` — "Fix notification follow-filter + verify S19 facet 3").
- **Explored:** `/notifications` (Follows tab — 7 follow requests with Accept/Decline buttons), Accept/Decline button testing (404 errors), DB edge verification.
- **Result:** **S22 FIXED** — API now returns 7 Follow notifications; UI shows all 7 with Accept/Decline buttons. **S19 facet 2 PARTIALLY FIXED** — buttons visible but clicking Accept → `POST /local/v1/u/andrew/requests/accept/{iri}` → **404**; Decline → `.../reject/{iri}` → **404**. DB edges unchanged. **NEW BUG S23** filed. **S4 re-confirmed OPEN** (21st pass — not re-tested this pass, same container).
- **Checkpoint:** next pass — wait for dev to fix S23 (register accept/reject endpoints), then re-verify S19 facet 2 end-to-end. Otherwise: S4, S17, or S16-UX.

## Pass 94 (2026-09-20) — S4 20th pass; Peers tab verified; session hydration delay confirmed (container 13:26:35)
- **Build/Live:** container `irisweb-iris-web-1` started 13:26:35 (same as Pass 92/93; no new deploy).
- **Explored:** Fresh login → `/home` (feed renders after ~15s, 8 posts), `/communities` (Following tab, 3 local communities), `/profile` (renders correctly), community detail (Peers tab — 1 peer: andrew, with Follow/Look up/Refresh). User report investigated: "feed doesn't render unless I click around; some pages say I'm not logged in."
- **Result:** **S4 re-confirmed OPEN** (20th pass — remote interop missing). **Peers tab works** (verified on qa-pass46-test). **Session hydration delay confirmed:** after fresh login, `/home` feed takes ~15s to render; `/communities` briefly shows "Sign in to browse communities" before the session hydrates and content appears. No console errors (0). This matches the user's report — the feed and some pages appear empty or show "Sign in" until the WASM session initializes. **S17/S16-UX unchanged** (not re-tested, same container).
- **Checkpoint:** next pass — wait for dev to fix S22 (notification query), then re-verify S19 facet 2. Otherwise: S4, S17, or S16-UX.

## Pass 93 (2026-09-20) — S22 root cause identified; S4/S17/S16-UX re-confirmed (container 13:26:35)
- **Build/Live:** container `irisweb-iris-web-1` started 13:26:35 (same as Pass 92; no new deploy).
- **Explored:** DB investigation (`Activities`, `BoxItems`, `Edges` tables), API endpoint testing (all notification types), `/notifications` (Follows tab), `/communities` (Following tab), `/profile` (3 tabs), object detail (poll).
- **Result:** **S22 ROOT CAUSE IDENTIFIED:** Follow activities exist in `Activities` (7 rows targeting andrew) and `BoxItems` (inbox, Direction=1) but `GET /local/v1/notifications?type=Follow` returns 0 items. Other types work: Like (13), Announce (77), Mention (3). The API's Follow-type filter does not resolve local Follow activities from the inbox. **S4 re-confirmed OPEN** (19th pass). **S17 re-confirmed OPEN** (39 outbox requests). **S16-UX re-confirmed OPEN** (1 votes, NO badge).
- **Checkpoint:** next pass — wait for dev to fix S22 (notification query), then re-verify S19 facet 2 (Accept/Decline buttons). Otherwise: S4, S17, or S16-UX.

## Pass 92 (2026-09-20) — S22 re-confirmed (2nd pass); S4/S17/S16-UX re-confirmed; Peers tab works (container 13:26:35)
- **Build/Live:** container `irisweb-iris-web-1` started 13:26:35 (6th restart today; dev deployed `45e5b70` — Peers tab).
- **Explored:** `/notifications` (Follows tab), `/register` (new account qa92test), actor page (follow andrew), `/communities` (Following tab), `/profile` (3 tabs), object detail (poll), community detail (Peers tab).
- **Result:** **S22 re-confirmed OPEN** (2nd pass — new follow from qa92test → andrew: edge exists in DB but NO notification created; API returns 0 items; both qa91test and qa92test affected); **S19 facet 2 CANNOT RE-VERIFY** (blocked by S22); **S4 re-confirmed OPEN** (18th pass — remote interop missing); **S17 re-confirmed OPEN** (39 outbox requests, identical); **S16-UX re-confirmed OPEN** (1 votes, NO badge); **Peers tab works correctly** (shows 1 peer: andrew, with Follow/Look up/Refresh actions).
- **Checkpoint:** next pass — investigate S22 root cause (DB: check if notification rows exist for follow edges), S4, S17, or S16-UX.

## Pass 91 (2026-09-20) — S19 facet 3 FIXED; NEW BUG: follow notifications not created (container 13:19:52)
- **Build/Live:** container `irisweb-iris-web-1` started 13:19:52 (5th restart today; dev deployed `a661cdd`).
- **Explored:** `/home`, `/register` (new account qa91test), actor page (follow andrew), `/notifications` (Follows tab), community detail (Edit Save), `/communities` (Following tab), `/profile` (3 tabs + Communities tab), object detail (poll).
- **Result:** **S19 facet 3 FIXED** (Edit Save persists — API returns updated description); **S19 facet 2 CANNOT RE-VERIFY — NEW BUG** (follow from qa91test → andrew: edge exists in DB but NO notification created; API returns 0 items); **S4 re-confirmed OPEN** (17th pass — remote interop missing from Following + Profile Communities tabs); **S17 re-confirmed OPEN** (39 outbox requests for 3 tabs, identical to Pass 90); **S16-UX re-confirmed OPEN** (1 votes, NO badge). 2 console errors (ERR_NETWORK_CHANGED — environmental).
- **Checkpoint:** next pass — investigate NEW BUG (follow notifications not created), S4, S17, or S16-UX.

## Pass 90 (2026-09-20) — S20/S21/S19 facet 1 FIXED; S4/S17/S16-UX re-confirmed (container 12:53:13)
- **Build/Live:** container `irisweb-iris-web-1` started 12:53:13 (4th restart today).
- **Explored:** `/home` (Posts/Communities tabs), `/c/{handle}` redirect, `/communities` (Following tab), community detail (Requests tab + Edit Save), object detail (poll badge), `/profile` (3 tabs).
- **Result:** **S20 FIXED** (Communities tab fires `?source=communities` → 200; Posts tab fires `?source=people` → 200); **S21 FIXED** (`/c/technology` + `/c/qa-pass46-test` redirect to `/community?iri=…`); **S19 facet 1 FIXED** (Requests tab → `GET /local/v1/c/qa-pass46-test/requests` → 200, shows "No pending join requests"); **S19 facet 2 CANNOT RE-VERIFY** (follow requests gone from DB — accounts removed during restart); **S19 facet 3 STILL OPEN** (Edit Save → 202 but DB summary NULL); **S4 re-confirmed OPEN** (16th pass — remote interop missing); **S17 re-confirmed OPEN** (39 outbox requests for 3 tabs, increased from 35); **S16-UX re-confirmed OPEN** (1 votes, NO badge).
- **Checkpoint:** next pass — S17, S16-UX, or S19 facet 2 (need new test accounts for follow requests).

## Pass 89 (2026-09-20) — S17 re-verify (profile over-fetch, container 12:43:01)

- **Build/Live:** Container 12:43:01. Healthy.
- **Explored:** S17: `/profile` — initial 9 outbox requests; Replies tab 13 requests; Likes tab 13 requests. Total 35 outbox requests for 3 tabs.
- **Result:** 0 new; **S17 re-confirmed OPEN** (35 outbox requests for 3 tabs; identical to Pass 76/80 — no change).
- **Checkpoint:** next pass targets S21 (/c/{handle}), S4 (remote communities), S20 (Communities tab).

---

## Pass 88 (2026-09-20) — S4 re-verify (remote interop missing, 15th pass)

- **Build/Live:** Container 12:43:01. Healthy.
- **Explored:** S4: Following tab — 3 local communities, remote `interop` still missing. `interop` actor page shows "Unfollow" (follow edge confirmed).
- **Result:** 0 new; **S4 re-confirmed OPEN** (remote interop missing — 15th consecutive pass).
- **Checkpoint:** next pass targets S17 (profile over-fetch), S16-UX (poll badge), S21 (/c/{handle}).

---

## Pass 87 (2026-09-20) — S20 re-verify (Communities tab visual-only, 9th pass)

- **Build/Live:** Container 12:43:01. Healthy.
- **Explored:** S20: `/home` → Communities tab → 0 new API requests, feed content identical to Posts tab (8 boosted posts).
- **Result:** 0 new; **S20 re-confirmed OPEN** (visual-only toggle — 9th consecutive pass).
- **Checkpoint:** next pass targets S4 (remote communities), S17 (profile over-fetch), S16-UX (poll badge).

---

## Pass 86 (2026-09-20) — S19 facet 2 + S21 re-verify (container restarted 12:43:01)

- **Build/Live:** Container restarted 12:43:01. Healthy. Dev PLAN.md updated: "S19 all facets fixed + deployed".
- **Explored:** S19 facet 2: Notifications — 3 follow requests, NO Accept/Decline buttons (still absent post-restart). S21: `/c/technology` → "Not found" (redirect not active).
- **Result:** 0 new; **S19 facet 2 STILL OPEN** (Accept/Decline buttons not visible even after container restart — WASM may not include `8b6a234`). **S21 /c/{handle} re-confirmed OPEN** (13th pass).
- **Checkpoint:** next pass targets S4 (remote communities), S20 (Communities tab), S17 (profile over-fetch). Investigate whether dev's rebuild actually included `8b6a234`.

---

## Pass 85 (2026-09-20) — S19 facet 2 re-verify (deployment gap persists)

- **Build/Live:** Container 12:15:50. Dev commit `8b6a234` (Accept/Decline buttons) still not deployed.
- **Explored:** S19 facet 2: Notifications — 3 follow requests, NO Accept/Decline buttons. S21: `/c/qa-pass46-test` → "Not found" (redirect not active).
- **Result:** 0 new; **S19 facet 2 deployment gap persists** (commit `8b6a234` not in live build). S21 /c/{handle} re-confirmed (12th pass).
- **Checkpoint:** next pass targets S4 (remote communities), S20 (Communities tab), S17 (profile over-fetch). Re-verify S19 facet 2 + S21 after next deploy.

---

## Pass 84 (2026-09-20) — S4/S21 re-verify (container 12:15:50)

- **Build/Live:** Container 12:15:50. Healthy.
- **Explored:** S4: Following tab — 3 local communities, remote `interop` still missing. S21: `/c/technology` → "Nothing at this address." (redirect not active).
- **Result:** 0 new; **S4 re-confirmed OPEN** (remote interop missing — 14th pass). **S21 /c/{handle} re-confirmed OPEN** (redirect not active — 11th pass). Note: `qa-pass46-test` description now shows "QA Pass 79 re-verify" (Pass 79 Edit partially persisted).
- **Checkpoint:** next pass targets S19 facet 2 (re-verify after deploy), S20 (Communities tab), S17 (profile over-fetch).

---

## Pass 83 (2026-09-20) — S16-UX re-verify (badge missing, IRI format changed)

- **Build/Live:** Container 12:15:50. Healthy.
- **Explored:** S16-UX: Poll `06GBVZDNA8JPCCFC8WW2JGAFZR` object detail — 1 votes, NO badge. DB confirms voters=[andrew], totalVotes=1. Old IRI format (without `/objects/`) returns "Object not found".
- **Result:** 0 new; **S16-UX re-confirmed OPEN** (badge missing on object detail, vote count consistent). New note: IRI format changed, old URLs not redirected.
- **Checkpoint:** next pass targets S21 (/c/{handle}), S4 (remote communities), S19 facet 2 (re-verify after deploy).

---

## Pass 82 (2026-09-20) — S20 re-verify (Communities tab visual-only, 8th pass)

- **Build/Live:** Container 12:15:50. Healthy.
- **Explored:** S20: `/home` → Communities tab → 0 new API requests, feed content identical to Posts tab.
- **Result:** 0 new; **S20 re-confirmed OPEN** (visual-only toggle — 8th consecutive pass).
- **Checkpoint:** next pass targets S21 (/c/{handle}), S4 (remote communities), S19 facet 2 (re-verify after deploy).

---

## Pass 81 (2026-09-20) — S19 facet 2 re-verify (deployment gap)

- **Build/Live:** Container built 12:15:50. Dev commit `8b6a234` (Accept/Decline buttons) landed 12:31:16 — AFTER build.
- **Explored:** S19 facet 2: Notifications page — 3 follow requests (qa39test, qa36test, qa34test) with NO Accept/Decline buttons.
- **Result:** 0 new; **S19 facet 2 deployment gap** — fix commit not in live build. Notifications still missing Accept/Decline.
- **Checkpoint:** next pass targets S21 (/c/{handle}), S4 (remote communities), S20 (Communities tab). Re-verify S19 facet 2 after next deploy.

---

## Pass 80 (2026-09-20) — S17 re-verify (profile over-fetch, container 12:15:50)

- **Build/Live:** Container restarted 12:15:50. Healthy.
- **Explored:** S17: `/profile` — initial 9 outbox requests; Replies tab 13 requests; Likes tab 13 requests. Total 35 outbox requests for 3 tabs.
- **Result:** 0 new; **S17 re-confirmed OPEN** (35 outbox requests for 3 tabs; identical to Pass 76 — no change).
- **Checkpoint:** next pass targets S21 (/c/{handle}), S4 (remote communities), S20 (Communities tab).

---

## Pass 79 (2026-09-20) — S19 re-verify (Edit community no-op, container 12:15:50)

- **Build/Live:** Container restarted 12:15:50. Healthy.
- **Explored:** S19: `qa-pass46-test` Edit community — checkbox checked (UI state), Description changed → Save → POST outbox 202, body has new description but NO requireApproval. DB: summary NULL, requireApproval NULL, CreatedAt unchanged.
- **Result:** 0 new; **S19 re-confirmed OPEN** (Update 202 but not persisted; requireApproval not in Update activity; DB unchanged).
- **Checkpoint:** next pass targets S21 (/c/{handle}), S4 (remote communities), S17 (profile over-fetch).

---

## Pass 78 (2026-09-20) — S4/S21 re-verify (container restarted 12:15:50)

- **Build/Live:** Container restarted 12:15:50. Healthy.
- **Explored:** S4: Following tab — 3 local communities, remote `interop` still missing. `interop` actor page shows "Unfollow". S21: `/c/technology` → "Nothing at this address." (redirect not active after restart).
- **Result:** 0 new; **S4 re-confirmed OPEN** (remote interop missing — 13th pass). **S21 /c/{handle} re-confirmed OPEN** (redirect not active — 10th pass, persists after container restart).
- **Checkpoint:** next pass targets S19 (Edit community no-op), S17 (profile over-fetch), S20 (Communities tab).

---

## Pass 77 (2026-09-20) — S20/S16-UX re-verify (Dev fix deployed)

- **Build/Live:** Dev fix deployed. Container healthy.
- **Explored:** S20: `/home` Communities tab → 0 new API requests, 0 console errors. S16-UX: Poll object detail: A:1/B:0/**1 votes**, NO "You voted" badge.
- **Result:** 0 new; **S20 re-confirmed OPEN** (7th pass, visual-only toggle). **S16-UX re-confirmed OPEN** (badge missing on object detail; vote count consistent).
- **Checkpoint:** next pass targets S21 (/c/{handle}), S4 (remote communities), S19 (Edit community no-op).

---

## Pass 76 (2026-09-20) — S17/S19 re-verify (Dev fix deployed)

- **Build/Live:** Dev fix deployed. Container healthy.
- **Explored:** S17: `/profile` — initial 9 pages, Replies 13 pages, Likes 13 pages (35 total). S19: Notifications All tab — 3 follow requests, NO Accept/Decline. Andrew actor page — no Requests tab.
- **Result:** 0 new; **S17 re-confirmed OPEN** (35 outbox requests for 3 tabs; initial 9, tab-switch 13 each). **S19 re-confirmed OPEN** (no Accept/Decline in notifications; no Requests tab on actor page).
- **Checkpoint:** next pass targets S20 (Communities tab), S21 (/c/{handle}), S16-UX (poll badge).

---

## Pass 75 (2026-09-20) — S4/S21 re-verify (Dev fix deployed)

- **Build/Live:** Dev fix deployed. Container healthy.
- **Explored:** S4: Communities → Following tab: 3 local communities (technology, qa-pass46-test, qa-pass65-test) — remote `lemmy.luit.ink/c/interop` still missing. `interop` actor page shows "Unfollow" button (follow edge exists). S21: `/c/technology` → "Sorry, there's nothing at this address." (redirect not active).
- **Result:** 0 new; **S4 re-confirmed OPEN** (remote interop missing — 12th pass). **S21 /c/{handle} re-confirmed OPEN** (redirect not active — 9th pass).
- **Checkpoint:** next pass targets S17 (profile over-fetch), S19 (notifications Accept/Decline), S16-UX (poll badge).

---

## Pass 74 (2026-09-20) — S19 re-verify (Dev fix deployed)

- **Build/Live:** Dev fix deployed. Container healthy.
- **Explored:** S19: Community `technology` — (1) Edit community: changed Description → Save → `POST /ap/v1/c/technology/outbox` → 202, body includes new description, but DB `summary` is NULL (not persisted). (2) Requests tab: "We couldn't load the join requests" — 0 new API requests.
- **Result:** 0 new; **S19 re-confirmed OPEN** (Update activity 202 but not persisted to DB; Requests tab still has no backing endpoint). Improvement: Update body now includes the changed field (was missing in Pass 52), but the DB is still not updated.
- **Checkpoint:** next pass targets S17 (profile over-fetch), S21 (/c/{handle}), S4 (remote communities).

---

## Pass 73 (2026-09-20) — S20/S2/S14 re-verify (Dev fix deployed)

- **Build/Live:** Dev fix deployed. Container healthy.
- **Explored:** S20: `/home` Communities tab → 0 new API requests, 0 console errors. S2: clean entry → `/` → 2 proxy 401 errors. S14: signed-out `/actor?iri=mastodon.social/users/deadline` → 5 errors (1 proxy 401 + 4 CSP).
- **Result:** 0 new; **S20 re-confirmed OPEN** (6th pass, visual-only toggle). **S2 re-confirmed OPEN** (2 proxy 401 — no change). **S14 re-confirmed OPEN** (5 errors — no change).
- **Checkpoint:** next pass targets S19 (Edit community no-op), S17 (profile over-fetch), S21 (/c/{handle}).

---

## Pass 72 (2026-09-20) — S16-UX re-verify (Dev fix deployed)

- **Build/Live:** Dev fix deployed.
- **Explored:** S16-UX: Poll `06GBVZDNA8JPCCFC8WW2JGAFZR` — (1) Object detail: A:1/B:0/**1 votes**, NO "You voted" badge. (2) Profile listing (after 2× Load more): A:0/B:0/**0 votes**, NO badge. **REGRESSION:** vote count now inconsistent (profile 0, object 1).
- **Result:** 0 new; **S16-UX re-confirmed OPEN** (vote count inconsistent: profile shows 0, object shows 1; badge missing from both). Regression from Pass 56.
- **Checkpoint:** next pass targets S2/S14 (proxy 401), S20 (Communities tab), S17 (profile over-fetch).

---

## Pass 71 (2026-09-20) — S4/S21 re-verify (Dev fix deployed)

- **Build/Live:** Dev fix deployed.
- **Explored:** S4: Communities → Following tab shows **"technology" + "qa-pass46-test" + "qa-pass65-test"** (all local) — remote `lemmy.luit.ink/c/interop` **still missing**. Actor page shows "Unfollow" button (follow edge exists). S21: `/c/technology` → "Sorry, there's nothing at this address." — redirect page **STILL NOT active**. `/c/qa-pass65-test` → same.
- **Result:** 0 new; **S4 re-confirmed OPEN** (remote interop missing — 11th consecutive pass). **S21 /c/{handle} STILL OPEN** (redirect not active — 8th pass).
- **Checkpoint:** next pass targets S2/S14 (proxy 401), S16-UX (poll badge), S20 (Communities tab).

---

## Pass 70 (2026-09-20) — S19 re-verify (Dev fix deployed)

- **Build/Live:** Dev fix deployed.
- **Explored:** S19: (1) `qa-pass46-test` → Edit community → checkbox "Require approval for join requests" is **checked** → Save. Reopened form → checkbox still checked (UI state). DB: `requireApproval` → **NULL**. 0 Update activities in DB. (2) Notifications → All: 3 follow requests (qa39test, qa36test, qa34test) — **NO Accept/Decline buttons**.
- **Result:** 0 new; **S19 re-confirmed OPEN** (Edit community Save is a silent no-op — checkbox state not persisted; notification action buttons still missing).
- **Checkpoint:** next pass targets S21 (/c/{handle} redirect), S2/S14 (proxy 401), S4 (remote interop missing).

---

## Pass 69 (2026-09-20) — S17 re-verify (Dev fix deployed)

- **Build/Live:** Dev fix deployed.
- **Explored:** S17: `/profile` → "Your posts" fired **6 outbox requests** (pages 1–6). Replies tab → **13-page fan-out** (pages 1–13). Likes tab → **13-page fan-out** (pages 1–13). Total: **32 outbox requests** for 3 tabs viewed.
- **Result:** 0 new; **S17 re-confirmed OPEN** (32 outbox requests for 3 tabs; initial load 6 pages, tab-switch still 13 pages each).
- **Checkpoint:** next pass targets S21 (/c/{handle} redirect), S2/S14 (proxy 401), S4 (remote interop missing).

---

## Pass 68 (2026-09-20) — S3 re-verify (Dev fix `c205d47` deployed)

- **Build/Live:** Dev fix `c205d47` ("S3: skip visibility gate for Activities in ObjectDocumentHandler") deployed.
- **Explored:** S3: Created fresh post "QA Pass 68: S3 Create persistence test post" (Note IRI: `…/notes/06GBX58AD4HAFES1WMBJ464HP0`, Create IRI: `…/creates/06GBX58AD4HAFES1WMBJ464HNW`). POST → 202. DB: Create IRI → 0 rows (not persisted). Note IRI → 200. **Create IRI → 200** (was 404 before). UI: `/object?iri=…/creates/…` → renders the Note content correctly. 0 console errors.
- **Result:** 0 new; **S3 FIXED (UI-facing)** — Create IRI no longer 404s; object detail page renders correctly. The visibility gate fix makes the Create IRI resolvable.
- **Checkpoint:** next pass targets S17 (profile over-fetch), S21 (/c/{handle} redirect), S2/S14 (proxy 401).

---

## Pass 67 (2026-09-20) — S20 re-verify (Dev fix deployed)

- **Build/Live:** Dev fix deployed (container restarted 11:21:16).
- **Explored:** S20: `/home` → Posts tab loads feed (`GET /ap/v1/u/andrew/feed` → 200, `?page=2` → 200). Click **Communities** tab → **0 new API requests** (no `?source=` parameter, no community-specific call). Feed content identical to Posts tab.
- **Result:** 0 new; **S20 re-confirmed OPEN** (Communities tab visual-only, 0 new API requests — 5th consecutive pass).
- **Checkpoint:** next pass targets S17 (profile over-fetch), S3 (Create persistence), S2/S14 (proxy 401).

---

## Pass 66 (2026-09-20) — S21 re-verify (Dev fix `8539f3f` deployed)

- **Build/Live:** Dev fix `8539f3f` deployed (container restarted 11:21:16).
- **Explored:** S21: (1) `/c/qa-pass65-test` → Blazor SPA shows **"Sorry, there's nothing at this address."** — the `CommunityHandleRedirect.razor` page is **NOT active** (no redirect). Same for `/c/technology`. (2) `/communities` → Following tab now shows **"technology" + "qa-pass46-test" + "qa-pass65-test"** (all with "Leave") — `qa-pass65-test` **IS in the Following tab** (auto-follow working). (3) `/community?iri=…/c/qa-pass65-test` → 200.
- **Result:** **S21 PARTIALLY FIXED.** Auto-follow (facet 1) is **FIXED**. `/c/{handle}` route (facet 2) is **STILL OPEN** — redirect page not active; all `/c/{handle}` URLs show "Nothing at this address."
- **Checkpoint:** next pass targets S20 (Communities tab visual-only), S17 (profile over-fetch), S3 (Create persistence).

---

## Pass 65 (2026-09-20) — S21 re-verify on `65ccfa0` (Dev fix `8539f3f` NOT deployed)

- **Build/Live:** deployed `65ccfa0` (Dev fix `8539f3f` on `interop-testing` but NOT deployed to live container — container started 11:05:19).
- **Explored:** S21: Created new community `qa-pass65-test` via `/communities` → "+ Create a community". After creation: (1) `/communities` → Following tab shows **"technology" + "qa-pass46-test"** — `qa-pass65-test` is **NOT in the Following tab** (no auto-follow). (2) `/c/qa-pass65-test` → **502 Bad Gateway** (route still missing — 7th consecutive pass). (3) `/community?iri=…/c/qa-pass65-test` → **200** (community page renders).
- **Result:** 0 new; **S21 re-confirmed OPEN** (no auto-follow + /c/{handle} route missing — 7th consecutive pass). Dev fix `8539f3f` exists on `interop-testing` but is NOT deployed.
- **Checkpoint:** next pass targets S20 (Communities tab visual-only), S17 (profile over-fetch), S2/S14 (proxy 401).

---

## Pass 64 (2026-09-20) — S3 re-verify on `65ccfa0`

- **Build/Live:** deployed `65ccfa0` (== HEAD? y).
- **Explored:** S3: Created fresh post "QA Pass 64: S3 Create persistence test post" (Note IRI: `…/notes/06GBX23XJHPVJ6HGNE4ZVE5PCW`, Create IRI: `…/creates/06GBX23XJHPVJ6HGNE4ZVE5PCR`). POST `/ap/v1/u/andrew/outbox` → **202 Accepted**. DB: Create IRI → **0 rows**. Only the Note is stored (1 row). Note IRI → **200** (clean render). Create IRI → **HTTP 404** + "Object not found." alert + 1 console 404 error.
- **Result:** 0 new; **S3 re-confirmed OPEN** (Create activities not persisted to DB — 7th consecutive pass).
- **Checkpoint:** next pass targets S21 (no auto-follow + /c/{handle} 404), S20 (Communities tab visual-only), S17 (profile over-fetch).

---

## Pass 63 (2026-09-20) — S19 re-verify on `65ccfa0`

- **Build/Live:** deployed `65ccfa0` (== HEAD? y).
- **Explored:** S19: Community `technology` (andrew, owner) → **Requests tab** → alert: *"We couldn't load the join requests. Please try again."* — **NO network request fired** for the requests endpoint (0 requests matching `requests|join` in network log). API probe: `GET /ap/v1/c/technology/requests` → **404** (endpoint does not exist). `GET /ap/v1/c/technology/members` → **200**. The Requests tab has no backing endpoint and no retry mechanism (no Refresh button).
- **Result:** 0 new; **S19 re-confirmed OPEN** (requests endpoint still 404s; UI error without API call; no retry button — 3rd community tested: technology, qa-pass46-test, qa-pass51-test).
- **Checkpoint:** next pass targets S3 (Create persistence), S21 (no auto-follow + /c/{handle} 404), S20 (Communities tab visual-only).

---

## Pass 62 (2026-09-20) — S4/S16-UX re-verify on `65ccfa0`

- **Build/Live:** deployed `65ccfa0` (== HEAD? y).
- **Explored:** S4: Communities → Following tab shows **"technology" + "qa-pass46-test"** (both local, both with "Leave" button) — the remote `lemmy.luit.ink/c/interop` community is **still missing**. Confirmed the follow edge exists: navigating to `lemmy.luit.ink/c/interop` actor page shows **"Unfollow" button** (not "Follow"). 0 console errors. S16-UX: Poll `06GBVZDNA8JPCCFC8WW2JGAFZR` — Object detail page: fresh load shows A:1/B:0/**1 votes** + **NO "You voted" badge**. Vote count consistent (1). Badge still missing on object detail page. Core data-integrity still FIXED.
- **Result:** 0 new; **S4 re-confirmed OPEN** (remote `interop` community missing from Following tab — 10th consecutive pass). **S16-UX re-confirmed OPEN** (poll "You voted" badge missing on object detail page — vote count consistent at 1).
- **Checkpoint:** next pass targets S19 (requests endpoint missing + Edit community no-op), S3 (Create persistence), S20 (Communities tab visual-only).

---

## Pass 61 (2026-09-20) — S20/S17 re-verify on `65ccfa0`

- **Build/Live:** deployed `65ccfa0` (== HEAD? y).
- **Explored:** S20: `/home` → Posts tab shows following feed (WeirdWriter boost first, 8 feed requests). Click **Communities** tab → **0 new API requests** (no `?source=` parameter, no community-specific call). Feed content **identical to Posts tab** (same first post, same items). 0 console errors. S17: `/profile` → "Your posts" fired `outbox` **4 times** (pages 1–4). Switched to **Replies** tab → fired the **full 13-page outbox fan-out** (pages 1–13). Switched to **Likes** tab → fired the **full 13-page outbox fan-out a third time** (pages 1–13). Total: **30 outbox requests** for 3 tabs viewed.
- **Result:** 0 new; **S20 re-confirmed OPEN** (Communities tab is visual-only — 0 new API requests on tab switch, feed data never changes — 4th consecutive pass). **S17 re-confirmed OPEN** (30 outbox requests for 3 tabs — initial load reduced to 4 pages, but tab-switch still pulls all 13 pages each time).
- **Checkpoint:** next pass targets S4 (remote interop missing from Following), S16-UX (poll badge missing on object detail), S19 (requests endpoint missing).

---

## Pass 60 (2026-09-20) — S2/S14 re-verify on `65ccfa0`

- **Build/Live:** deployed `65ccfa0` (== HEAD? y).
- **Explored:** S2: Signed-out `/` → **2 console errors**: 2× proxy 401 (mastodon.social/users/deadline, mastodon.social/users/arstechnica). **Identical to Pass 54** — the proxy still 401s unsigned GETs for Mastodon username-path actors. No CORS/direct-fallback errors. S14: Signed-out `/actor?iri=https://mastodon.social/users/deadline` → **5 console errors**: 1× proxy 401 + 4× CSP-violation on the direct fallback (`connect-src 'self'`). Page shows **"Failed to load actor. It may not exist or the server is unreachable."** **Identical failure mode to Pass 54 and Pass 32.**
- **Result:** 0 new; **S2 re-confirmed OPEN** (proxy 401 for unsigned Mastodon GETs persists — no change from Pass 54). **S14 re-confirmed OPEN** (proxy 401 + CSP violations on actor detail — identical to Pass 54 and Pass 32).
- **Checkpoint:** next pass targets S20 (Communities tab visual-only), S17 (profile over-fetch), S4 (remote interop missing from Following).

---

## Pass 59 (2026-09-20) — S3 Create persistence re-verify on `65ccfa0`

- **Build/Live:** deployed `65ccfa0` (== HEAD? y).
- **Explored:** S3: Created fresh post "QA Pass 59: S3 Create persistence test post" (Note IRI: `…/notes/06GBWVKQFPS6H3NFHHN456DHN0`, Create IRI: `…/creates/06GBWVKQFPS6H3NFHHN456DHMW`). POST `/ap/v1/u/andrew/outbox` → **202 Accepted** (UI shows "Posted (HTTP 202)"). DB query: `SELECT "Id", "ObjectType" FROM "Objects" WHERE "Id" = '…/creates/06GBWVKQFPS6H3NFHHN456DHMW'` → **0 rows**. `SELECT "Id", "ObjectType" FROM "Objects" WHERE "Document" @> '{"type":"Create"}' AND "Document"->>'actor' = '…/u/andrew'` → **0 rows**. Note IRI → **200** (content verified, clean render). Create IRI → **HTTP 404** + "Object not found." alert + 1 console 404 error.
- **Result:** 0 new; **S3 re-confirmed OPEN** (Create activities not persisted to DB — 6th consecutive pass. Root cause: server generates Create IRI in 202 response but never stores it).
- **Checkpoint:** next pass targets S20 (Communities tab visual-only), S17 (profile over-fetch), S2/S14 (proxy 401).

---

## Pass 58 (2026-09-20) — S21 re-verify on `65ccfa0`

- **Build/Live:** deployed `65ccfa0` (== HEAD? y).
- **Explored:** S21: Created new community `qa-pass58-test` via `/communities` → "+ Create a community" (Name: "QA Pass 58 test community", Handle: `qa-pass58-test`). After creation: (1) `/communities` → **Following tab shows only "technology"** — `qa-pass58-test` is **NOT in the Following tab** (no auto-follow on creation). "All on this instance" tab shows `qa-pass58-test`. (2) `/c/qa-pass58-test` → **404 "Not found"** in Blazor SPA (route still missing). (3) `/community?iri=…/c/qa-pass58-test` → **200** (community page renders with tabs: Feed, Members (0), Owners, Peers, Requests). (4) DB: Group document exists (`https://iris.luit.ink/ap/v1/c/qa-pass58-test`, ObjectType: Group, name: "QA Pass 58 test community"). 0 console errors on community page.
- **Result:** 0 new; **S21 re-confirmed OPEN** (no auto-follow on community creation — creator must manually follow their own community; `/c/{handle}` route still missing — 6th consecutive pass).
- **Checkpoint:** next pass targets S3 (Create persistence), S20 (Communities tab visual-only), S2/S14 (proxy 401).

---

## Pass 57 (2026-09-20) — S19 re-verify on `65ccfa0`

- **Build/Live:** deployed `65ccfa0` (== HEAD? y).
- **Explored:** S19: Community `qa-pass46-test` (andrew, owner) → "Edit community" → checked "Require approval for join requests" checkbox → clicked Save. **DB verification:** `SELECT "Id", "ObjectType", "CreatedAt" FROM "Objects" WHERE "Document" @> '{"type":"Update"}' AND "Document"->>'actor' = 'https://iris.luit.ink/ap/v1/u/andrew'` → **0 rows** (no Update activity persisted). Reopened Edit community form → checkbox `edit-community-approve-members` is **unchecked** (state did not persist). The Edit community Save is a **silent no-op** — the checkbox state is not included in the Update activity, and the DB document is unchanged. 3 console errors (502 on lemmy.ml proxy — unrelated to S19). **Notifications → All tab:** 3 follow requests visible (qa39test 2h ago, qa36test 3h ago, qa34test 4h ago) — **ALL have NO Accept/Decline buttons** (only "View andrew's profile" link).
- **Result:** 0 new; **S19 re-confirmed OPEN** (Edit community no-op re-confirmed on different community `qa-pass46-test`; notification action buttons still missing — 3 follow requests pending, none actionable).
- **Checkpoint:** next pass targets S21 (no auto-follow + /c/{handle} 404), S3 (Create persistence), S2/S14 (proxy 401).

---

## Pass 56 (2026-09-20) — S16-UX/S4 re-verify on `65ccfa0`

- **Build/Live:** deployed `65ccfa0` (== HEAD? y).
- **Explored:** S16-UX: Poll `06GBVZDNA8JPCCFC8WW2JGAFZR` — (1) Profile "Your posts" listing: shows A:1/B:0/**1 votes** + **"You voted" badge** visible (badge now appears in profile context — regression from Pass 49 where badge was missing). (2) Object detail page: fresh load shows A:1/B:0/**1 votes** + **NO "You voted" badge**. Vote count now consistent across views (1 in both), but "You voted" badge still missing on object detail page while visible in profile listing. Inconsistent badge re-hydration persists. Core data-integrity still FIXED. S4: Communities → Following tab shows **"technology" + "qa-pass46-test"** (both local, both with "Leave" button) — the remote `lemmy.luit.ink/c/interop` community is **still missing**. Confirmed the follow edge exists: navigating to `lemmy.luit.ink/c/interop` actor page shows **"Unfollow" button** (not "Follow"). 0 console errors.
- **Result:** 0 new; **S16-UX partially improved** (vote count now consistent across views — 1 in both profile and object page; "You voted" badge now visible in profile listing) but **STILL OPEN** (badge still missing on object detail page). **S4 re-confirmed OPEN** (remote `interop` community missing from Following tab — 9th consecutive pass).
- **Checkpoint:** next pass targets S20 (Communities tab visual-only), S17 (profile over-fetch), S19 (requests endpoint missing).

---

## Pass 55 (2026-09-20) — S17/S20 re-verify on `65ccfa0`

- **Build/Live:** deployed `65ccfa0` (== HEAD? y).
- **Explored:** S17: `/profile` → 3 tabs clicked (Your posts, Replies, Likes). **39 outbox requests** (13 per tab × 3 tabs). Each tab click fires 13 sequential outbox requests (page 1–13). The profile page fetches the entire outbox for each tab — no server-side filtering by activity type. S20: `/home` → Posts tab shows following feed (skinnylatte dolphin boost first). Click **Communities** tab → **0 new API requests** (no `?source=` parameter, no community-specific call). Feed content **identical to Posts tab**. 0 console errors. Click back to **Posts** tab → 0 new API requests. The Communities tab is a **visual-only toggle** — changes CSS active state but does not trigger a new feed fetch.
- **Result:** 0 new; **S17 re-confirmed OPEN** (39 outbox requests for 3 tabs — 13 per tab, no server-side filtering). **S20 re-confirmed OPEN** (Communities tab is visual-only — 0 new API requests on tab switch, feed data never changes).
- **Checkpoint:** next pass targets S4 (remote interop missing from Following), S16-UX (poll vote inconsistency), S2/S14 (proxy 401 — partially improved).

---

## Pass 54 (2026-09-20) — S2/S14 re-verify on `65ccfa0`

- **Build/Live:** deployed `65ccfa0` (== HEAD? y).
- **Explored:** S2: Signed-out `/` → **2 console errors**: 2× proxy 401 (mastodon.social/users/arstechnica, mastodon.social/users/deadline). **REDUCED from 11 (Pass 36) to 2** — the numeric-ID direct-fallback CORS/ERR_FAILED errors are gone. Proxy still 401s unsigned GETs: `curl GET /ap/v1/proxy/https%3A%2F%2Fmastodon.social%2Fusers%2Fgnomon` → **401** `{"error":"Request not signed"}`. Hachyderm: `curl GET /ap/v1/proxy/https%3A%2F%2Fhachyderm.io%2Fusers%2Fskinkylatte` → **404** (not 401 — different failure mode). S14: Signed-out `/actor?iri=https://mastodon.social/users/deadline` → **5 console errors**: 1× proxy 401 + 4× CSP-violation on direct fallback (`connect-src 'self'`). Page shows **"Failed to load actor. It may not exist or the server is unreachable."** Identical failure mode to Pass 32.
- **Result:** 0 new; **S2 partially improved** (error count reduced 11→2, no more CORS/direct-fallback noise on home feed) but **STILL OPEN** (proxy 401 for unsigned Mastodon GETs persists). **S14 re-confirmed OPEN** (proxy 401 + CSP violations on actor detail, identical to Pass 32).
- **Checkpoint:** next pass targets S4 (remote interop missing from Following), S20 (Communities tab 403), S17 (profile over-fetch).

---

## Pass 53 (2026-09-20) — S3 Create persistence re-verify on `65ccfa0`

- **Build/Live:** deployed `65ccfa0` (== HEAD? y).
- **Explored:** S3: Created fresh post "QA Pass 53: S3 Create persistence test post" (Note IRI: `…/notes/06GBWMFJ8KJ33DWJ85NVTSDXH8`, Create IRI: `…/creates/06GBWMFJ8KJ33DWJ85NVTSDXH4`). POST `/ap/v1/u/andrew/outbox` → **202 Accepted** (UI: "Posted (HTTP 202)"). DB query: Create IRI → **0 rows** in `Objects`. `SELECT * FROM "Objects" WHERE "Document"->>'type' = 'Create' AND "Document"->>'actor' = '…/u/andrew'` → **0 rows**. Note IRI → **200** (content verified). Create IRI → **HTTP 404** + "Object not found." alert + 1 console 404 error.
- **Result:** 0 new; **S3 re-confirmed OPEN** (Create activities not persisted to DB — 5th consecutive pass; root cause: server generates Create IRI in 202 response but never stores the Create activity as an Object).
- **Checkpoint:** next pass targets S2/S14 (blocked), S4 (remote interop missing from Following), S20 (Communities tab 403).

---

## Pass 52 (2026-09-20) — S21/S19 re-verify + Edit community silent no-op on `65ccfa0`

- **Build/Live:** deployed `65ccfa0` (== HEAD? y).
- **Explored:** S21: Created new community `qa-pass51-test` via `/communities` → "+ Create a community". **Following tab** (immediately after creation): shows only "technology" + "qa-pass46-test" — `qa-pass51-test` **NOT present** (no auto-follow, 2nd consecutive pass). "All on this instance" tab: shows `qa-pass51-test`. `/c/qa-pass51-test` → **404** in Blazor SPA ("Sorry, there's nothing at this address."). Owner view via community card → `/community?iri=…/c/qa-pass51-test`: tabs **Feed, Members (0), Owners, Peers, Requests**. Owners: andrew. Members: "No members yet." **S19:** Requests tab: alert *"We couldn't load the join requests. Please try again."* — **NO network request fired** (UI error without API call). API: `GET /ap/v1/c/qa-pass51-test/requests` → **404**. `GET /ap/v1/c/qa-pass51-test/members` → **200**. `GET /local/v1/c/qa-pass51-test/owners` → **200**. **NEW — Edit community Save is silent no-op:** "Edit community" form (Name, Description, Icon, "Require approval for join requests" checkbox). Clicking Save fires `POST /ap/v1/c/qa-pass51-test/outbox` → **202 Accepted**, but request body contains **only the original Group document** (no `requireApproval`, no changes). DB `Objects` document **unchanged** after Save. The "Require approval for join requests" setting is **not persisted** — entire Edit community feature is a silent no-op.
- **Result:** 0 new; **S21 re-confirmed OPEN** (no auto-follow — 2nd pass; /c/{handle} 404 — 5th pass); **S19 re-confirmed OPEN** (Requests tab UI error without API call; /ap/v1/c/{name}/requests 404). **New facet:** Edit community Save is a silent no-op — checkbox state not included in Update activity, DB unchanged.
- **Checkpoint:** next pass targets S3 (Create persistence), S2/S14 (blocked), S4 (remote interop missing from Following).

---

## Pass 51 (2026-09-20) — S19 Follows tab re-verify on `65ccfa0`

- **Build/Live:** deployed `65ccfa0` (== HEAD? y).
- **Explored:** S19: Notifications → **Follows tab**: 5 follow requests visible — qa39test (1h ago), qa36test (2h ago), qa34test (3h ago), New User/newuser1 (1d ago), RayvenMX/mastodon.world (1d ago). **ALL have NO Accept/Decline buttons** — each shows only "View andrew's profile" link. The Follows filter tab correctly surfaces follow requests but provides no action buttons. 0 console errors.
- **Result:** 0 new; **S19 re-confirmed OPEN** (5 follow requests pending, none actionable — no Accept/Decline buttons in Follows tab).
- **Checkpoint:** next pass targets S21 (no auto-follow + /c/{handle}), S19 (requests endpoint missing + no Accept/Decline), S3 (Create persistence), S2/S14 (blocked).

---

## Pass 50 (2026-09-20) — S4 re-verify + Directory community follow state on `65ccfa0`

- **Build/Live:** deployed `65ccfa0` (== HEAD? y).
- **Explored:** S4: Communities → Following tab shows **"technology" + "qa-pass46-test"** (both local, both "Leave" button) — remote `lemmy.luit.ink/c/interop` **still missing**. Confirmed follow edge exists: `lemmy.luit.ink/c/interop` actor page shows **"Unfollow"** button. Directory → Communities tab (This instance): `interop` listed with **"Join" button** (not "Leave") — directory does not reflect the existing follow edge. New observation: Directory Communities tab shows "Join" instead of "Leave" for an already-followed remote community (UX inconsistency with Communities page which shows "Leave" for local follows).
- **Result:** 0 new; **S4 re-confirmed OPEN** (remote `interop` community missing from Communities → Following tab, 8 consecutive passes; follow edge confirmed via Unfollow button). New facet: Directory Communities tab shows "Join" for already-followed remote community (should show "Leave").
- **Checkpoint:** next pass targets S21 (no auto-follow + /c/{handle}), S19 (requests endpoint missing), S3 (Create persistence), S2/S14 (blocked).

---

## Pass 49 (2026-09-20) — S17/S16-UX/S20 re-verify on `65ccfa0`

- **Build/Live:** deployed `65ccfa0` (== HEAD? y).
- **Explored:** S17: `/profile` → "Your posts" fired `outbox` **8 times** (pages 1–8). Switched to **Replies** tab → full 13-page outbox fan-out (requests 91–103). Switched to **Likes** tab → full 13-page outbox fan-out a third time (requests 104–116). Switched back to **Your posts** → no additional requests (cached). Total: **34 outbox requests** for 3 tabs viewed. Replies tab: empty list, no "No replies yet" message. Likes tab: empty list with "Load more" button. S16-UX: Poll `06GBVZDNA8JPCCFC8WW2JGAFZR` — Profile listing: A:0/B:0/**0 votes**, NO badge. Object detail page: A:1/B:0/**1 votes**, NO badge. Vote count inconsistency persists (0 vs 1). Badge missing from both. S20: Home → Communities tab fires same `GET /ap/v1/u/andrew/feed` as Posts (no `?source=` param). API probe: `GET /ap/v1/u/andrew/feed?source=communities` → **403** (server rejects `source` param). `GET /ap/v1/u/andrew/feed` (no param) → 200. Server does not support `source` filtering.
- **Result:** 0 new; **S17 re-confirmed OPEN** (34 outbox requests for 3 tabs; tab-switch still pulls all 13 pages); **S16-UX re-confirmed OPEN** (vote count 0 in profile vs 1 in object; badge missing everywhere); **S20 re-confirmed OPEN** (server 403s `?source=` param — no backing endpoint for Communities tab).
- **Checkpoint:** next pass targets S21 (no auto-follow + /c/{handle}), S19 (requests endpoint missing), S3 (Create persistence), S2/S14 (blocked).

---

## Pass 48 (2026-09-20) — S19/S21 re-verify + API probe on `65ccfa0`

- **Build/Live:** deployed `65ccfa0` (== HEAD? y).
- **Explored:** S19: Community `technology` actor page: tabs **Posts (0), Followers (2), Following (0)** — no Requests, no Members tab. Followers tab: andrew + 1 other. API probes: `GET /ap/v1/c/technology/members` → **200**, `GET /ap/v1/c/technology/requests` → **404** (endpoint doesn't exist), `GET /local/v1/c/technology/owners` → **403** (unauthenticated). Community `qa-pass46-test` actor page: same tab structure, Followers (1). Notifications: follow requests still visible with NO Accept/Decline buttons. S21: `/communities` → Following tab now shows **both "technology" AND "qa-pass46-test"** (Pass 47's manual follow now reflected — likely Blazor cache refresh). Both show "Leave" button (not "Follow"). "All on this instance" tab shows both. `/c/qa-pass46-test` still 404. S20: Home → Communities tab fires **same** `GET /ap/v1/u/andrew/feed` as Posts tab — no community-specific call.
- **Result:** 0 new; **S19 re-confirmed OPEN** (`/ap/v1/c/{name}/requests` endpoint 404s — no backing endpoint for Requests tab); **S21 PARTIALLY RESOLVED** (manual follow now populates Following tab — caching issue, not a filter bug; but no auto-follow on creation + /c/{handle} still 404s); **S20 re-confirmed OPEN** (Communities tab uses same feed endpoint as Posts).
- **Checkpoint:** next pass targets S21 (no auto-follow on creation + /c/{handle} route), S19 (requests endpoint missing), S3 (Create persistence), S2/S14 (blocked).

---

## Pass 47 (2026-09-20) — S19/S21 re-verify on `65ccfa0`

- **Build/Live:** deployed `65ccfa0` (== HEAD? y).
- **Explored:** S19: Notifications — follow requests from qa34test/qa36test/qa39test visible, NO Accept/Decline buttons. Community actor pages (technology, qa-pass46-test): tabs Posts/Followers/Following only — no Requests, no Members. `/c/technology` and `/c/qa-pass46-test` both 404. S21: After manually following `qa-pass46-test` (Follow → Unfollow), `/communities` → Following tab **still shows only "technology"** (follow edge exists but community not listed). "All on this instance" tab shows `qa-pass46-test`. `/c/qa-pass46-test` still 404.
- **Result:** 0 new; **S19 re-confirmed OPEN** (no Accept/Decline, no Requests/Members tabs); **S21 re-confirmed OPEN** (no auto-follow, manual follow doesn't populate Following tab, /c/{handle} 404s).
- **Checkpoint:** next pass targets S21 (Following tab filter/caching), S19 (Requests/Members tabs + Accept/Decline), S3 (Create persistence), S20 (Home Communities tab), S2/S14 (blocked).

---

## Pass 46 (2026-09-20) — S3 re-verify + community creation flow on `65ccfa0`

- **Build/Live:** deployed `65ccfa0` (== HEAD? y).
- **Explored:** S3: Created post "QA Pass 46 S3 re-verify post" (Note IRI: `…/notes/06GBW8XSFP3WKCNJSTXEB8HWFW`). POST outbox → 202. DB confirms **zero Create activities for andrew** (count = 0). Note IRI → 200, clean render, 0 console errors. Community creation: Created "qa-pass46-test" via /communities → POST outbox 202 → Group in DB. **New community missing from Following tab** (only "technology" listed). **New community appears in "All on this instance" tab.** `/c/qa-pass46-test` → 404.
- **Result:** **1 NEW** (S21 — newly created community missing from Following tab + /c/{handle} 404s → [s21](s21-new-community-missing-following-tab.md)); **S3 re-confirmed OPEN** (Create activities still not persisted to DB).
- **Checkpoint:** next pass targets S21 (community creation follow edge + /c/{handle} route), S19 (community page 404), S3 (Create persistence), S20 (Home Communities tab), S2/S14 (blocked).

---

## Pass 45 (2026-09-20) — S16 re-verify on `65ccfa0`

- **Build/Live:** deployed `65ccfa0` (== HEAD? y).
- **Explored:** S16: Poll `06GBVZDNA8JPCCFC8WW2JGAFZR` — Profile page: A:0/B:0/**0 votes**, NO "You voted" badge. Object page: A:1/B:0/**1 votes**, NO "You voted" badge. **Regression:** vote count differs between profile (0) and object (1); badge missing from both (was visible on profile in Pass 42).
- **Result:** 0 new; **S16-UX re-confirmed OPEN** (regression: inconsistent vote counts + badge missing everywhere).
- **Checkpoint:** next pass targets S19 (community page 404), S3 (Create persistence), S20 (Home Communities tab), S2/S14 (blocked).

---

## Pass 44 (2026-09-20) — S17/S20/S4 re-verify on `65ccfa0`

- **Build/Live:** deployed `65ccfa0` (== HEAD? y).
- **Explored:** S17: `/profile` → "Your posts" fired 11 outbox requests (pages 1–11, all 200, no aborts). Replies tab fired 13-page fan-out (pages 1–13). Total 24 outbox requests for 2 tabs. S20: `/home` → Communities tab → 0 new API requests, same content as Posts tab (skinnylatte dolphin boost first in both). S4: Communities → Following shows only "technology" (local), remote "interop" still missing.
- **Result:** 0 new; **3 re-confirmed OPEN** (S17, S20, S4).
- **Checkpoint:** next pass targets S19 (community page 404 + missing tabs), S3 (Create persistence), S16-UX (poll badge), S2/S14 (blocked).

---

## Pass 43 (2026-09-20) — S3/S19 re-verify + community page routing on `65ccfa0`

- **Build/Live:** deployed `65ccfa0` (== HEAD? y).
- **Explored:** S3: Created post "QA Pass 43 S3 re-verify post" (Note IRI: `…/notes/06GBW5EY16SRJ99N0SKGB3PJQW`). POST outbox → 202. DB confirms **zero Create activities for andrew** — Create activities generated in-memory for outbox but **never persisted to Objects table**. S19: `/c/technology` → **"Not found"** (404). Community only reachable via `/actor?iri=…/c/technology` (generic actor page: Posts/Followers/Following tabs — **no Requests, no Members tab**). S19 scope changed. S8 re-confirmed fixed (management page tabs work).
- **Result:** 0 new; **S3 re-confirmed OPEN** (root cause: Create activities not persisted to DB); **S19 re-confirmed OPEN** (scope changed: /c/{name} 404s, actor page lacks Requests/Members tabs); **S8 re-confirmed FIXED**.
- **Checkpoint:** next pass targets S19 (community page 404 + missing tabs), S3 (Create persistence), S20 (Home Communities tab), S4, S17, S2/S14 (blocked).

---

## Pass 42 (2026-09-20) — S16/S3/S4/S19/S17 re-verify + Home feed Communities tab on `65ccfa0`

- **Build/Live:** deployed `65ccfa0` (== HEAD? y).
- **Explored:** S16: Poll `06GBVZDNA8JPCCFC8WW2JGAFZR` — Profile page shows "You voted" badge (A:1/B:0/1 votes). Object page shows A:1/B:0/1 votes but **no "You voted" badge** on fresh load. S3: Created post "QA Pass 42 S3 re-verify post" (Note IRI: `…/notes/06GBW1RWNE5TQ90WV5PNX4G6R8`). DB shows only Note object, **no Create activity** stored. Note IRI → 200, clean render. S4: Communities → Following shows only "technology" (local), remote "interop" still missing (interop community page shows "Leave" button, confirming follow exists). S19: Technology Requests tab → "We couldn't load the join requests. Please try again." dead-end, no request fires. S17: Profile "Your posts" fired 8 outbox requests (1–8, with 4 ERR_ABORTED); Replies tab fired 13-page fan-out (1–13). Total 21 requests for 2 tabs. Home feed: Communities tab fires 0 new API requests, shows same content as Posts tab (skinnylatte dolphin post first in both) — **Phase 4 tab wiring non-functional**.
- **Result:** **1 NEW** (S20 — Home feed Communities tab non-functional → [s20](s20-home-feed-communities-tab-nonfunctional.md)); **5 re-confirmed OPEN** (S16-UX badge on object page, S3, S4 remote, S17, S19). S3 scope note: Create activities may not be persisted to DB at all (only Note objects stored), making Create-IRI 404s a deeper issue than just collection derivation.
- **Checkpoint:** next pass targets S3 (investigate why Create activities aren't in DB), S19 (follow request acceptance UI), S20 (Home feed Communities tab), S2/S14 (blocked on dev).

---

## Pass 41 (2026-09-20) — S16/S3/S4/S19/S17/S7 re-verify on `59ff4ec`

- **Build/Live:** deployed `59ff4ec` (== HEAD? y).
- **Explored:** S16: Created poll `06GBVZDNA8JPCCFC8WW2JGAFZR` ("QA Pass 41 poll re-verify", A/B). Profile page shows "You voted" badge + A:1/B:0/1 votes. Object page (`/object?iri=...`) shows A:1/B:0/1 votes but **no "You voted" badge** on fresh load. S3: Create-IRI `/ap/v1/u/andrew/creates/06GBVQB28WTGQCAYWQM58KD4JC` → HTTP 404 (net::ERR_HTTP_RESPONSE_CODE_FAILURE). Note-IRI `/ap/v1/u/andrew/notes/06GBVQB2920X0D9R9611ANV1MG` → 200. S4: Communities → Following shows only "technology" (local), remote "interop" still missing. S19: Technology Requests tab → "We couldn't load the join requests. Please try again." alert, no request fires (only `/members` request). S17: Profile "Your posts" fired 6 outbox pages (1–6) + 2 duplicates; Replies tab fired 13-page fan-out (1–13) with 3 ERR_ABORTED. Total 19+ requests for 2 tabs. S7: Directory lookup `lemmyadmin@lemmy.luit.ink` → actor card appears, 0 console errors. Home feed: Posts tab fires 3 feed pages (1–3); Communities tab fires 0 new requests (content unchanged — tab switch not wired to different feed source).
- **Result:** 0 new; **5 re-confirmed OPEN** (S16-UX badge on object page, S3, S4 remote, S17, S19); **1 re-confirmed FIXED** (S7). Home feed Communities tab appears non-functional (same content as Posts tab, no new API call).
- **Checkpoint:** next pass targets S3 (Create-IRI 404), S19 (follow request acceptance UI), S2/S14 (blocked on dev). Home feed Communities tab may need new finding.

---

## Pass 40 (2026-09-20) — S17/S4/S16/S19/S7 authless re-verify on `4f5dd5c`

- **Build/Live:** deployed `4f5dd5c` (== HEAD? y).
- **Explored:** S17: profile → "Your posts" fired 6 outbox pages (1–6) on load; Replies tab fired full 13-page fan-out (19 total for 2 tabs). S4: Communities → Following shows only "technology" (local), remote "interop" still missing. S16: poll `06GBVCXGF7HRK17GJTH9N2ZXS0` → Option A: 2 / Option B: 0 / "2 votes", no "You voted" badge (badge re-hydration still broken). S19: technology Requests tab → "couldn't load" dead-end, no request fires, 0 non-environmental console errors (3× lemmy.ml 502s). S7: Directory lookup `lemmyadmin@lemmy.luit.ink` → actor card appears, 0 console errors. Authless `/` → 0 console errors, feed renders, "Log in"/"Register" links present; clicking "Open post" → 302 to login (correct gating).
- **Result:** 0 new; **5 re-confirmed OPEN** (S17, S4 remote, S16-UX badge, S19, S3); **1 confirmed FIXED** (S7 — Directory external lookup works).
- **Checkpoint:** next pass targets S3 (Create-IRI 404), S19 (follow request acceptance), S2/S14 (signed-out proxy 401 — blocked on dev).

---

## Pass 39 (2026-09-20) — S13/S3/S15/S18/S19 re-verify on `4f5dd5c`

- **Build/Live:** deployed `4f5dd5c` (includes S13 fix, S3 fix, S15 fix, S10 fix, S12a fix, S11 fix, S9 fix).
- **Explored:** S13: remote Lemmy post `lemmy.luit.ink/post/1` → **0 console errors** (was 3 in Pass 38). S3: created fresh post, navigated to Create-IRI `…/creates/06GBVQB28WTGQCAYWQM58KD4JC` → "Object not found" + 1 console 404 (still broken); Note IRI → 200 clean. S15: all 6 visibility/type combos now correct — Public "addressed to the public", Followers "visible to followers only", Direct "sent as a direct message" (grammar fixed). S18: registered `qa39test`, followed andrew → Unfollow button, Home timeline populated with posts; hard-refresh shows Unfollow (state persisted); andrew's Followers tab shows count 4 (not 5); andrew's notifications show "qa39test sent you a follow request" with NO Accept/Decline buttons (S19). S19: `/requests` route → 404 "Not found"; notifications page shows follow requests but no action buttons.
- **Result:** 0 new; **2 confirmed FIXED** (S13, S15); **1 re-confirmed OPEN** (S3 — Create-IRI still 404s); **1 partially fixed** (S18 — state persists now, but follow request not auto-approved); **1 re-confirmed OPEN** (S19 — no Accept/Decline UI, /requests 404).
- **Checkpoint:** next pass targets S3 (Create-IRI 404), S19 (follow request acceptance), S4 (remote communities), S17 (profile over-fetch), S16-UX (poll badge).

---

## Pass 38 (2026-09-20) — S9/S11/S12a/S10/S15/S16/S17/S3/S13/S19/S4/S18 re-verify on rebuilt container

- **Build/Live:** rebuilt container post-`bdc0e66` (S10) + `b6987d4` (S12a) + `d729c66` (S11) + `c1da416` (S9). Container `irisweb-iris-web-1` healthy.
- **Explored:** clean entry (andrew). S11a: body-less poll → posts successfully. S9: flagged bob → "Reported ✓" + disabled. S12a: `@Alice` mention → redirects to canonical `alice` page (200). S10: selector says "Article" (no "(long-form)"); type-aware hint. S15: hint now visibility-aware in all 6 cases (minor grammar: "A note a private message"). S16: poll votes persist (Option A: 2), no "You voted" badge on fresh load. S17: profile tabs — initial load 4 outbox pages (reduced from 13), but tab switch still fires full 13-page fan-out (29 total for 3 tabs). S3: Create-IRI `?iri=…/creates/{id}` → "Object not found" + 1 console 404; Note IRI → 200. S13: remote Lemmy post → 3 console 404s (`/replies|likes|shares`). S19: technology Requests tab → "couldn't load" dead-end, no request fires. S4: Communities → Following shows only "technology" (local), remote "interop" missing. S18: qa37test NOT in Followers (4) tab — follow state lost.
- **Result:** 0 new; **4 confirmed FIXED** (S9, S11a/b, S12a, S10); **1 mostly fixed** (S15 — hint now visibility-aware, minor grammar); **7 re-confirmed OPEN** (S3, S4 remote, S13, S16-UX badge, S17, S18, S19).
- **Checkpoint:** next pass targets S2/S14 (signed-out proxy 401 — blocked on dev), S18 (fresh account follow → Home empty), S17 (profile over-fetch fix).

---

## Pass 37 (2026-09-20) — S4/S8/S13/S15/S16 re-verify

- **Build/Live:** deployed `bb28dcf` (== HEAD? n — HEAD is `3a7f934` merge of QA docs; no code change since `bb28dcf`). Container `irisweb-iris-web-1` healthy.
- **Explored:** clean entry (andrew). S4: andrew follows both `technology` (local) and `lemmy.luit.ink/c/interop` (remote); Communities → Following tab shows **only "technology"** — remote `interop` is missing. S8: "All on this instance" tab lists all 6 local + 2 seeded remote communities (correct). S13: remote Lemmy post `lemmy.luit.ink/post/1` → 3 console 404s (`/replies|likes|shares`), post renders. S15: compose visibility hint — "A note addressed to the public…" for Public/Followers/Direct (all 3 wrong); Poll variant "A poll addressed to the public…" (also wrong). S16: fresh poll `06GBVCXGF7HRK17GJTH9N2ZXS0` — andrew voted Option A (count 0→1, DB confirms `voters=[andrew]`); new user `qa37test` voted Option B (first click 502, second click count 1→2, DB confirms `voters=[andrew, qa37test]`); hard-refresh reverts to "2" (votes persisted) but "You voted" badge does NOT re-hydrate.
- **Result:** 0 new; **4 re-confirmed OPEN** (S4 remote, S13, S15, S16-UX); **1 confirmed FIXED** (S8). S16 core data-integrity is fixed (votes persist server-side) but UX gaps remain (badge re-hydration, 502 on first vote from new user).
- **Checkpoint:** all 5 target findings from Pass 36 checkpoint now have Pass 37 evidence. Remaining open: S4 (remote), S9, S10, S11a/b, S12a, S13, S15, S16-UX, S17, S18, S19, S2/S14 (blocked on dev).

---

## Pass 36 (2026-09-20) — S11/S19/S18/S2 re-verify

- **Build/Live:** deployed `bb28dcf` (== HEAD? n — HEAD is `62a3897` merge of QA docs; no code change since `bb28dcf`).
- **Explored:** signed-out `/` (S2/S14: 11 console errors — 6× proxy 401, 2× CORS, 2× ERR_FAILED). S11: body-less poll → no request fired (S11a); poll with body → no request fired (S11b untestable, no object created). S19: technology Requests tab → "couldn't load" dead-end, no request fires. S18: registered `qa36test`, followed andrew → Unflip to Unfollow, qa36test in Followers(3), Home empty; hard-refresh shows "Follow" again (state not persisted).
- **Result:** 0 new; **4 re-confirmed OPEN** (S11a, S11b, S19, S18, S2). S18 has a new facet: follow state lost on hard-refresh.
- **Checkpoint:** next pass explores S4 (communities following remote), S8 (all-on-instance list), S13 (remote Lemmy 404 noise), S15 (visibility hint), S16 (poll votes).

---

## Pass 35 (2026-09-20) — S12/S9/S17/S3/S10 re-verify

- **Build/Live:** deployed `bb28dcf` (== HEAD? y). Container `irisweb-iris-web-1` healthy.
- **Explored:** clean entry (andrew). S12a: posted `@Alice` (no autocomplete) → dead link `…/u/Alice` 404. S12b: `@alice` autocomplete → local `alice` only, canonical IRI 200. S9: flagged bob (actor detail) → no toast/state change, duplicate accepted; Moderation list stuck "Loading…". S17: profile → 13-page outbox fan-out on load + full re-fire on Replies tab (26 requests). S3: Create-activity IRIs 404 via curl/browser; UI now routes through Note IRIs (200, clean). S10: Article still 500-char cap, no title field, plain-paragraph render.
- **Result:** 0 new; **5 re-confirmed OPEN** (S12a, S9, S17, S3, S10). S12b appears fixed (autocomplete shows local-only). S3 scope narrowed (UI uses Note IRIs; only direct `?iri=…/creates/{id}` 404s).
- **Checkpoint:** next pass explores S2/S14 (proxy 401 — blocked on dev), S11 (poll), S18 (follow→Home), S19 (community requests).

---

## Pass 34 (2026-09-20) — S18/S19/S11 re-verify

- **Build/Live:** deployed `456b0d9` (== HEAD? y). Container `irisweb-iris-web-1` healthy.
- **Explored:** clean entry (andrew). S18: registered `qa34test`, followed local `andrew`, checked Home. S19: community `technology` Requests tab. S11: poll compose (body-less + with-body).
- **Result:** 0 clean; **3 re-confirmed OPEN** (S18: follow flips to Unfollow but Home stays empty; S19: Requests tab shows "couldn't load" dead-end, no request fires; S11a: body-less poll silent no-op; S11b: poll with body 202s but invisible in profile). 0 non-environmental console errors.
- **Checkpoint:** next pass explores S12 (mention linkify/autocomplete), S9 (report/flag), S17 (profile tab over-fetch), S3 (object-detail Create IRI 404), S10 (Article mislabel).

---

## Pass 33 (2026-09-20) — Compose Article type + Settings Security/Password + Notifications + S7 re-verify

- **Build/Live:** deployed `456b0d9` (== HEAD? y). Container `irisweb-iris-web-1` healthy.
- **Explored:** clean entry (andrew). Compose "Article (long-form)" end-to-end, Settings → Security + Change password, Notifications tab filtering (All/Follows), Directory external lookup (S7 re-verify).
- **Result:** **3 clean** (Article compose → 202 → renders on profile + AP doc type=Article; Change password → success message + re-login works; Notifications Follows filter shows only follow requests); **1 fixed** (S7 → `456b0d9`, external lookup renders resolved actor card, 0 errors). 0 non-environmental console errors.
- **Checkpoint:** next pass explores S18 (local follow → empty Home timeline) re-verify, S19 (community Requests tab), S11 (Poll compose), and Settings → Danger tab.

---

## Pass 32 (2026-09-20) — S2/S14 re-verify + S5 fix confirmation + profile avatar flow

- **Build/Live:** deployed `456b0d9` (== HEAD? y — S2/S14 anonymous-proxy seam + S5 Search fix are live). Container `irisweb-iris-web-1` healthy.
- **Explored:** clean entry (andrew). S2/S14 signed-out re-verify (proxy 401s unsigned GET), S5 search "alice" re-verify (orphan gone), profile avatar upload/remove round-trip.
- **Result:** **1 clean** (S5 orphan gone, canonical alice renders, 0 errors on actor detail); **2 re-confirmed OPEN** (S2 signed-out `/` → 11 console errors, proxy 401s unsigned GET for Mastodon; S14 signed-out remote actor detail → proxy 401 → "Actor not found."); **1 fixed** (S5 → `456b0d9`). Profile avatar: upload works (media IRI persisted), remove works (icon cleared), but in-page avatar cache is stale after save (shows old media IRI until hard refresh). 0 non-environmental console errors.
- **Checkpoint:** next pass explores fresh areas (Compose "Article" type, Settings → Security/Change password, Directory "Find someone" re-verify for S7, Notifications tab filtering).

---

## Pass 31 (2026-09-20) — Search re-verify + Compose media attachment

- **Build/Live:** deployed `a45f3d4` (== HEAD? y — dev proxy rate-limiter WIP still uncommitted; S2/S14 re-verify remains blocked). Container `irisweb-iris-web-1` healthy.
- **Explored:** clean entry (andrew). S5 search "alice" re-verify + "Actors only" filter, Compose media-attachment end-to-end (file pick → preview → post → object page → Delete).
- **Result:** **2 clean** ("Actors only" search filter returns actor-only results; media-attachment post renders image on object page, DB `attachment` JSONB + media URL serve the exact 69-byte PNG, Delete→Tombstone works); **1 re-confirmed OPEN** (S5 orphan `localhost:8088/ap/v1/u/alice` still first result of "alice"). 0 non-environmental console errors.
- **Checkpoint:** next pass re-verifies S2/S14 once dev's anonymous-proxy rate-limiter WIP is committed+rebuilt; re-check S17 if `PagedCollection.razor` WIP lands.

---

## Pass 30 (2026-09-20) — Communities detail tabs + "All on this instance" + Settings moderation

- **Build/Live:** deployed `a45f3d4` (== HEAD? y — only docs + dev uncommitted WIP since, now incl. `PagedCollection.razor`; build current). Container `irisweb-iris-web-1` healthy.
- **Explored:** clean entry (andrew). Community detail (Feed/Members/Owners/Peers/Requests), Communities "All on this instance" tab, Settings → Account → Moderation (Blocked/Muted/Reported).
- **Result:** **4 clean** (community Feed/Members/Owners/Peers tabs, "All on this instance" lists all local communities [S8 still fixed], Settings moderation lists load with correct empty states); **1 NEW** (S19 community "Requests" tab always shows "couldn't load the join requests" — no request fires, no console error, no retry); 0 non-environmental console errors (only remote `lemmy.ml` proxy 502s).
- **Checkpoint:** next pass re-verifies S2/S14 once dev's anonymous-proxy rate-limiter WIP is committed+rebuilt, then fresh areas (Search page, "Edit profile" flow, media attachments, Communities "Create a community" re-verify).

---

## Pass 29 (2026-09-20) — Profile tabs + actor page + object-detail + Settings + multi-account follow

- **Build/Live:** deployed `a45f3d4` (== HEAD? y — only docs + dev uncommitted proxy WIP since; build current). Container `irisweb-iris-web-1` healthy.
- **Explored:** clean entry (andrew) then fresh local account `qa29test`. Profile tabs (Your posts/Replies/Likes), public `/actor` page, object-detail (note IRI + Delete owner flow), Settings (Account/Content/Danger + account-deletion confirm), register + follow (multi-account).
- **Result:** **3 clean** (object-detail note IRI + Delete→Tombstone, Settings tabs + read-only security + 2-step account-delete, register flow); **2 NEW** (S17 profile tabs over-fetch the entire outbox on load + every tab switch — ~40 redundant requests; S18 local follow "succeeds" but follower's Home timeline stays empty — followers collection + inbox never updated); 0 console errors.
- **Checkpoint:** next pass re-verifies S2/S14 once dev's anonymous-proxy rate-limiter WIP is committed+rebuilt, then fresh areas (multi-account moderation, Communities detail, Communities "All known").

---

## Pass 28 (2026-09-20) — S15 + S12b + Notifications + CW + S7

- **Build/Live:** deployed `a45f3d4` (== HEAD? y — only docs + dev uncommitted proxy WIP since; build current). Container `irisweb-iris-web-1` healthy.
- **Explored:** clean entry (andrew). S15 (all 6 visibility-hint cases), S12b (autocomplete candidate pool), Notifications (tabs + mark-all-read), content-warning post→render→Show/Hide, S7 (directory external lookup).
- **Result:** **5 clean** (Notifications, CW end-to-end, /admin gating, Notifications mark-all-read, no request spam); **2 re-confirmed OPEN** (S15 all-6-cases, S7 spinner-stuck); S12b candidate pool confirmed remote-contaminated (orphan localhost `alice` first + many remote actors) — stays open. No new defects.
- **Checkpoint:** next pass continues at S2/S14 re-verify (dev's anonymous-proxy rate-limiter WIP is uncommitted — needs rebuild first), then fresh areas (Profile tabs, Settings, multi-account moderation).

---

## Pass 27 (2026-09-20) — full re-verification of all open findings post-rebuild

- **Build/Live:** container `irisweb-iris-web-1` rebuilt 2026-09-20 ~03:31 UTC (image `018ecaff`); deployed HEAD `a45f3d4` (== HEAD? y). **Note:** the deployed-commit label in PLAN.md was stale (it still said `c4511c0`); the rebuild actually contains `a45f3d4`.
- **Explored:** clean-entry (andrew + signed-out) re-verification of every open finding (S2–S16), incl. DB cross-checks.
- **Result:** **3 FIXED** (S4 local-community following, S6 remote join single-button, S8 "All on this instance" now lists all 6 local communities), **1 re-confirmed-fixed** (S16 poll votes persist: DB `poll.voters` + `votesCount=1` survive rebuild), **9 re-confirmed OPEN** (S2, S3, S5, S7, S9, S10, S11a+b, S12a, S13, S14), S15 not re-exercised (stays open). No new defects.
- **Checkpoint:** next pass continues at S15 (compose visibility hint) + S12b (autocomplete), then fresh exploratory areas.

---

## Legacy passes (10–25) — verbose, archived as-is

The entries below predate the brief format (they were extracted verbatim from PLAN.md on 2026-09-20). They are kept for history but are **not** the model to follow — new passes use the brief format above.


**Pass 25 (2026-09-20, QA, exploratory — poll rendering + voting):** no new commit since `c4511c0` (Phase 5); build green (0/0); **no rebuild** (container `irisweb-iris-web-1` still Up 2h), so **S2–S15 remain open** (not re-exercised in depth). Signed in as QAUser1. **Explored poll (Question) rendering + voting — found ONE new defect (S16), re-confirmed S11a + S11b:** **(A) NEW defect S16 (S3-sev, data-integrity): poll votes are NOT persisted.** On a poll's object-detail page, clicking an option (Option A) updated the UI in place — count 0→1, a **"You voted"** badge, "1 votes" — with **0 errors**; but a **page refresh reverted the count to 0** and the "You voted" badge disappeared. DB confirms no vote was recorded server-side: the poll `Object` has **no `votes` property** (only the Create activity; **no `Vote` activity/object, no Edge** to the poll). So the vote is a **client-only in-memory Blazor state change** that is silently discarded — a user's poll vote is lost on refresh. Fix: persist the vote (deliver a `Vote`/`Add` activity to the poll and reflect it in the poll's stored `votes`/state, or at minimum store the user's choice server-side so the UI can re-hydrate it). **(B) S11a RE-CONFIRMED (silent no-op):** posting a poll that uses the **Question** field **without** the main Content body (question + 2 options) → **no "Posted" confirmation, 0 errors, and NO object created** (DB: no new `Question` object) — `PostAsync`'s empty-`Content` guard (`Compose.razor:1330`) silently discards it. **(C) S11b RE-CONFIRMED (poll invisible in "Your posts"):** a poll posted **with** a body (202, object stored at `…/objects/06GBSGQTYCMVSXEYC9XCMK6MPR`, `type=["Question","Object"]`) is **NOT listed in the profile "Your posts"** — `OutboxFilter.IsContentItem` (`OutboxFilter.cs:42`) accepts only `Note||Article`. **(D) Poll rendering (object-detail) — WORKS (0 errors):** the poll's object-detail page renders correctly — question, both options with counts, "0 votes", "Ends 09/21/2026 03:01" — so the poll *renders* fine when reached directly; the S11b gap is purely the "Your posts" visibility. [change doc](../changes/997-ui-ux-review.md)

**Pass 24 (2026-09-20, QA, exploratory — Followers visibility delivery):** the other agent **committed `c4511c0`** (Inbox ② Phase 5 — "single Join/Leave button on community detail") and **fixed the Pass-23 build break** (updated the stale `AddMemberAsync`/`RemoveMemberAsync` `cref`s) — `dotnet build apps/Iris.Web/Iris.Web.csproj -c Release` is **green (0/0)** again. **But the live container `irisweb-iris-web-1` is still Up 2h — NO rebuild**, so live behavior is unchanged and **S2–S15 remain open** (not re-exercised in depth; will re-verify S4/S6/S8 + the S6 fix + Phase 4/5 Join/Leave after a rebuild). The S6 fix + Phase 4/5 (Join/Leave drive Follow/Undo; single Join/Leave button) are **committed but not yet live**. Signed in as QAUser1. **Explored Followers-visibility delivery — WORKS (correct, 0 errors):** **(A) Followers visibility delivery — WORKS (correct):** posting a note with visibility **Followers** → **202**, 0 errors; the stored note has **`to`=followers, `cc`=followers** (NOT `Public`) — i.e. addressed to the actor's followers, not the public; it **appears in the author's "Your posts"** (correct — the author sees their own followers-only notes). Together with Pass 22 (Direct = to/cc followers, not in Home), **all three visibility levels (Public/Followers/Direct) now have verified-correct delivery audiences.** **(B) S15 re-confirmed for Followers:** the compose hint still renders "A note addressed to the public…" for Followers (the same cosmetic defect found in Pass 22 — `Compose.razor:175-183` ignores the visibility setting). No new defect. [change doc](../changes/997-ui-ux-review.md)

**Pass 23 (2026-09-20, QA, exploratory — admin dashboard + follow-request area + build-state check):** the other agent **committed `68ae703`** (Inbox ② Phase 4 — "Join/Leave drive Follow/Undo") on top of `abc1b33`/`cb9330f`, **but the live container `irisweb-iris-web-1` is still Up 2h — NO rebuild**, so live behavior is unchanged and **S2–S15 remain open** (not re-exercised in depth; will re-verify S4/S6/S8 + the S6 fix after a rebuild). The S6 fix (remote Join now drives a server-side Follow/Undo instead of a direct browser POST) is **committed but not yet live**. **⚠️ BUILD STATE — HEAD is RED (committed build break, transient in-flight work):** `dotnet build apps/Iris.Web/Iris.Web.csproj -c Release` now returns **2 errors (CS1574)** — unresolvable XML `cref` attributes in **committed** files `src/Iris.Server/Inbox/AddActivityHandler.cs:43` (`AddMemberAsync`) and `RemoveActivityHandler.cs:43` (`RemoveMemberAsync`). Root cause: the Phase 4 refactor (`cb9330f`/`abc1b33` "unify community members with followers") renamed `ICommunityStore.AddMemberAsync`→`AddFollowerAsync` / `RemoveMemberAsync`→`RemoveFollowerAsync` (the members→followers unification), but these two XML-doc `cref`s still reference the old (now-removed) names. The files are unmodified in the working tree, so the break is in HEAD, not WIP — a transient state the other agent is mid-refactor on (their working tree also has a **staged deletion of `JoinButton.razor`** + modified `CommunityCard.razor`/`FollowButton.razor`/`CommunityDetail.razor` — the Join/Leave→Follow/Undo client consolidation). **Left untouched per the "wait before confirming" rule** (a rebuild of the broken HEAD would fail; the live container still runs the last good build). *QA note for the dev agent: fix = update the two `cref`s to `AddFollowerAsync`/`RemoveFollowerAsync` (or drop them); build is otherwise green.* **Explore (on the running last-good build, signed in as QAUser1):** **(A) Admin dashboard — correctly gated (WORKS, 0 errors):** `/admin` for the non-admin QAUser1 shows a clean alert — "This page is only available to instance administrators…" — no leak of admin content, 0 console errors. (Could not test the dashboard itself: QAUser1 is not an instance admin and no admin test credentials are available.) **(B) Follow-request accept/reject — NOT TESTED live (no trigger available):** QAUser1 has `manuallyApprovesFollowers` **absent** (auto-approve) and **0 pending follow requests** (1 incoming follow already auto-accepted from remote lemmyadmin; 3 outgoing follows). There is no local second test account to send QAUser1 a follow request to exercise the accept/reject UI, so this flow is **deferred** (not a defect — no test trigger available). [change doc](../changes/997-ui-ux-review.md)

**Pass 22 (2026-09-20, QA, exploratory — Direct/Followers visibility delivery):** the other agent **committed `abc1b33`** (Inbox ② Phase 3 — "gate community follows on `manuallyApprovesMembers`") and the working tree **builds green (0/0)**, **but the live container `irisweb-iris-web-1` is still Up 2h — NO rebuild**, so live behavior is unchanged and **S2–S14 remain open** (not re-exercised in depth; will re-verify S4/S6/S8 after a rebuild). Note: the other agent still has the **S6 fix uncommitted** (`JoinButton.razor` + `ActivityPubClient.cs` + `IActivityPubClient.cs` — the remote-Join server-side routing). Signed in as QAUser1. **Explored Direct/Followers visibility delivery — WORKS (clean, 0 errors), found ONE minor cosmetic defect (S15):** **(A) Direct visibility delivery — WORKS (correct, private):** posting a note with visibility **Direct** → **202**, 0 errors; the stored note has **`to`=followers, `cc`=followers** (NOT `Public`) — i.e. it is addressed to the actor's followers/mentions only, not the public; it **appears in the author's "Your posts"** (correct — the author can see their own direct notes) but does **NOT appear in the Home timeline** (verified: no "Direct visibility delivery test" post on `/home`) — so the privacy model is correct (no public leak). **(B) NEW minor defect S15 (class=UX/cosmetic, S3 sev):** the compose **visibility hint** is **misleading for Followers/Direct**. The hint (`Compose.razor:175-183`) is a ternary that **ignores the visibility setting** — for Followers **and** Direct it still renders **"A note addressed to the public — it lands in your outbox and appears in your followers' timelines."** (and the Poll variant, line 181, is also hard-coded to "addressed to the public"). For **Direct** (a private message) this is actively misleading — the hint says "addressed to the public" but the post is **not** public (verified in (A)); the delivery is correct, only the hint text is wrong. Fix: make the hint reflect the selected visibility (Public → "appears in followers' timelines", Followers → "visible to your followers only", Direct → "a private message — not public, not in your timeline"), and drop the hard-coded "addressed to the public" from the Poll variant. [change doc](../changes/997-ui-ux-review.md)

**Pass 21 (2026-09-20, QA, exploratory — reply-to-reply threading depth):** no rebuild since Pass 20 (container `irisweb-iris-web-1` still Up 2h, no new commit since `cb9330f`), so **S2–S14 remain open** (not re-exercised in depth). Build green (`Iris.Web.csproj` 0/0); health still **degraded** (`InstanceObservabilityHealthCheck` — observability only). Signed in as QAUser1. **Explored reply-to-reply (depth-2) threading — WORKS (clean, 0 errors):** **(A) Reply-to-reply threading — WORKS:** on a local note that already had one reply (the Pass-16 reply), clicking the **reply's** "Reply" opened the reply composer with the correct context — **"Replying to" QAUser1** + a blockquote of the reply being answered + **"Thread started by" QAUser1** + the hint "This reply is threaded under the parent note and visible in its replies". Posting the depth-2 reply → **202**, 0 errors; the new reply's `inReplyTo` correctly points to the **depth-1 reply** (not the parent), and the depth-1 reply's object detail now shows **"Replies (1)"** containing the depth-2 reply, each with the correct "In reply to" breadcrumb (depth-2 → depth-1 → parent). Threading/nesting is correct at depth 2. **(B) Re-confirmed S13 + a related data point:** on the remote Lemmy post `lemmy.luit.ink/post/2`, the header shows **"2 comments"** (from Lemmy's `/api/v3/post` payload) but the **Replies tab says "No replies yet"** — the comments **count** (Lemmy-side) and the **replies list** (Iris-side, empty because Lemmy exposes no AS `/replies` collection — the S13 404s) are **inconsistent**, reinforcing S13's fix (derive the comment count and reply list from the same Lemmy source, or hide the count when the replies can't be loaded). No new defect. [change doc](../changes/997-ui-ux-review.md)

**Pass 20 (2026-09-20, QA, exploratory — signed-out / anonymous routes):** no rebuild since Pass 19 (container `irisweb-iris-web-1` still Up 2h, no new commit since `cb9330f`), so **S2–S13 remain open** (not re-exercised in depth). Build green (`Iris.Web.csproj` 0/0); health still **degraded** (`InstanceObservabilityHealthCheck` — observability only). **Explored the signed-out (anonymous) experience, re-confirmed S2 and found ONE new defect (S14):** **(A) S2 RE-CONFIRMED OPEN:** signed-out `/` → **6 console errors** (3× CORS + 3× `ERR_FAILED`) for numeric-ID Mastodon/hachyderm actors (`mastodon.social/ap/users/117300166312075407`, `…/117294272768263207`, `hachyderm.io/ap/users/117265166278633915`) — the browser fetches them **directly** (bypassing the proxy) → CORS-blocked, same as prior passes. **(B) Signed-out local actor detail — WORKS (clean, 0 errors):** `/actor?iri=…/iris.luit.ink/ap/v1/u/alice` renders the local actor fine (local actors are same-origin, no proxy/CSP issue). **(C) NEW defect S14 (class=bug, S2 sev — the S2 *actor-detail* facet):** signed-out **remote actor detail** (`/actor?iri=…/lemmy.luit.ink/u/lemmyadmin`) does a **direct browser fetch** to `https://lemmy.luit.ink/u/lemmyadmin` → **blocked by CSP `connect-src 'self'`** (4 console errors: 2× CSP-violation + 2× "Refused to connect"), and the page shows **"Failed to load actor. It may not exist or the server is unreachable."** — i.e. a remote actor's profile **cannot be viewed at all when signed out**, even though the actor is resolvable (it renders fine when signed in, which routes through the proxy). This is the **remote-actor-detail** facet of S2: the S2 root cause (`UiContext.FetchActorAsync:489` skips the proxy when signed out) breaks the *root page's* numeric-ID actor reads, but the *actor-detail page* has its own signed-out code path that **also** fetches the remote actor IRI directly → CSP-blocked for **any** remote actor (Lemmy/Mastodon), not just numeric IDs. Fix: route the signed-out remote actor-detail read through the same anonymous proxy GET path as the signed-out root page (the public/anonymous proxy path is the intended S2 fix — it covers this page too). **(D) Signed-out Directory & Community detail — redirect to /login (intentional, not a defect):** `/directory` and `/community?iri=…` both **redirect to the login page** when signed out (the login page renders clean, 0 errors) — so anonymous users cannot browse the directory or view (remote) community detail; that's an auth-gate choice, not a bug, but worth noting the remote-community-detail is therefore only reachable when signed in. [change doc](../changes/997-ui-ux-review.md)

**Pass 19 (2026-09-20, QA, exploratory — remote Lemmy object-detail + voting):** the other agent **committed `cb9330f`** (Inbox ② Phase 1 — "unify community members with followers", the S4/S6 community-simplification slice) and the working tree now **builds green (0/0)** (the Pass-18 `FollowActivityHandler.cs` error is resolved), **but the live container `irisweb-iris-web-1` is still Up 2h — NO rebuild**, so live behavior is unchanged and **S2–S12 remain open** (not re-exercised in depth; will re-verify S4/S6 after a rebuild). Health still **degraded** (`InstanceObservabilityHealthCheck` — same federation-observability signal, not a UI defect). Signed in as QAUser1. **Explored the remote-Lemmy object-detail + voting, found ONE new defect (S13):** **(A) Signed-in Home timeline — WORKS (clean, 0 errors):** mixed local + remote (Lemmy) content renders with correct boost attribution ("Boosted by interop"), author links, and per-post actions. **(B) Remote Lemmy post Upvote/Downvote — WORKS (clean, no new errors):** on `lemmy.luit.ink/post/1`, **Upvote** toggles (1→2, Score 2, `[pressed]`), **Downvote** toggles and **correctly clears the upvote** (upvote 2→1, downvote 0→1, Score 0) — the upvote/downvote states are mutually exclusive with correct score math; restored the post to its original state (upvote 1, Score 1). **(C) Remote Lemmy object-detail — NEW defect S13 (class=UX/bug, S3 sev, console-noise):** opening a **remote Lemmy post** (`/object?iri=https://lemmy.luit.ink/post/1`) logs **3 console 404 errors** — `POST /ap/v1/proxy/https://lemmy.luit.ink/post/1/{replies|likes|shares}` all 404. Root cause: the object-detail page (Replies/Likes/Shares tabs) requests those **ActivityStreams collection paths** from the remote object, but **Lemmy does not expose them** (Lemmy serves post data via `/api/v3/post?id=1`, not AS collection endpoints) — confirmed by hitting Lemmy directly: `/post/1` → 200, `/post/1/replies|likes|shares` → 404; the proxy faithfully relays Lemmy's 404s (the post doc itself loads via the proxy → 200, which is why the post renders). The UI **degrades gracefully** (Replies tab shows "No replies yet", Likes/Shares tabs render) but does **not** suppress these expected 404s, so every remote-Lemmy post detail logs 3 errors. Distinct from **S3** (which is a *local* post's Create-IRI `/replies` 404); this is the *remote-Lemmy* variant. Fix: when the object is a remote Lemmy post, either (a) don't request `/replies`/`/likes`/`/shares` (Lemmy has no AS collections — derive comment/like counts from the `/api/v3/post` payload or the `Lemmy`-specific comment endpoint), or (b) treat 404s on these optional collection fetches as "empty" (no console error / no failed network log). [change doc](../changes/997-ui-ux-review.md)

**Pass 18 (2026-09-20, QA, exploratory — mention/hashtag linkify + media):** no rebuild since Pass 17 (container `irisweb-iris-web-1` unchanged, no new commit), so **S2–S11 remain open** (not re-exercised in depth this pass). Build green (`Iris.Web.csproj` 0/0); health still **degraded** (`InstanceObservabilityHealthCheck` — same federation-observability signal as Pass 17, not a UI defect). Signed in as QAUser1. **Explored mention/hashtag linkify + media upload, found ONE new defect (S12):** **(A) Hashtag linkify — WORKS (clean, 0 errors):** a note with `#qapass18` posts (202); the hashtag is stored as a proper `Hashtag` tag (`href=https://iris.luit.ink/search?q=%23qapass18`, `name=#qapass18`), renders as a **link** to hashtag search on the profile, and clicking it → Search returns **1 result** (the note), 0 errors. **(B) Media attachments — NOT TESTED (environment limitation, not a defect):** the compose attachment `InputFile` (`#compose-attachment`) needs a file on the **browser** host, but the MCP Playwright browser runs in a separate filesystem from this session (`setInputFiles` → ENOENT on both `/tmp` and `/workspace` paths). Could not upload an image to verify the media-render flow; flag for a later pass with a browser-accessible file path. **(C) @mention linkify — NEW defect S12 (class=bug, S2 sev; two parts):** **S12a (case-sensitive same-instance mention → dead link):** `DetectMentionsWithDisplayAsync` (`Compose.razor:477,495`) matches `@([a-zA-Z0-9_]+)` and resolves a same-instance mention **verbatim** to `{base}/ap/v1/u/{handle}` (no case normalization); the actor lookup is a **case-sensitive** exact match (`EfActorStore.TryGetActorAsync:52` `e.Id == actorIri.Value`; `BuildActorIri:6575` builds the IRI verbatim). The local test actor is `alice`/`alice` (lowercase), so typing `@Alice` produces a mention tag + link to `https://iris.luit.ink/ap/v1/u/Alice`, which **404s** (confirmed live: console `Failed to load resource … 404 @ …/ap/v1/u/Alice`; `/actor?iri=…/u/Alice` renders an empty error page). The post itself is fine (hashtag + body render) but the **mention link is dead** and the mentioned user is not actually addressed. Fix: normalize same-instance mention handles to the canonical stored case (look up the local actor by preferredUsername case-insensitively, use its canonical IRI) or make the actor-doc route/lookup case-insensitive. **S12b (mention autocomplete mismatch):** the compose `@` autocomplete (`GetMentionCandidatesAsync:813`) surfaced a **remote** actor — "Alice McFlurry :bc:" (a `bc` instance) — for the same-instance query `@alice`, and accepting it left the raw `@alice` in the text (the post then resolved to the **local** `/u/Alice`), so the suggested candidate and the resolved target disagree. Fix: scope same-instance (`@handle`, no domain) autocomplete to **local** actors only (or clearly label remote candidates and resolve to the candidate's IRI when accepted). **Minor interaction quirk (no S-number):** on a post card the card's "Open post" overlay link intercepts clicks on inline mention/hashtag links, so inline mention links inside a card are hard to click (must open the object page first). [change doc](../changes/997-ui-ux-review.md)

**Pass 17 (2026-09-20, QA, exploratory — post types + CW):** no rebuild since Pass 16 (container `irisweb-iris-web-1` still Up ~1h, predates the other agent's community WIP; no new commit since `d61462f`), so **S2–S9 remain open** (not re-exercised in depth this pass). Build green (`Iris.Web.csproj` 0/0). Signed in as QAUser1. **Health note (not a UI defect):** `/ap/v1/health` is now **degraded** — `InstanceObservabilityHealthCheck`: **5 outbound deliveries dead-lettered** and only **22/3,719** stored actors have a resolvable signing key (a federation observability signal, not a user-facing failure; all UI routes still render clean). **Explored post types + content-warning, found TWO new defects:** **(A) Content warning — WORKS (clean, 0 errors):** the CW checkbox reveals a "Warning summary" field; a CW note posts (202), renders behind a **"Show"** button with the summary + "This content may be sensitive.", the body is hidden, and **Show↔Hide** toggles the reveal correctly. **(B) Article (long-form) — NEW defect S10 (class=UX/feature-gap, S2 sev):** selecting **"Article (long-form)"** posts a valid `Article` object (stored `ObjectType=Article`, IRI `…/articles/{id}`, detail page renders clean, 0 errors) — but it is **mislabeled "long-form"**: there is **no title field**, the body is **still capped at 500 chars** (the `@Content.Length/@MaxChars` counter at `Compose.razor:97-98` is shared with Note), and it **renders identically to a Note** (plain paragraph, no long-form/typography treatment). `PostArticleAsync` (`Compose.razor:1718-1794`) just wraps `Content.Trim()` into `Article.Content`. Fix: either implement real long-form (title field + larger limit + distinct render) or drop the "(long-form)" label. **(C) Poll — NEW defect S11 (class=bug, S2 sev; two parts):** **S11a (silent no-op):** `PostAsync` (`Compose.razor:1330`) **silently returns when `Content` is empty**, but a poll's question lives in the dedicated **Question** field (`PostPollAsync` uses `PollQuestion`, `Compose.razor:1820`), not the main body — so a valid poll (question + 2 options + duration, no main-body text) **does not post**: clicking Post fires **no request, no error, no state change** (a silent no-op). Confirmed live: the poll did nothing until the main **Content** box was also filled, then it posted (202, IRI `…/creates/06GBS3R7S8S0FXW6PVSNEF2XC4`). Fix: exempt Poll from the empty-`Content` guard (validate `PollQuestion`/options instead) and/or surface an error when Post is clicked with an invalid/empty poll. **S11b (poll invisible in posts listings):** a successfully posted poll is stored (`ObjectType=Question`, IRI `…/objects/{id}`) and **renders correctly on its own object page** (question, options, "Ends …", "0 votes", 0 errors) — but it **does NOT appear on the Profile "Your posts" tab** nor another actor's "Posts" tab, because `OutboxFilter.IsContentItem` (`OutboxFilter.cs:42`) only accepts `obj is Note || obj is Article` and **excludes `Question`**. Fix: include `Question` in the content-item check (so polls show in posts listings) — note the same filter feeds the Home/ActorDetail/Profile "posts" views. [change doc](../changes/997-ui-ux-review.md)

**Pass 16 (2026-09-20, QA, exploratory — moderation + reply):** no rebuild since Pass 15 (container `irisweb-iris-web-1` still Up ~1h, predates the other agent's now-40+-file community-membership WIP; no new commit since `d61462f`), so **S2–S8 remain open** (not re-exercised in depth this pass). Build green (`Iris.Web.csproj` 0/0); `/ap/v1/health` healthy. Signed in as QAUser1. **Explored two under-tested areas, all clean (0 console errors) except one new defect:** **(A) Actor-detail moderation** on a remote actor (`/actor?iri=…/lemmyadmin`): **Block/Unblock works** (button toggles Block→Unblock, Mute/Report hide while blocked, round-trips back; the flag is delivered via the actor's own outbox), **Mute/Unmute works** (button toggles Mute→Unmute), and **Settings → Moderation** correctly reflects state (Blocked/Muted/Reported lists, "Remove" works — cleaned up all test state). **NEW defect (S9, class=UX/bug, S2 sev):** **Report (flag) is a silent no-op with no feedback and is re-clickable (duplicate flags).** Clicking **Report** on the actor detail (or a post card) fires the flag correctly — `client.FlagAsync` → `POST /ap/v1/u/QAUser1/outbox` → **202**, and it **is** recorded (shows up under **Settings → Moderation → "Reported"** with the actor IRI) — but there is **no toast, no in-context state change, no reason prompt, and the "Report" button never changes/disables**, so the user gets zero confirmation the report landed and can click it again to file **duplicate** flags. Contrast with Block/Mute, which visibly toggle. Root cause: `FlagAsync` (`ModerationActions.razor:197-217`) only sets a private `_flagIri` (never rendered, no `StateHasChanged`/toast) and `CardReportAsync` (`ObjectView.razor.cs:1228-1249`) swallows the result with an empty `catch` and no feedback; neither prompts for a reason nor disables the button after a successful flag. Fix: surface a confirmation (toast or "Reported ✓" state) after a successful flag, optionally prompt for a short reason, and disable/de-duplicate the button once reported (or route it through the same visible-state pattern as Block/Mute). **(B) Reply/threading:** the reply composer (`/compose?replyTo=…`) renders the parent context correctly (blockquote + "This reply is threaded under the parent note"); posting a test reply → **"Posted (HTTP 202)"** (`…/creates/06GBS190RF3XZT4FW2AGGMSBT0`), and the new reply **appears threaded under the parent note's "Replies" tab** with an "In reply to" context card — works end-to-end, 0 errors. **(C) Like/Boost on a remote post:** the remote community feed (`/community?iri=…/lemmy.luit.ink/c/interop`) renders clean (0 errors); clicking **Like** on a post visibly toggles (`[pressed]`, count 1→2, delivered `POST …/outbox` 202) and unlike round-trips (count back to 1) — so Like/Boost have proper visual feedback, confirming the silent-feedback gap (S9) is **specific to Report/flag**, not engagement in general. [change doc](../changes/997-ui-ux-review.md)

**Pass 15 (2026-09-20, QA):** no new code committed since `d61462f`; the other agent has **uncommitted WIP in the community-membership area** (`ICommunityStore` + `EfCommunityStore`/`InMemoryCommunityStore`/`FileBackedCommunityStore`, `MembershipActivityHandler`, + `EntityFrameworkPersistenceExtensions`) — i.e. the **community-simplification / S4/S6** slice — but the live container (`irisweb-iris-web-1`, Up 43 min) **predates it**, so live behavior is unchanged. Re-ran `dotnet build Iris.Web.csproj -c Release` = **0/0** (green); `/ap/v1/health` healthy. **Signed in as QAUser1.** **Re-verified all 7 open defects (S2–S7) STILL OPEN (not yet fixed):** **S2** signed-out `/` → 6 console errors (3× CORS + 3× ERR_FAILED) for numeric-ID Mastodon actors; **S3** object-detail via a **Create-activity** IRI (`/object?iri=…/creates/…`) → 404 on `/replies` (1 console error; the Note IRI renders clean); **S4** Communities→Following "No communities followed yet" (0 errors); **S5** Search "alice" still lists the stale `http://localhost:8088/…/alice` orphan; **S6** remote Join → direct browser POST to `lemmy.luit.ink/c/interop/inbox` → CSP-blocked (2 errors), button stays "Join"; **S7** Directory external lookup still stuck on the spinner (proxy 200s, no result, no error, 0 errors). **NEW exploratory (both PASS clean, 0 console errors):** **Create-a-community** (filled Name/Handle/Description → submit → form collapsed, no error; verified the community **was created** — `GET /ap/v1/c/qa-pass15` = **200**, `attributedTo`=QAUser1, and it appears in the local Group search) and **Profile tabs** (Your posts / Replies / Likes / Followers / Following all render; **Following correctly lists the remote "Iris Interop"** with an Unfollow button — confirming S4 is *specifically* the Communities page's gap, since the Profile page resolves the followed remote community fine). **One NEW defect (S8, class=bug/data, S2 sev):** the **Communities "All on this instance" tab is incomplete/inconsistent** — it shows only **6** community cards (interop, owner-test-5428, piefed-test, technology, test-882, test-community-541) but the local-Group search endpoint (`/ap/v1/search?local=true&type=Group`) returns **11** (6 Iris + 5 remote). The UI **omits the local `qa-pass15`** (just created, API 200 + in search) **and the local `interop`** community, while *including* the **remote** `lemmy.luit.ink/c/interop` — so the "local-only" list both drops real local communities and mixes in a remote one. `Communities.razor:195-222` builds the list from `SearchAsync(baseIri, "", {Type="Actor", LocalOnly:true})` filtered to `Group` and sorted by handle — same endpoint the API query hits, so the omission is a real filter/limit/IRI-normalization gap, not a page-limit (a missing local item can't be pushed past a limit that still fits a remote one). Stable across reload (re-verified on a fresh page load). Repro: create a new community (or note `qa-pass15`/local `interop`) → Communities → "All on this instance" → the new/local communities are absent. Fix: make the local-Group list a complete, correctly-scoped local query (dedupe by normalized IRI, don't drop local Groups, exclude remote Groups) and re-verify the count matches the local-Group store. [change doc](../changes/997-ui-ux-review.md)

**Pass 14 (2026-09-20, QA, exploratory):** no new code committed since `d61462f`; the other agent has **uncommitted WIP** in `app.css` (narrows the `--card-hue` header strip from the full header row to just the avatar+handle group) + untracked plan docs — left untouched. Re-ran `dotnet build Iris.Web.csproj -c Release` = **0/0** (green); app healthy (`irisweb-iris-web-1` Up, `/ap/v1/health` healthy). **Clean entry → signed in as QAUser1.** Explored previously light areas, all **PASSES clean (0 console errors):** **Directory** (People local + "All known" remote actors — 02kagami02, belltrigger, CoiledDragon, etc. — and Communities, both tabs/scope toggles; note S5's stale `localhost alice` does **not** surface here — it's specific to the Search path), **remote actor detail via proxy** (`/actor?iri=https://lemmy.luit.ink/u/lemmyadmin` renders clean: banner, Block button, "No posts yet"), and **Settings → Profile save round-trip** (changed display name → "QA User 1 (Pass 14)" + bio → Save; after reload the profile shows both persisted — the in-page render lags one beat post-save but the save **does** land). **Re-verified all open defects STILL OPEN (not yet fixed):** **S4** — Communities→Following shows "No communities followed yet" for QAUser1 (follows only the remote `lemmy.luit.ink/c/interop`; 0 console errors), "All on this instance" lists every local community fine → specifically the remote-follow gap; **S5** — Search "alice" (25 results) still lists the stale orphan `http://localhost:8088/ap/v1/u/alice` alongside the real `https://iris.luit.ink/…/alice`; clicking the stale card → proxy **502** (×2) → "Actor not found." (2 console errors); **S6** — on the remote community detail (which shows **both** "Unfollow" *and* "Join"), clicking **Join** → **direct browser `POST` to `https://lemmy.luit.ink/c/interop/inbox`** → **CSP-blocked** (`connect-src 'self'`, 2 console errors) → button **stays "Join"** (no "Leave", no error — `JoinButton.razor:119-122` empty catch); the rest of the community detail renders clean. **One NEW defect (S7, class=bug, S2 sev):** the **Directory "Find someone on another server"** external lookup is **broken / stuck** — typing `lemmyadmin@lemmy.luit.ink` + Enter fires the proxy correctly (WebFinger + actor doc, both **200**) but the UI **stays on the loading spinner forever** — **no result card, no error, 0 console errors** — and re-pressing Enter **re-fires the request pair each time** (observed 4× the same two 200s), i.e. the async `LookupExternalAsync` (`Directory.razor:167-238`) never completes its re-render even though `ProxyGetAsync` + `client.GetObjectAsync` return valid 200s (the server relays the upstream status+body verbatim, `ActivityPubServerExtensions.cs:2034`/`:2161-2163`). The actor *itself* is fine — the intended destination `/actor?iri=…/lemmyadmin` renders perfectly — so the fault is isolated to the lookup card's result-binding / re-render path (the `obj is not Actor` / `finally` `_externalBusy=false` path never lands a rendered state; possible Blazor render-disposal or `GetObjectAsync` deserialize-then-drop). Repro: `/directory` → type a known remote handle → Enter → spinner spins indefinitely, no result, no error. Fix: trace `LookupExternalAsync` to completion (log/confirm `_externalResult` is set + state changed), and guard against a re-entrancy loop if `GetObjectAsync` re-issues the proxied read. [change doc](../changes/997-ui-ux-review.md)

**Pass 13 (2026-09-20, QA, exploratory):** since the last pass the other agent committed only a **docs** change (`d61462f` — recording Inbox ①); no new code, so no "recently finished" item to confirm. Re-ran `dotnet build Iris.Web.csproj -c Release` = **0/0** (green); app healthy (container `irisweb-iris-web-1` recreated 12 min ago, `/ap/v1/health` = healthy). **Clean entry → signed in as QAUser1.** Explored areas not previously deep-tested: **Notifications** (PASSES clean — All/Follows/Likes/Boosts/Replies/Mentions tabs + "Mark all as read" render, empty-state "No notifications yet," **0 console errors**; each filter tab dials a properly-scoped `GET /local/v1/notifications?type=…` — no request spam), **Search** (works — 25 results for "alice," actors-then-notes, 0 console errors; local + remote actor cards render with Block/Mute), and the **Lemmy community detail** (renders clean, 0 console errors, feed shows the 3 interop posts with Lemmy Upvote/Downvote/Score + "Boosted by interop"). **Re-verified S4 STILL OPEN:** Communities→Following shows "No communities followed yet" for QAUser1 (follows only the remote `lemmy.luit.ink/c/interop`); "All on this instance" lists every local community correctly, so it's specifically the remote-follow gap. **Two NEW defects found:** **S5 (class=bug/data-integrity, S2 sev):** the Search results list **two `alice` local actors** — the good one (`https://iris.luit.ink/ap/v1/u/alice`, 16 posts) **and** a stale orphaned one (`http://localhost:8088/ap/v1/u/alice`, **0 Objects + 0 Edges**, `PreferredUsername=alice`). Clicking the stale card → `/actor?iri=http%3A%2F%2Flocalhost…` → the client proxies it → server **502** (can't reach `localhost:8088` from inside the container) → page shows **"Actor not found."** for an actor that *does* exist. Confirmed in the store: exactly **1** actor row has a `localhost:8088` IRI (of 3,718; 21 use the public `iris.luit.ink` IRI; Objects has 0 localhost). Root cause: a record persisted under the container's **internal** host (dev seed / an old `BaseUrl`), left orphaned when the public IRI later became canonical. Fix: (a) **delete** the orphan row (verified safe — no `Objects`/`Edges` reference it), and (b) a **guard** so local actors are always stored/served under the public `BaseUrl` (never a `localhost`/internal IRI) + optionally the search handler skip/filter any local actor whose IRI host ≠ the public origin. **S6 (class=bug, S2 sev):** **Joining a remote community fails silently.** On the Lemmy community detail (which shows **both** "Unfollow" *and* "Join" for any non-self community — `CommunityDetail.razor:47-48`), clicking **Join** triggers a **direct browser `POST` to the remote `https://lemmy.luit.ink/c/interop/inbox`** — **CSP-blocked** (`connect-src 'self'`, 2 console errors) — and `JoinButton.razor:119-122`'s **empty `catch` swallows the failure**, so the button stays "Join" (never "Leave"), no error shown, nothing delivered. Root cause: `RequestJoinAsync` (`ActivityPubClient.cs:374`) calls `DeliverAsync(communityIri.InboxOf(), …)` — a **direct** browser POST to the **recipient's** remote inbox, which violates the codebase's own delivery model (`FollowAsync` at `:284` correctly posts to the actor's **own** outbox and lets the **server** own the recipient hop; the browser can only reach same-origin). Fix: route Join/Leave through a **server-side** endpoint (a local `/local/v1/…/join|leave` that the server delivers, mirroring how Follow is handled) rather than a direct remote-inbox POST; also surface the error in `JoinButton` instead of the empty catch. (Join is the Inbox **② Community simplification** item's "members=followers, Join/Leave map to Follow/Undo" target — S6 is the concrete symptom that the Join path is unusable for remote communities today.) [change doc](../changes/997-ui-ux-review.md)

**Pass 12 (2026-09-20, QA):** live-confirmed **Inbox ① (1)(2)(3) are fixed and holding** on the rebuilt container (commit `6819c0e`) — (1) moderation icons (Block/Mute/Report) are **gone from feed-card headers** (Home feed shows only Like/Boost/Reply + Lemmy Upvote/Downvote/Score) but **kept on the actor detail page** (`/actor?iri=…/alice` shows Block/Mute/Report beside Follow, 0 console errors); (2) the card-header color strip (per-actor `--card-hue` gradient) now spans the **full header row width** including under the "4d ago" timestamp; (3) the boost header reads "Boosted by **interop**" with **no "replying to" hint**. Signed-in `/home` feed renders correctly (3 interop posts via `POST /ap/v1/proxy/…`, all 200, **0 console errors** — the earlier "empty main" was a sub-second timing artifact, not a defect). **Re-verified my Pass 11 defects are still OPEN (not yet fixed):** **S2** (signed-out remote actor-doc direct-fetch CORS) — signed-out `/` still shows **6 console errors (3× CORS + 3× ERR_FAILED)** + 3 blank/fallback avatars for numeric-ID authors (`mastodon.social/ap/users/117294272768263207` + `117300166312075407`, `hachyderm.io/ap/users/117265166278633915`); **S3** (object-detail collections 404) — opening a local post's `/object?iri=…/creates/{id}` still 404s the **Create-activity** IRI's `/replies` (1 console error; the stored Note IRI `…/notes/{id}` serves 200 empty collections). **One NEW defect (S4, class=UX/bug, S2 sev):** the **Communities page "Following" tab** shows **"No communities followed yet"** even though the actor **does** follow a community — `Communities.razor` `ResolveFollowingCommunities()` (`:256-277`) only matches followed IRIs against the **local** search cache (`byIri.TryGetValue`) and has **no fetch-by-IRI fallback**, despite its own doc comment (`:253-254`) promising a followed community "not in the local search (a **remote** community…) is fetched by its IRI." So a followed **remote** community (`lemmy.luit.ink/c/interop`) is silently dropped → empty tab. Repro: QAUser1 follows only the remote `interop` community; Profile→Following lists it correctly + its posts populate `/home`, but Communities→Following is empty (the page does fire `GET /following` 200 — it just doesn't render the remote Group). Local communities render fine under "All on this instance" (owner-test-5428, piefed-test, test-882, …), so this is specifically the remote-follow gap. Fix: implement the documented fetch-by-IRI fallback (or list followed communities directly from the `/following` collection's resolved docs). **Minor (efficiency note, not a defect):** Profile page fires `GET /outbox` **twice** on load (the "Your posts" + "Replies" tabs both dial the same outbox) — the redundant-first-fetch area already tracked under Inbox ①(4). [change doc](../changes/1000-inbox-1-card-header-polish.md)

**Pass 11 (2026-09-20, QA):** live-confirmed all three **Pass 10 defects are fixed and holding** — Defect 1: signed-out home feed fires **0** authenticated-proxy 401s (was 15–17); Defect 2: a `lemmy.ml` proxy fetch (e.g. `/actor?iri=https://lemmy.ml/u/Gargron`) returns a **clean 502** (was unhandled 500) and the page degrades to "Actor not found." with no error overlay; Defect 3: the Lemmy community `lemmy.luit.ink/c/interop` dials **`/outbox` + `/followers` (both 200)**, not the 404 `/feed`+`/members`, and renders its real feed (3 posts, Lemmy vote bar) with 0 console errors. **Signed-in smoke (QAUser1, newly registered):** register→home, compose end-to-end (**Posted HTTP 202**, post appears on Profile, hard-refresh stable), Settings/Directory/Search (25 results for "alice")/Notifications/Communities all render 0 console errors; following a remote Lemmy community puts its posts in `/home`; **signed-in remote reads correctly route through `POST /ap/v1/proxy/…`** (0 direct cross-origin fetches) — including a numeric-ID Mastodon actor (`mastodon.social/ap/users/117294272768263207` → 200, resolves to "ctartworld"). **One NEW defect (S2, class=UX/bug):** **signed-out** remote **actor-document** reads bypass the proxy and are fetched **directly by the browser** — `UiContext.FetchActorAsync` (`Iris.Web.Client/Ui/UiContext.cs:489`) skips the proxy when `_session.Client is null` and falls to `FetchActorDocumentAnonymousAsync` (`:611`, a bare `GET` with no ActivityPub `Accept`). `https://{host}/users/{username}` answers with `Access-Control-Allow-Origin: *` (200), but `https://{host}/ap/users/{numeric-id}` (Mastodon's strict AP surface) sends **no** `Access-Control-Allow-Origin` → the browser CORS-blocks it (confirmed: `mastodon.social/users/deadline` = 401 **with** `ACAO:*`; `mastodon.social/ap/users/117294272768263207` = 401 **without**). Live result: signed-out `/` shows **6 console errors (3× CORS + 3× ERR_FAILED) + 3 blank/fallback avatars** for numeric-ID authors (the public feed carries 16 `/ap/users/{id}/statuses/` objects; content is inlined so only the *actor* docs fail). **Design rule (owner, 2026-09-20): any request for a remote actor or content from the UI should move through the proxy endpoint.** The fix is a **public/anonymous GET proxy path** (the server already resolves these numerics fine when signed — `ProxyHandler` + `TryServeCachedTargetAsync`), letting signed-out visitors route remote actor/content reads through the same-origin proxy instead of direct fetches; the proxy's hard `401 if authenticatedHandle is null` gate (`ActivityPubServerExtensions.cs:1844-1846`) must allow unsigned GET reads of public remote content (rate-limited, target-policy-checked). Re-verify from a clean entry after the fix. **Second NEW defect (S3, class=UX/bug):** opening a **local** post's object-detail page (`/object?iri=…/creates/{id}`, the deep-link the Profile tab + the post's own Reply button produce) 404s the post's own `/replies`+`/likes`+`/shares` collections — **3 console errors** — even though the post renders fine with "No replies yet." Root cause: a local post has **two IRIs** — the **Create activity** (`…/creates/{id}`, the `?iri=` deep-link value) and the **stored Note object** (`…/notes/{id}`, a *different* id). `ObjectDetail.razor`'s `ObjectIri` getter (`:339-349`) returns the **raw `IriParam`** (the activity IRI) and derives `/replies`+`/likes`+`/shares` from it (`:403`, `:409`, replies load `:504`). But the server serves those collections only for the **stored object** IRI (an activity IRI is "an object this instance does not store" → 404, `ObjectRepliesAsync`/`ObjectLikesAsync` `ActivityPubServerExtensions.cs:7856-7862,7917-7919`). Verified live: `…/notes/…5M/replies|likes|shares` = **200 empty collections** (correct); `…/creates/…5G/replies|likes|shares` = **404** (the bug). The page already resolves the Note via `SubjectObject` (the content + Reply href render from it — the Reply link even points at `…/notes/…5M`), so the fix is to derive the collection walks from the **resolved Note IRI** (`SubjectObject.Id` / the object the doc wraps), not the raw `?iri=` activity IRI. (A secondary server-side option: serve a Create activity's object's collections too, or 302 the activity IRI → object IRI.) **Also observed (not defects):** (a) object-card author headers still show the raw numeric ID for `/ap/users/{id}` actors (Pass 8 minor observation — the card uses the IRI, not the resolved name); (b) the Lemmy community detail shows **both** "Follow" **and** "Join" buttons — the dual relationship set the Inbox **Community simplification** item collapses into one. [change doc](../changes/997-ui-ux-review.md)

**Pass 10 (2026-09-19):** found and fixed **three real defects** — (1) signed-out home feed called the authenticated proxy per remote actor → 401 spam + blank avatars (now skips the proxy when signed out → 0 console errors); (2) proxy upstream abort (lemmy.ml Kerberos/TLS) escaped as unhandled 500 (now a clean 502, like the media proxy); (3) cached Lemmy community misclassified as Iris because the actor-count refresh stamps `iris:*Count` onto every stored Group, so `IsLemmy()`'s "no `iris:` keys" negative test failed → `/feed`+`/members` 404s (now positive on Lemmy's bare fields `language`/`featured`/`sensitive`/`postingRestrictedToMods` → `/outbox`+`/followers`, real feed renders). All committed + live-verified. 138.25 surfaces are wired but no live post/community currently carries the flags (no banner visible against real data).