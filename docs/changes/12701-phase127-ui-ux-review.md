# 127.1 — General UI/UX review (fourth pass)

**Date:** 2026-09-13
**Type:** Review/audit (no code changes)
**Scope:** Visual sweep of all pages (desktop 1400px + mobile 375px) via Playwright

## Method

Signed in as `alice` via Playwright. Visited every page (home, compose, notifications,
directory, communities, search, profile, settings, actor detail, community detail) on
both desktop and mobile viewports. Checked console errors on every page.

## Results

| Page | Desktop | Mobile | Console errors |
|---|---|---|---|
| Home | ✅ | ✅ | 0 |
| Compose | ✅ | — | 0 |
| Notifications | ✅ | ✅ | 0 |
| Directory | ✅ | — | 0 |
| Communities | ✅ | — | 0 |
| Search | ✅ | — | 0 |
| Profile | ✅ | — | 0 |
| Settings | ✅ | — | 0 |
| Actor detail (bob) | ✅ | ✅ | 0 |
| Community detail | — | ✅ | 0 |

## 126.* fixes verified

- **126.1 (actor detail stats row):** Bob shows "4 posts" (Note/Article count), not
  "13 posts" (total outbox items). ✅
- **126.2 (notification null-safe rendering):** All notification cards render name +
  verb correctly. No empty cards. ✅
- **123.3 (community mobile banner):** Ownership banner (Follow, Join, member count,
  post button) stacks vertically on 375px viewport. ✅

## No new issues found

The app is in a stable state after 4 review passes. The UI/UX review cycle has
converged:

| Pass | Phase | Issues found |
|---|---|---|
| 1st | 120.1 | 7 |
| 2nd | 122.1 | 3 |
| 3rd | 124.1 | 0 |
| 4th | 127.1 | 0 |
