# S4 — Communities "Following" tab drops followed REMOTE communities

- **Class:** UX / bug — **Severity:** S2
- **Status:** **CLOSED (QA re-verified, 2026-09-22, build `7620faa1`)** (fixes `83eb22aa` + `cca01fa7`). Re-verify: after `ii-b1`@B joined the remote community `ii-a8-community`@A, the remote community appears in **both** Communities→Following and Profile→Communities with a "Leave" button. (An initial "No communities" reading was a timing artifact — the cross-instance proxy fetch was still in flight; it rendered once the fetch completed.)
- **Found:** Pass 12 (2026-09-20) — re-confirmed Passes 13, 14, 15, 37

## Symptom

The Communities page **"Following"** tab shows **"No communities followed yet"** for an actor who **does** follow a community — specifically a **remote** one (e.g. QAUser1 follows only `lemmy.luit.ink/c/interop`). Meanwhile **Profile → Following** lists it correctly, and its posts populate `/home`. The "All on this instance" tab lists every local community fine, so it's specifically the remote-follow gap.

## Root cause

`Communities.razor` `ResolveFollowingCommunities()` (`:256-277`) only matches followed IRIs against the **local** search cache (`byIri.TryGetValue`) and has **no fetch-by-IRI fallback** — despite its own doc comment (`:253-254`) promising a followed community "not in the local search (a **remote** community…) is fetched by its IRI."

## Fix

For each followed IRI not in the local cache, fetch the actor doc by IRI (via the signed client / proxy) and keep the ones that are `Group`s — the behavior the comment already describes.

## Re-verify

With QAUser1 (follows only the remote interop community): Communities → Following shows **"Iris Interop"** (not the empty state), 0 console errors.

**Re-verification evidence (Pass 27, 2026-09-20, andrew):** Communities → Following shows **"technology" with a Leave button** (andrew follows it), 0 console errors — the empty-state symptom is gone for local communities. **Caveat:** QAUser1's follow edge no longer exists in the DB (the `Following` table is empty for QAUser1; the follow was likely dropped during a rebuild), so the specific *remote* community display could not be re-verified this pass. Re-open if a fresh remote follow still doesn't appear in this tab.

**Re-verification evidence (Pass 37, 2026-09-20, andrew, deployed `bb28dcf`):** `andrew` follows BOTH `technology` (local) and `lemmy.luit.ink/c/interop` (remote). Communities → Following tab shows **only "technology"** — the remote `interop` community is **missing** from the Following tab, despite the follow edge existing (confirmed: the interop actor page shows "Unfollow" button). 0 console errors. **Remote-follow display is still broken.** STILL OPEN for remote communities.

**Re-verification evidence (Pass 38, 2026-09-20, andrew, rebuilt container post-`bdc0e66`):** Communities → Following tab still shows **only "technology"** (local) — the remote `interop` community is still **missing**. 0 console errors. STILL OPEN for remote communities.

**Re-verification evidence (Pass 39, 2026-09-20, andrew, deployed `4f5dd5c`):** Communities → Following tab still shows **only "technology"** (local) — the remote `interop` community is still **missing**. 0 console errors. STILL OPEN for remote communities.

**Re-verification evidence (Pass 41, 2026-09-20, andrew, deployed `59ff4ec`):** Communities → Following tab still shows **only "technology"** (local) — the remote `interop` community is still **missing**. 0 console errors. STILL OPEN for remote communities.

**Re-verification evidence (Pass 42, 2026-09-20, andrew, deployed `65ccfa0`):** Communities → Following tab still shows **only "technology"** (local) — the remote `interop` community is still **missing**. Confirmed the follow edge exists: navigating directly to `lemmy.luit.ink/c/interop` community page shows **"Leave" button** (not "Follow"), proving andrew follows it. 0 console errors. STILL OPEN for remote communities.

**Re-verification evidence (Pass 43, 2026-09-20, andrew, deployed `65ccfa0`):** Communities → Following tab still shows **only "technology"** (local) — the remote `interop` community is still **missing**. 0 console errors. STILL OPEN for remote communities.

