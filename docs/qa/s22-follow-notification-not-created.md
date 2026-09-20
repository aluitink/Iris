# S22 — Follow notification not created for local follows

- **Class:** bug / data-integrity — **Severity:** S2
- **Status:** open (found Pass 91, 2026-09-20, on deployed `a661cdd`)
- **Found:** Pass 91 (2026-09-20)
- **Related:** [S19](s19-community-requests-tab-fails.md) (facet 2: Accept/Decline buttons — blocked by this bug), [S18](s18-local-follow-timeline-empty.md) (local follow timeline empty)

## Symptom

When a local user follows another local user, the **follow notification is not created** on the followed user's notifications page. The follow edge is created in the database, but no corresponding notification row is generated.

Repro (andrew + qa91test, both local):
1. Register new account `qa91test` via `/register`.
2. Navigate to `andrew`'s actor page (`/actor?iri=…/u/andrew`).
3. Click **Follow** → button changes to **Unfollow** (follow succeeded).
4. DB confirms the edge: `SELECT "Kind","Source","Target" FROM "Edges" WHERE "Source" LIKE '%qa91test%'` → Kind 0, `qa91test → andrew`.
5. Log in as `andrew` → `/notifications` → **Follows tab** → **"No notifications yet"** (0 items).
6. API: `GET /local/v1/notifications?limit=20&offset=0&type=Follow` → **200**, `{"items":[],"totalItems":0}`.

The follow **edge exists** but **no notification was created**. The `unread-count` endpoint also shows 0 unread follow notifications.

## Root cause (suspected)

The follow handler creates the edge in the `Edges` table but does **not** create a corresponding notification record (in `Objects` or a dedicated notifications table). The notification system may only be wired for remote follows (via the shared inbox) but not for local-to-local follows.

## Fix

- When a local user follows another local user, create a follow notification record for the followed user.
- Ensure the notification appears in the followed user's `/notifications` page under the **Follows** tab.
- The notification should include **Accept/Decline** buttons (S19 facet 2) if the followed user has `manuallyApprovesMembers` enabled.

## Re-verify

Clean entry, two local accounts:
1. Account A follows Account B.
2. Account B's `/notifications` → Follows tab shows the follow request from Account A.
3. The notification has **Accept/Decline** buttons (if applicable).
4. Accepting/Declining updates the relationship correctly.
5. 0 console errors.

**Re-verification evidence (Pass 91, 2026-09-20, andrew, container 13:19:52):** Registered `qa91test` → followed `andrew` (edge created in DB). `andrew`'s notifications → Follows tab → **"No notifications yet"** (0 items). API: `GET /local/v1/notifications?limit=20&offset=0&type=Follow` → **200**, `{"items":[],"totalItems":0}`. The follow edge exists but no notification was created. **NEW BUG confirmed.**

**Re-verification evidence (Pass 92, 2026-09-20, andrew, container 13:26:35):** Registered `qa92test` → followed `andrew` (edge created in DB). `andrew`'s notifications → Follows tab → **"No notifications yet"** (0 items). API: `GET /local/v1/notifications?limit=20&offset=0&type=Follow` → **200**, `{"items":[],"totalItems":0}`. Both `qa91test` and `qa92test` follow edges exist in DB but NO notifications were created. **S22 STILL OPEN (2nd pass).**
