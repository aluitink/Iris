# S16 — Poll votes are not persisted (data-integrity)

- **Class:** bug / data-integrity — **Severity:** S3
- **Status:** open
- **Found:** Pass 25 (2026-09-20)

## Symptom

On a poll's object-detail page, clicking an option (Option A) updates the UI in place — count 0→1, a **"You voted"** badge, "1 votes" — with **0 errors**; but a **page refresh reverts the count to 0** and the "You voted" badge disappears. A user's poll vote is silently lost.

## Root cause

The vote is a **client-only in-memory Blazor state change** that is never delivered. DB confirms no vote was recorded server-side: the poll `Object` has **no `votes` property** (only the Create activity; **no `Vote` activity/object, no Edge** to the poll).

## Fix

Persist the vote: deliver a `Vote`/`Add` activity to the poll and reflect it in the poll's stored `votes`/state — or at minimum store the user's choice server-side so the UI can re-hydrate the "You voted" state after a refresh.

## Re-verify

Vote on a poll's object-detail page, then hard-refresh: the count and "You voted" badge persist; the DB shows the vote recorded (Vote/Add activity or stored choice) and it is reflected in the poll's `votes`.