**Re-verification evidence (Pass 50, 2026-09-20, andrew, deployed `65ccfa0`):** Communities → Following tab shows **"technology" + "qa-pass46-test"** (both local, both with "Leave" button) — the remote `lemmy.luit.ink/c/interop` community is **still missing**. Confirmed the follow edge exists: navigating to `lemmy.luit.ink/c/interop` actor page shows **"Unfollow" button** (not "Follow"). Directory → Communities tab (This instance): `interop` listed with **"Join" button** (not "Leave") — the directory does not reflect the existing follow edge either. 0 console errors. STILL OPEN for remote communities (now 8 consecutive passes).

**Re-verification evidence (Pass 56, 2026-09-20, andrew, deployed `65ccfa0`):** Communities → Following tab shows **"technology" + "qa-pass46-test"** (both local, both with "Leave" button) — the remote `lemmy.luit.ink/c/interop` community is **still missing**. Confirmed the follow edge exists: navigating to `lemmy.luit.ink/c/interop` actor page shows **"Unfollow" button** (not "Follow"). 0 console errors. STILL OPEN for remote communities (now 9 consecutive passes).

**Re-verification evidence (Pass 62, 2026-09-20, andrew, deployed `65ccfa0`):** Communities → Following tab shows **"technology" + "qa-pass46-test"** (both local, both with "Leave" button) — the remote `lemmy.luit.ink/c/interop` community is **still missing**. Confirmed the follow edge exists: navigating to `lemmy.luit.ink/c/interop` actor page shows **"Unfollow" button** (not "Follow"). 0 console errors. STILL OPEN for remote communities (now 10 consecutive passes).

**Re-verification evidence (Pass 71, 2026-09-20, andrew, Dev fix deployed):** Communities → Following tab shows **"technology" + "qa-pass46-test" + "qa-pass65-test"** (all local, all with "Leave" button) — the remote `lemmy.luit.ink/c/interop` community is **still missing**. Confirmed the follow edge exists: navigating to `lemmy.luit.ink/c/interop` actor page shows **"Unfollow" button** (not "Follow"). 0 console errors. STILL OPEN for remote communities (now 11 consecutive passes).

**Re-verification evidence (Pass 75, 2026-09-20, andrew, Dev fix deployed):** Communities → Following tab shows **"technology" + "qa-pass46-test" + "qa-pass65-test"** (all local, all with "Leave" button) — the remote `lemmy.luit.ink/c/interop` community is **still missing**. Confirmed the follow edge exists: navigating to `lemmy.luit.ink/c/interop` actor page shows **"Unfollow" button** (not "Follow"). 0 console errors. STILL OPEN for remote communities (now 12 consecutive passes).

**Re-verification evidence (Pass 78, 2026-09-20, andrew, Dev fix deployed, container restarted 12:15:50):** Communities → Following tab shows **"technology" + "qa-pass46-test" + "qa-pass65-test"** (all local, all with "Leave" button) — the remote `lemmy.luit.ink/c/interop` community is **still missing**. Confirmed the follow edge exists: navigating to `lemmy.luit.ink/c/interop` actor page shows **"Unfollow" button** (not "Follow"). 0 console errors. STILL OPEN for remote communities (now 13 consecutive passes).

**Re-verification evidence (Pass 84, 2026-09-20, andrew, container 12:15:50):** Communities → Following tab shows **"technology" + "qa-pass46-test" + "qa-pass65-test"** (all local) — the remote `lemmy.luit.ink/c/interop` community is **still missing**. Note: `qa-pass46-test` description now shows "QA Pass 79 re-verify" (the Pass 79 Edit community Save was partially persisted — description updated, but the Update activity was not stored as a DB row). STILL OPEN for remote communities (now 14 consecutive passes).

