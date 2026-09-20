# S22 — Follow notification not created for local follows

- **Class:** bug / data-integrity — **Severity:** S2
- **Status:** fixed (2026-09-20, deployed `ee47565`) — re-verify pending
- **Found:** Pass 91 (2026-09-20)
- **Fixed:** Pass 94 (2026-09-20) — notification follow-filter fix deployed
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

**Root cause analysis (Pass 93, 2026-09-20, container 13:26:35):** The follow activities **DO exist** in the database:
- `Activities` table: `qa91test/follows/…` and `qa92test/follows/…` both present with `ActivityType = 'Follow'`, `ObjectIri = andrew`.
- `BoxItems` table: Both follow IRI values are in `andrew`'s inbox (`Direction=1`, `ActorId=andrew`).
- `Edges` table: Both follow edges exist (`Kind=0`, `qa91test→andrew`, `qa92test→andrew`).

However, `GET /local/v1/notifications?type=Follow` returns `{"items":[],"totalItems":0}`. Other notification types work correctly:
- `type=Like` → 13 items
- `type=Announce` → 77 items
- `type=Mention` → 3 items
- `type=Reply` → 0 items (expected — no replies)
- `type=Follow` → **0 items (BUG — 7 Follow activities exist in Activities table targeting andrew)**

**Suspected root cause:** The notification query for `type=Follow` does not resolve local Follow activities from the inbox. It may only be looking at remote follows (via the shared inbox) or using a different join path that doesn't include local follows. The local Follow activities are stored in `Activities` with `ObjectIri = andrew` and are in `andrew`'s `BoxItems` inbox, but the API's Follow-type filter doesn't pick them up.

**Fix:** Ensure the `/local/v1/notifications?type=Follow` query includes local Follow activities from the `Activities` table where `ObjectIri` matches the current user's IRI, joined with `BoxItems` (Direction=1) for inbox verification.

**Fix applied (2026-09-20, commit `ee47565`):** The actual root cause was different from the suspected cause. The Follow activities WERE being stored in the inbox correctly, but the notification endpoint's follow-filter logic was incorrectly filtering them out. The filter was removing ALL Follow notifications that were not in the pending follow-request queue, but auto-accepted follows (from users without `manuallyApprovesFollowers`) are never in the queue. The fix adds a check: only filter out Follow notifications when the account has `manuallyApprovesFollowers` set. For auto-accepting accounts, Follow notifications remain visible. See [change doc 1595](../changes/1595-notification-follow-filter-fix.md).

**Re-verification evidence (Pass 95, 2026-09-20, andrew, container 14:09:44):** **S22 FIXED.** API: `GET /local/v1/notifications?limit=20&offset=0&type=Follow` → **200**, `{"items":[...7 items...],"totalItems":7}`. Follow notifications now appear in the UI: Notifications → Follows tab shows 7 follow requests (qa92test, qa91test, qa39test, qa36test, qa34test, newuser1, RayvenMX) each with **Accept** and **Decline** buttons. Dev commit `ee47565` ("Fix notification follow-filter") resolved the issue. **S22 CLOSED.**
