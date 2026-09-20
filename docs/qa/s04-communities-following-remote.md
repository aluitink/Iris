# S4 — Communities "Following" tab drops followed REMOTE communities

- **Class:** UX / bug — **Severity:** S2
- **Status:** open for remote communities (re-confirmed Pass 39, 2026-09-20, deployed `4f5dd5c`); fixed for local communities (Pass 27)
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
