# S23 — Accept/Decline follow-request buttons hit 404 endpoints

- **Class:** bug / backend — **Severity:** S2
- **Status:** open (found Pass 95, 2026-09-20, on deployed `ee47565`)
- **Found:** Pass 95 (2026-09-20)
- **Related:** [S22](s22-follow-notification-not-created.md) (follow notifications now display — FIXED), [S19](s19-community-requests-tab-fails.md) (facet 2: Accept/Decline buttons — UI now visible but backend 404s)

## Symptom

Follow-request notifications now display correctly (S22 fixed), each showing **Accept** and **Decline** buttons. However, clicking either button hits a **404** endpoint:

Repro (andrew, 7 follow requests visible):
1. Log in as `andrew` → `/notifications` → **Follows tab** → 7 follow requests visible (qa92test, qa91test, etc.), each with Accept/Decline buttons.
2. Click **Accept** on qa92test → `POST /local/v1/u/andrew/requests/accept/https://iris.luit.ink/ap/v1/u/qa92test` → **404**.
3. Click **Decline** on qa91test → `POST /local/v1/u/andrew/requests/reject/https://iris.luit.ink/ap/v1/u/qa91test` → **404**.
4. DB: follow edge still exists (`Kind=0`, `qa92test → andrew`) — no state change.
5. UI: buttons remain visible after the 404 (no error message shown to user).

## Root cause (suspected)

The frontend calls `/local/v1/u/{handle}/requests/accept/{iri}` and `/local/v1/u/{handle}/requests/reject/{iri}`, but these endpoints are **not registered** in the backend router. The S19 facet 2 change doc references `Session.LocalModeration.Accept/RejectFollowRequestAsync` methods, but the corresponding HTTP routes may not be wired up.

## Fix

- Register the `POST /local/v1/u/{handle}/requests/accept/{iri}` and `POST /local/v1/u/{handle}/requests/reject/{iri}` routes in the backend.
- Accept: create a follow-back edge (or mark the follow as accepted), send an `Accept` activity to the follower.
- Decline: remove the follow edge (or mark as rejected), send a `Reject` activity to the follower.
- Show a user-facing error message if the request fails (instead of silently 404ing).

## Re-verify

1. Log in as `andrew` → `/notifications` → Follows tab → follow requests visible with Accept/Decline buttons.
2. Click **Accept** on a follow request → 200 response → follow-back edge created → button disappears or changes to "Unfollow".
3. Click **Decline** on a follow request → 200 response → follow edge removed → button disappears.
4. 0 console errors.

**Evidence (Pass 95, 2026-09-20, andrew, container 14:09:44):** Accept → `POST /local/v1/u/andrew/requests/accept/…` → **404**. Decline → `POST /local/v1/u/andrew/requests/reject/…` → **404**. DB edges unchanged. Buttons remain visible. **NEW BUG confirmed.**