**Re-verification evidence (Pass 88, 2026-09-20, andrew, container 12:43:01):** Communities → Following tab shows **"technology" + "qa-pass46-test" + "qa-pass65-test"** (all local) — the remote `lemmy.luit.ink/c/interop` community is **still missing**. Confirmed the follow edge exists: navigating to `lemmy.luit.ink/c/interop` actor page shows **"Unfollow" button** (not "Follow"). 0 console errors. STILL OPEN for remote communities (now 15 consecutive passes).

**Re-verification evidence (Pass 90, 2026-09-20, andrew, container 12:53:13):** Communities → Following tab shows **"technology" + "qa-pass46-test" + "qa-pass65-test"** (all local, all with "Leave" button) — the remote `lemmy.luit.ink/c/interop` community is **still missing**. Confirmed the follow edge exists: navigating to `lemmy.luit.ink/c/interop` actor page shows **"Unfollow" button** (not "Follow"). 0 console errors. STILL OPEN for remote communities (now 16 consecutive passes).

**Re-verification evidence (Pass 91, 2026-09-20, andrew, container 13:19:52):** Communities → Following tab shows **"technology" + "qa-pass46-test" + "qa-pass65-test"** (all local, all with "Leave" button) — the remote `lemmy.luit.ink/c/interop` community is **still missing**. Profile → Communities tab also shows only the 3 local communities (no remote interop). Confirmed the follow edge exists in DB: `SELECT "Kind","Source","Target" FROM "Edges" WHERE "Source"='https://lemmy.luit.ink/c/interop' AND "Target"='https://iris.luit.ink/ap/v1/u/andrew'` → Kind 10 (follow). STILL OPEN for remote communities (now 17 consecutive passes).

**Re-verification evidence (Pass 92, 2026-09-20, andrew, container 13:26:35):** Communities → Following tab shows **"technology" + "qa-pass46-test" + "qa-pass65-test"** (all local, all with "Leave" button) — the remote `lemmy.luit.ink/c/interop` community is **still missing**. STILL OPEN for remote communities (now 18 consecutive passes).

**Re-verification evidence (Pass 93, 2026-09-20, andrew, container 13:26:35):** Communities → Following tab shows **"technology" + "qa-pass46-test" + "qa-pass65-test"** (all local) — the remote `lemmy.luit.ink/c/interop` community is **still missing**. STILL OPEN (19 consecutive passes).

**Re-verification evidence (Pass 94, 2026-09-20, andrew, container 13:26:35):** Communities → Following tab shows 3 local communities (technology, qa-pass46-test, qa-pass65-test) — remote interop still missing. **STILL OPEN** (20 consecutive passes).

**Re-verification evidence (Pass 96, 2026-09-20, andrew, container 14:09:44):** Communities → Following tab shows 3 local communities (technology, qa-pass46-test, qa-pass65-test) — remote interop still missing. **STILL OPEN** (22 consecutive passes).

**Fix (dev2, 2026-09-22):** Two root causes addressed:
1. **Client (commit `83eb22aa`):** `Communities.razor` `ResolveFollowingCommunitiesAsync` and `Profile.razor` `LoadFollowedCommunitiesAsync` now fetch followed IRIs not in the local search cache via `Ui.GetActorAsync` (routes remote actors through the same-origin proxy) and keep them if `Group`.
2. **Server (commit `cca01fa7`):** When a remote instance accepts a follow, it delivers the Accept to the original follow activity's IRI (`/u/{handle}/follows/{ulid}`), which is not a valid actor IRI. `HandleInboxPostAsync` now re-resolves the recipient for Accept/Reject activities: it reads the referenced follow from the activity store and uses the follower (the follow's actor) as the recipient. This fixes the "unknown recipient" 404 that prevented the follower from confirming the follow.

**Re-verification evidence (dev2, 2026-09-22, deployed `cca01fa7`):** New account `s4e2e2` on iris-b follows remote community `test-community` on iris-a. Communities → Following tab shows **"test-community" with a "Leave" button**. Profile → Communities tab also shows **"test-community" with a "Leave" button**. The Accept from iris-a is correctly delivered and processed (log: `Inbox re-resolved: .../follows/... → .../u/s4e2e2 (follow response)`). **S4 CLOSED.**
