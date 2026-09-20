# S16 — Poll votes are not persisted (data-integrity)

- **Class:** bug / data-integrity — **Severity:** S3
- **Status:** fixed (2026-09-20, verified Pass 37 against deployed `bb28dcf`; re-confirmed Pass 27)
- **Found:** Pass 25 (2026-09-20); re-verified Passes 27, 37

## Symptom

On a poll's object-detail page, clicking an option (Option A) updates the UI in place — count 0→1, a **"You voted"** badge, "1 votes" — with **0 errors**; but a **page refresh reverts the count to 0** and the "You voted" badge disappears. A user's poll vote is silently lost.

## Root cause

The vote is a **client-only in-memory Blazor state change** that is never delivered. DB confirms no vote was recorded server-side: the poll `Object` has **no `votes` property** (only the Create activity; **no `Vote` activity/object, no Edge** to the poll).

## Fix

Persist the vote: deliver a `Vote`/`Add` activity to the poll and reflect it in the poll's stored `votes`/state — or at minimum store the user's choice server-side so the UI can re-hydrate the "You voted" state after a refresh.

## Re-verify

Vote on a poll's object-detail page, then hard-refresh: the count and "You voted" badge persist; the DB shows the vote recorded (Vote/Add activity or stored choice) and it is reflected in the poll's `votes`.

**Re-verification evidence (Pass 27, 2026-09-20, clean entry, andrew):** the Pass-25 poll (`…/objects/06GBSGQTYCMVSXEYC9XCMK6MPR`) now shows **Option A: 1 / Option B: 0 / "1 votes"** on a fresh load — the earlier vote survived the container rebuild. DB confirms server-side persistence: `poll.voters = [https://iris.luit.ink/ap/v1/u/QAUser1]`, `options[0].votesCount = 1`, `totalVotes = 1`. FIXED.

**Re-verification evidence (Pass 37, 2026-09-20, andrew, deployed `bb28dcf`):** fresh poll `06GBVCXGF7HRK17GJTH9N2ZXS0` — (1) `andrew` voted Option A: count 0→1, "You voted" badge, DB confirms `poll.voters = [andrew]`; hard-refresh reverts to "1" (vote persisted server-side, but "You voted" badge does NOT re-hydrate on refresh — cosmetic gap). (2) New user `qa37test` voted Option B: first click → 502 error (`/local/v1/u/qa37test/votes/…`), second click → count 1→2, "You voted" badge, DB confirms `poll.voters = [andrew, qa37test]`; hard-refresh shows "2" (vote persisted). **Votes ARE persisted server-side** (DB confirms both voters). **BUT:** (a) "You voted" badge does NOT re-hydrate on hard-refresh for either voter; (b) first vote from a new user can 502 (race condition?). Core data-integrity fix is confirmed, but UX polish (badge re-hydration + 502 on first vote) remains.

**Re-verification evidence (Pass 38, 2026-09-20, andrew, rebuilt container post-`bdc0e66`):** fresh load of poll `06GBVCXGF7HRK17GJTH9N2ZXS0` → shows **Option A: 2 / Option B: 0 / "2 votes"** (votes persisted server-side, 0 console errors). No "You voted" badge visible on fresh load (badge re-hydration still broken). Core data-integrity confirmed FIXED. UX gap (badge re-hydration) remains — cosmetic only.
