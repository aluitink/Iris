# S18 — Following a local account: follow "succeeds" but the follower's Home timeline stays empty

- **Class:** bug / data-integrity — **Severity:** S2
- **Status:** open (found Pass 29)
- **Found:** Pass 29 (2026-09-20)
- **Related:** [s11](s11-poll-silent-noop-and-outbox.md) (outbox/timeline data), the Home timeline

## Symptom

Following a **local** account appears to succeed, but the follower's **Home** timeline never shows the followed account's posts.

Repro (fresh account `qa29test` → follow local `andrew`):
1. Register `qa29test`, go to andrew's actor page, click **Follow**.
2. The button **flips to "Unfollow"** (UI treats it as a confirmed follow).
3. `qa29test` → **Home** → "Your timeline is empty. Follow people to see their posts here." — still empty after repeated **Refresh** and ~40 s.
4. andrew has many **public, non-CW** notes (e.g. "UI/UX review test note", "Media lifecycle s8 test post") — none appear in `qa29test`'s Home.

## What the DB shows (the follow is only half-applied)

- **Follow edge exists:** `Edges(Kind=0, Source=…/qa29test, Target=…/andrew)`.
- **Follow activity stored:** `…/qa29test/follows/06GBT8VJFT1KYNGG195AJ03P78`, `type=Follow`, actor `qa29test`, object `andrew` — **but with no `to` and no `cc`** (no audience).
- **andrew's followers collection is NOT updated:** `totalItems` is still **1** (only the pre-existing remote follower `mastodon.world/users/RayvenMX`); `qa29test` is **not** in the `orderedItems`.
- **`qa29test`'s inbox** contains **only** the Follow activity — none of andrew's posts were fanned out to it.

So the relationship is recorded as an edge + activity, the UI confirms it, but the follower's follower-collection is not updated and no content is delivered to the follower's inbox. The follower is, from the data's point of view, not actually being followed.

## Root cause

A local Follow is applied to the `Edges` table and persisted as an Activity, but the two things that make a follow *real* are skipped: (a) updating the target's `followers` OrderedCollection (add the follower, bump `totalItems`), and (b) fanning the target's public (and `cc`) content out to the new follower's inbox/queue. The missing `to`/`cc` on the stored Follow activity suggests the audience was never resolved, so delivery has nothing to target.

Expected: following a local account adds them to the target's followers collection and starts delivering the target's public/`cc` posts to the follower's Home; the follower's timeline populates.

## Fix

- On a local Follow: add the follower to the target's `followers` OrderedCollection and update `totalItems`.
- Populate the follower's inbox with the target's existing public/`cc` content (or seed the follower's Home from the target's outbox on first follow).
- Set a correct `to`/`cc` on the stored Follow activity.
- Confirm the UI only flips to "Unfollow" once the follow is actually persisted (it currently flips optimistically before the data is applied).

## Re-verify

Clean entry, fresh local account A, follow local account B (who has public posts):
- B's `followers` collection lists A and `totalItems` increments.
- A's **Home** timeline shows B's public posts (within a few seconds / one Refresh).
- The stored Follow activity has a valid `to`/`cc`.
