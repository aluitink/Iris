# S16 — Poll votes are not persisted (data-integrity)

- **Class:** bug / data-integrity — **Severity:** S3
- **Status:** fixed (2026-09-20, verified Pass 27 against the 2026-09-20 03:31 UTC rebuild; deployed-commit label in PLAN.md was stale, see Pass 27 note)
- **Found:** Pass 25 (2026-09-20); re-verified Pass 27 (2026-09-20)

## Symptom

On a poll's object-detail page, clicking an option (Option A) updates the UI in place — count 0→1, a **"You voted"** badge, "1 votes" — with **0 errors**; but a **page refresh reverts the count to 0** and the "You voted" badge disappears. A user's poll vote is silently lost.

## Root cause

The vote is a **client-only in-memory Blazor state change** that is never delivered. DB confirms no vote was recorded server-side: the poll `Object` has **no `votes` property** (only the Create activity; **no `Vote` activity/object, no Edge** to the poll).

## Fix

Persist the vote: deliver a `Vote`/`Add` activity to the poll and reflect it in the poll's stored `votes`/state — or at minimum store the user's choice server-side so the UI can re-hydrate the "You voted" state after a refresh.

## Re-verify

Vote on a poll's object-detail page, then hard-refresh: the count and "You voted" badge persist; the DB shows the vote recorded (Vote/Add activity or stored choice) and it is reflected in the poll's `votes`.

**Re-verification evidence (Pass 27, 2026-09-20, clean entry, andrew):** the Pass-25 poll (`…/objects/06GBSGQTYCMVSXEYC9XCMK6MPR`) now shows **Option A: 1 / Option B: 0 / "1 votes"** on a fresh load — the earlier vote survived the container rebuild. DB confirms server-side persistence: `poll.voters = [https://iris.luit.ink/ap/v1/u/QAUser1]`, `options[0].votesCount = 1`, `totalVotes = 1`. FIXED.
