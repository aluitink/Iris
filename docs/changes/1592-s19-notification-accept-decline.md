# S19 facet 2 — Notification Accept/Decline buttons for follow requests

- **Date:** 2026-09-20
- **Status:** done
- **QA finding:** `docs/qa/s19-community-requests-tab-fails.md`

## Problem

Follow-request notifications in the Notifications page showed "sent you a follow request" with
no action buttons. The user had to navigate to the Profile page's Requests tab to accept or
decline, which is an unnecessary round-trip.

## Fix

- **`apps/Iris.Web.Client/Components/NotificationRow.razor`** — Added Accept and Decline buttons
  to notification rows whose activity type is `Follow` (a pending inbound follow request). The
  buttons call `Session.LocalModeration.AcceptFollowRequestAsync` /
  `RejectFollowRequestAsync` with the signed-in actor's IRI and the requester's IRI. On success
  the row's "New" highlight is cleared.

- **`apps/Iris.Web.Client/wwwroot/css/app.css`** — Added `.notification-card__actions` styles
  (flex row, gap, top margin) for the button row.

## Verification

- 1392 server tests passed (0 failed)
- Live-verified (s7test + s19-test, fresh browser):
  1. Enabled `manuallyApprovesFollowers` on s7test via Profile → Edit profile.
  2. s19-test followed s7test → follow request created (not auto-approved).
  3. s7test's Notifications page: "s19-test sent you a follow request" with **Accept** +
     **Decline** buttons.
  4. Clicked **Accept** → `POST /local/v1/u/s7test/requests/accept/…` → **204**. s19-test now
     appears in s7test's followers.
  5. s19-test unfollowed + re-followed s7test → new follow request.
  6. Clicked **Decline** → `POST /local/v1/u/s7test/requests/reject/…` → **204**. s19-test does
     **not** appear in s7test's followers.
  7. 0 console errors throughout.
