# 124.1 — General UI/UX review (third pass)

**Date:** 2026-09-13
**Branch:** phase-32-production-app

## Scope

Visual inspection of all pages (desktop 1400px + mobile 375px) via Playwright, signed in as `alice`. This is the third pass, following 120.1 (first) and 122.1 (second). Focus: verify the 123.* fixes (notification dedup, actor detail stats, community mobile banner) and find any remaining rough edges.

## Pages checked

- `/home` (desktop + mobile) — timeline renders, action buttons, mobile nav menu. Clean.
- `/notifications` (desktop + mobile) — dedup working (1 Bob follow request, not 4). Filter tabs, card layout, "Mark all as read" button. Clean.
- `/directory` (desktop) — actor cards, search. Clean.
- `/communities` (desktop) — community list + create form. Clean.
- `/community?iri=...` (desktop + mobile) — ownership banner stacks vertically on mobile (123.3 fix verified). Clean.
- `/object?iri=...` (mobile) — content, action buttons. Clean.
- `/actor?iri=...` (desktop + mobile) — stats row (posts, followers, following) visible (123.2 fix verified). Clean.
- `/profile` (desktop) — tabs, posts list. Clean.
- `/settings` (desktop) — 3 tabs, subsections. Clean.
- `/compose` (desktop + mobile) — textarea, action buttons. Clean.
- `/search` (desktop) — search box, actors-only toggle. Clean.

## Findings

**No new issues found.** The 123.* fixes are working correctly:
- Notification dedup: 1 "Bob sent you a follow request" card (was 4 before 123.1).
- Actor detail stats: "13 posts, 2 followers, 1 following" visible on Bob's page (123.2).
- Community mobile banner: "This is your community." / "Edit community" / "0 members" stack vertically on 375px (123.3).

0 console errors on every page (desktop + mobile).

## No code changes in this slice

This is a review-only slice. No new items were found.
