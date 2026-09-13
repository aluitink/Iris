# 123.1 — Notifications: deduplicate repeated follow requests

**Date:** 2026-09-13
**Branch:** phase-32-production-app

## Problem

When an actor sends multiple follow requests to the same recipient (e.g., Bob clicks "Follow Alice" 4 times during testing), Alice's notification inbox shows 4 identical "Bob sent you a follow request" cards instead of one.

**Root cause:** The server mints a fresh ULID IRI for every `Follow` activity (`MintActivityIds`, decision 055). All existing dedup is keyed on the activity IRI (`AddToInboxAsync` PK is `(Direction, ActorId, ItemIri)`), so each new Follow IRI passes as "new" and gets a separate `BoxItems` row. The `Edges` table correctly collapses the follow relationship to one edge, but the inbox write was unconditional.

## Fix

Two-part fix (write-time gate + read-time collapse):

### 1. Write-time gate (`RecordFollowLocalAsync`)

`RecordFollowLocalAsync` now checks `IsFollowingAsync` BEFORE `RecordFollowAsync` and returns a `(Iri? Target, bool IsNewFollow)?` tuple. The outbox-publish handler gates the `AddToInboxAsync` call on `isNewFollow` for Follow activities — a re-follow (edge already existed) skips the local inbox write. The `RecordFollowRequestAsync` call is also gated on `!alreadyFollowing` so a re-follow does not re-create a stale request edge.

The switch expression at the call site was restructured to handle the Follow case separately (it now returns a tuple, unlike the other `Record*Async` methods which return `Iri?`).

### 2. Read-time collapse (`FilterInboxByPrefs` / `DeduplicateFollows`)

A new `DeduplicateFollows` method collapses duplicate Follow notifications: for each unique `(actor IRI, target IRI)` pair, only the most recent Follow is kept. Other activity types pass through unchanged.

This runs on **both** the fast path (no prefs, no self-IRI) and the prefs-filtered path, so pre-existing duplicates (from before the write-time gate, or from the inbound federation path which has no write-time gate) are also collapsed on read. This also fixes the unread-count badge (which calls the same filter).

## Inbound federation path

The inbound path (`InboxProcessor.ProcessAsync`) was NOT modified. It gates `AddToInboxAsync` on `firstDelivery` (IRI-based dedup), which is sufficient for re-deliveries of the same Follow but not for new Follows with new IRIs. The read-time collapse acts as the safety net for this path.

## Test changes

**Deleted (1):** `FilterInboxByPrefs_NoSelfIri_NoPrefs_ReturnsUnchanged` — tested the old fast-path behavior (no noise filter, no dedup on the fast path). The fast path now runs the noise filter + dedup, so this test's premise no longer holds.

**Added (4):**
- `FilterInboxByPrefs_DuplicateFollows_CollapsesToMostRecent` — 3 Follows from same actor to same target → 1 (most recent kept)
- `FilterInboxByPrefs_FollowsFromDifferentActors_NotCollapsed` — 2 Follows from different actors → both kept
- `FilterInboxByPrefs_FollowsToDifferentTargets_NotCollapsed` — 2 Follows from same actor to different targets → both kept
- `FilterInboxByPrefs_DuplicateFollows_FastPath_NoSelfIri_NoPrefs` — dedup runs on the fast path (no prefs, no self-IRI)

## Verification

- `dotnet build` clean (0 warnings, 0 errors).
- `dotnet test` green: 1,822 pass (1,819 baseline + 4 new − 1 deleted), 0 fail. (1 known flaky federation test passes when the server suite runs alone.)
- Live-verified: 4 pre-existing duplicate "Bob sent you a follow request" entries in Alice's notifications now render as 1 card. 0 console errors.
