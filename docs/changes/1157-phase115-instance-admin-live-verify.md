# Phase 115.7 — Instance Admin Cluster: Live Verify + Reconciliation

**Date:** 2026-09-13
**Type:** Live verification + D-column reconciliation (no code changes)
**Scope:** 3 Instance admin D-column matrix rows

## Summary

Live-verified all 3 Instance admin features in the production Docker app (signed in as `alice`, Admin role) and reconciled their D-column (web-integration) matrix rows from ☐ → ✅. No code defects found.

## Features Verified

### 1. Instance metadata edit
- **Location:** `/admin/instance`
- **UI:** Name input (maxlength 255) + description textarea (rows 3, maxlength 1024) + Save button
- **Load:** `GET /local/v1/admin/instance` → `{ name: "Iris on luit.ink", description: "A decentralized social platform powered by Iris" }`
- **Save:** `PUT /local/v1/admin/instance` → "Instance settings saved."
- **Note:** Both load and save verified end-to-end.

### 2. Moderation queue
- **Location:** `/admin/moderation`
- **UI:** Table with columns Flagged by / Actor / Date / Action; per-row Dismiss button; "N open flag(s)" header
- **Load:** `GET /local/v1/admin/flags` → 2 flags (alice→ScottEdelman, bob→alice)
- **Action:** `POST /local/v1/admin/flags/dismiss` → dismissed alice→ScottEdelman flag; count dropped 2→1
- **Note:** Dismiss action verified end-to-end. No delete button (dismiss-only by design).

### 3. User list / role management
- **Location:** `/admin/users`
- **UI:** Table with columns Handle / Role / Created / Action; per-row buttons: Make admin/Demote to user, Reset password, Delete
- **Load:** `GET /local/v1/admin/users` → 4 users (alice=Admin, andrew=User, bob=User, verifier87=User)
- **Role toggle:** `POST /local/v1/admin/users/{id}/role` → bob User→Admin (button flipped to "Demote to user"), then bob Admin→User (button flipped back to "Make admin")
- **Note:** Role toggle verified end-to-end (both directions). Reset password + Delete buttons present but not exercised (destructive).

## Matrix Rows Reconciled (3)

| Feature | Before | After |
|---|---|---|
| Instance metadata edit | ☐ | ✅ |
| Moderation queue | ☐ | ✅ |
| User list / role management | ☐ | ✅ |

## Verification Method

- Live Docker app (`irisweb-iris-web-1`, port 8088)
- Signed in as `alice`/`adminpass123` (Admin role)
- Playwright browser automation
- No console errors
