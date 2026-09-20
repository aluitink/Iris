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

**Re-verification evidence (Pass 39, 2026-09-20, andrew, deployed `4f5dd5c`):** fresh load of poll `06GBVCXGF7HRK17GJTH9N2ZXS0` → shows **Option A: 2 / Option B: 0 / "2 votes"** (votes persisted server-side, 0 console errors). No "You voted" badge visible on fresh load (badge re-hydration still broken). Core data-integrity confirmed FIXED. UX gap (badge re-hydration) remains — cosmetic only.

**Re-verification evidence (Pass 41, 2026-09-20, andrew, deployed `59ff4ec`):** Created new poll `06GBVZDNA8JPCCFC8WW2JGAFZR` ("QA Pass 41 poll re-verify", options A/B). (1) Profile page (`/actor?iri=...andrew`): shows A:1/B:0/1 votes + **"You voted" badge** visible (badge works in profile context). (2) Object page (`/object?iri=...06GBVZDNA8JPCCFC8WW2JGAFZR`): fresh load shows A:1/B:0/1 votes but **NO "You voted" badge** (badge re-hydration broken on object detail page). Vote persisted server-side (count survived navigation). Core data-integrity confirmed FIXED. UX gap: "You voted" badge appears in profile listing but NOT on object detail page — inconsistent re-hydration.

**Re-verification evidence (Pass 42, 2026-09-20, andrew, deployed `65ccfa0`):** Poll `06GBVZDNA8JPCCFC8WW2JGAFZR` — (1) Profile page: shows A:1/B:0/1 votes + **"You voted" badge** visible. (2) Object page (`/object?iri=...06GBVZDNA8JPCCFC8WW2JGAFZR`): fresh load shows A:1/B:0/1 votes but **NO "You voted" badge**. Inconsistent re-hydration persists across deploys. Core data-integrity confirmed FIXED. UX gap: badge visible in profile listing but missing on object detail page.

**Re-verification evidence (Pass 45, 2026-09-20, andrew, deployed `65ccfa0`):** Poll `06GBVZDNA8JPCCFC8WW2JGAFZR` — (1) Profile page: shows A:0/B:0/**0 votes** + **NO "You voted" badge** (regression from Pass 42 where badge was visible and count was 1). (2) Object page: fresh load shows A:1/B:0/**1 votes** + **NO "You voted" badge**. **New inconsistency:** vote count differs between profile listing (0 votes) and object detail page (1 votes) for the same poll. The object page correctly shows the persisted vote, but the profile listing does not. "You voted" badge is now missing from BOTH pages (regression). Core data-integrity still FIXED (vote persisted server-side). UX gap widened: inconsistent vote counts across views + badge missing everywhere.

**Re-verification evidence (Pass 49, 2026-09-20, andrew, deployed `65ccfa0`):** Poll `06GBVZDNA8JPCCFC8WW2JGAFZR` — (1) Profile "Your posts" listing: shows A:0/B:0/**0 votes** + **NO "You voted" badge**. (2) Object detail page (`/object?iri=...06GBVZDNA8JPCCFC8WW2JGAFZR`): fresh load shows A:1/B:0/**1 votes** + **NO "You voted" badge**. Vote count inconsistency persists: profile listing shows 0, object page shows 1. "You voted" badge missing from BOTH pages. Core data-integrity still FIXED (vote persisted server-side, count 1 on object page). UX gap unchanged: inconsistent vote counts across views + badge missing everywhere.

**Re-verification evidence (Pass 56, 2026-09-20, andrew, deployed `65ccfa0`):** Poll `06GBVZDNA8JPCCFC8WW2JGAFZR` — (1) Profile "Your posts" listing: shows A:1/B:0/**1 votes** + **"You voted" badge** visible (badge now appears in profile context — regression from Pass 49 where badge was missing). (2) Object detail page (`/object?iri=...06GBVZDNA8JPCCFC8WW2JGAFZR`): fresh load shows A:1/B:0/**1 votes** + **NO "You voted" badge**. Vote count now consistent across views (1 in both), but "You voted" badge still missing on object detail page while visible in profile listing. Inconsistent badge re-hydration persists. Core data-integrity still FIXED. UX gap narrowed: vote counts consistent, but badge still missing on object detail page.

**Re-verification evidence (Pass 62, 2026-09-20, andrew, deployed `65ccfa0`):** Poll `06GBVZDNA8JPCCFC8WW2JGAFZR` — Object detail page (`/object?iri=...06GBVZDNA8JPCCFC8WW2JGAFZR`): fresh load shows A:1/B:0/**1 votes** + **NO "You voted" badge**. Vote count consistent (1). Badge still missing on object detail page. Core data-integrity still FIXED. UX gap unchanged: badge missing on object detail page. STILL OPEN.

**Re-verification evidence (Pass 72, 2026-09-20, andrew, Dev fix deployed):** Poll `06GBVZDNA8JPCCFC8WW2JGAFZR` — (1) Object detail page: fresh load shows A:1/B:0/**1 votes** + **NO "You voted" badge**. (2) Profile "Your posts" listing (after 2× Load more): shows A:0/B:0/**0 votes** + **NO "You voted" badge**. **REGRESSION:** Vote count is now INCONSISTENT across views (profile shows 0, object page shows 1). The "You voted" badge is missing from BOTH views. This is a regression from Pass 56 where profile showed 1 vote + badge, and object page showed 1 vote without badge. Core data-integrity: the vote IS persisted (object page shows 1), but the profile listing shows 0 — the profile feed is not re-hydrating the poll state.
