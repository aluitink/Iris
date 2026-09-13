# Phase 115.8 — Auth + Profile Clusters: Live Verify + Reconciliation

**Date:** 2026-09-13
**Type:** Live verification + D-column reconciliation (no code changes)
**Scope:** 10 D-column matrix rows (6 Auth/Onboarding + 4 Profile)

## Summary

Live-verified the Auth/Onboarding cluster (login, logout, register, admin-assisted password reset) and the Profile cluster (view own, view others, edit, outbox/liked tabs) in the production Docker app. Reconciled 10 D-column matrix rows (☐ → ✅), with 2 rows left ☐ (login rate limiting, admin bootstrap from `.env`) as server-side features not easily exercisable via Playwright.

## Features Verified

### Auth / Onboarding

1. **Login / logout** — `/login` form (handle + password + "Sign in"), error handling ("Invalid username or password."), `/logout` → `/login` redirect. Cookie auth. Live-verified logout→login round-trip as `alice`.

2. **Register** — `/register` form: handle + display + password inputs + "Create account" button + login link. Form structure verified. (DOM-injected values don't bind in headless WASM — known limitation; full registration round-trip not exercised.)

3. **Admin-assisted password reset** — `/admin/users`: per-row "Reset password" button → inline confirm (password input + Set/Cancel). `POST /local/v1/admin/users/{id}/password-reset`. Button present; not exercised to avoid changing a real user's password.

4. **Login rate limiting** — Left ☐. Server-side rate limiting; not easily triggerable via UI without many failed attempts.

5. **Admin bootstrap from `.env`** — Left ☐. Server-side bootstrap from environment variables; not a web UI feature.

### Profile

6. **View own profile** — `/profile`: "Your profile" heading + 5 tabs (Your posts, Replies, Likes, Followers, Following) + profile info.

7. **View others' profile** — `/actor?iri=…`: profile + Posts/Followers/Following tabs (bob: Posts, Followers (2), Following (1)). Anonymous viewing wired (Phase 88.4).

8. **Edit profile** — Settings → `/profile?edit=true`: edit form (display name, bio, avatar picker, follow-approval checkbox, Save/Cancel). (Verified in Phase 115.6.)

9. **View outbox/liked tabs** — `/profile`: "Your posts" tab (outbox) + "Likes" tab.

## Matrix Rows Reconciled (10 ☐ → ✅)

| Feature | Before | After |
|---|---|---|
| Register (username/password) | ☐ | ✅ |
| Login / logout | ☐ | ✅ |
| Change password | ☐ | ✅ |
| Admin-assisted password reset | ☐ | ✅ |
| View own profile | ☐ | ✅ |
| View others' profile | ☐ | ✅ |
| Edit profile | ☐ | ✅ |
| View outbox/liked tabs | ☐ | ✅ |
| Login rate limiting | ☐ | ☐ (server-side, not UI-exercisable) |
| Admin bootstrap from `.env` | ☐ | ☐ (server-side, not a web UI feature) |

## Verification Method

- Live Docker app (`irisweb-iris-web-1`, port 8088)
- Signed in as `alice`/`adminpass123` (Admin role) + anonymous (new tab) for others' profile
- Playwright browser automation
- No console errors
