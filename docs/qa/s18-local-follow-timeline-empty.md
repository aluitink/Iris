# S18 — Following a local account: follow "succeeds" but the follower's Home timeline stays empty

- **Class:** bug / data-integrity — **Severity:** S2
- **Status:** partially fixed (Pass 39, 2026-09-20, on deployed `4f5dd5c` — follow state now persists, Home timeline populates; follow request not auto-approved)
- **Found:** Pass 29 (2026-09-20) — re-confirmed Passes 34, 36
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

**Re-verification evidence (Pass 34, 2026-09-20, andrew, deployed `456b0d9`):** Registered fresh account `qa34test`, followed local `andrew` (774 posts). Follow button flipped to "Unfollow". `qa34test` → Home → **"Your timeline is empty."** after 5s + Refresh. andrew's posts do not appear. STILL OPEN.

**Re-verification evidence (Pass 36, 2026-09-20, andrew, deployed `bb28dcf`):** Registered fresh account `qa36test`, followed local `andrew` (777 posts). Follow button flipped to "Unfollow"; `qa36test` appears in andrew's Followers (3) tab. `qa36test` → Home → **"Your timeline is empty. Follow people to see their posts here."** after 3s. andrew's posts do not appear. Additionally: on hard-refresh of andrew's actor page, the button shows **"Follow"** again (not "Unfollow") even though the follower edge exists — the follow state is not persisted server-side or the UI re-checks against an incomplete data source. STILL OPEN.

**Re-verification evidence (Pass 38, 2026-09-20, andrew, rebuilt container post-`bdc0e66`):** andrew's Followers tab shows **4** followers: `qa34test`, `qa36test`, + 2 others (likely remote). **`qa37test` is NOT present** (registered + followed andrew in Pass 37) — the follow state was lost (same as Pass 36's hard-refresh symptom). Home timeline emptiness not re-tested this pass (would require a fresh account). STILL OPEN (follow state not persisted).

**Re-verification evidence (Pass 39, 2026-09-20, andrew, deployed `4f5dd5c`):** Registered fresh account `qa39test`, followed local `andrew` (780 posts). Follow button flipped to "Unfollow". `qa39test` → Home → **timeline populated with posts** (CiaraNi, stephen, and others from andrew's follows). Hard-refresh of andrew's actor page → button still shows **"Unfollow"** (state persisted). andrew's notifications show "qa39test sent you a follow request" (with NO Accept/Decline buttons — see S19). andrew's Followers tab shows count **4** (not 5). **PARTIALLY FIXED:** follow state now persists server-side, Home timeline populates correctly. Remaining issue: the follow request is not auto-approved (andrew must accept it), and the Followers count doesn't increment until acceptance.
