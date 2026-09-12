# 87.1 — Live community verification (Feed → Group, end-to-end)

**Phase:** 87.1 — Live community verification (Feed → Group, end-to-end)
**Date:** 2026-09-12
**Status:** Complete

## Objective

Manually verify (Playwright, no new coded web tests per the WASM Manual-Test Policy) that a
`Feed`-typed ActivityPub community renders as a first-class community in the Iris web client
against the live Docker app. This exercises the 86.1 `Feed`→`Group` mapping in the real
production code path (server + WASM client) — not just the in-process test seams.

## What was verified

### 1. Server: Feed community document served correctly

- Seeded a `Feed`-typed community (`piefed-test`, IRI `https://iris.luit.ink/ap/v1/c/piefed-test`)
  directly into Postgres `Actors` (bypassing the signed AP write path, which 86.1 does not gate).
- `GET http://localhost:8088/ap/v1/c/piefed-test` → **HTTP 200**, `type: Feed`, with correct
  `id` / `name` / `preferredUsername` / `members` / `inbox`.
- This exercises `EfCommunityStore.TryGetCommunityAsync`'s `Type=="Feed"` read filter and the
  `CommunityDocumentHandler` — proving the 86.1 read-side mapping works in the live server.

### 2. UI: Feed community detail page renders as a first-class community

Navigated to `/community?iri=https%3A%2F%2Firis.luit.ink%2Fap%2Fv1%2Fc%2Fpiefed-test`:

- Heading **"PieFed Test Community"** (h1).
- Header card: avatar, handle `piefed-test`, name, summary
  ("A Feed-typed community seeded for the Phase 87 live verification (PieFed/Pleroma-fork wire type).").
- Action row: **Follow** / **Join** buttons + "0 members".
- "+ Post to this community" link.
- Tabs: **Feed** / **Members (0)**; Feed tab shows "Community Feed" + "No posts in this community yet."

The community passes the client-side `is Group` filter (`CommunityDetail.razor` / `Ui.GetActorAsync`),
confirming the 86.1 mapping deserializes the `Feed` wire document to a `Group` in the WASM client.

### 3. UI: Feed community appears in the communities list + directory

- **`/communities`** page: `piefed-test` / "PieFed Test Community" listed (passes the
  `.Where(o => o is Group)` filter at `Communities.razor:158-161`).
- **`/directory` → Communities tab**: `piefed-test` listed with a Follow button
  (passes `Actors?.OfType<Group>()` at `Directory.razor:137`).

### 4. UI: Follow round-trips (Accept lands, edge recorded)

- Clicked **Follow** on the community detail page → button flipped to **Unfollow**.
- DB verification (`Edges` table):
  - `Kind 0` (Follow): `verifier87 → piefed-test`
  - `Kind 10` (Accept): `piefed-test → verifier87`
  - `Kind 11` (Follows): `verifier87 → piefed-test`
- The community scheduled and recorded an `Accept` in response to the follow — the
  `FollowActivityHandler`'s community branch works for a `Feed`-typed community.

### 5. Console: zero errors

- `browser_console_messages(level=error)` → **0 errors, 0 warnings** across the community
  detail, `/communities`, and `/directory` pages.

## Environment

- Live Docker app: `irisweb-iris-web-1` (host port **8088**), rebuilt from current code
  (the old image predated the 86.1 fix — its `Iris.Core.dll` had zero "Feed" refs).
- DB: `irisweb-db-1` (postgres:16), AdvertiseBase `https://iris.luit.ink`.
- Login: alice's password had been changed since the `.env` bootstrap (the 52.1 change-password
  test), and repeated failed attempts triggered a 9-minute rate-limit. A fresh account
  (`verifier87` / `verify87pass`) was registered via the normal `/register` flow (which provisions
  a real local actor) and used for the verification.

## Findings

**All verification points passed.** No code seam broke. The 86.1 `Feed`→`Group` mapping is
confirmed working end-to-end (server + WASM client + follow round-trip) against the live app.

- No 87.2 fix slice is needed.
- The seeded `Feed` community `piefed-test` and the `verifier87` account remain in the live DB as
  verification artifacts (harmless; they are test data in a disposable Docker DB).

## Files

- No production code changed (86.1 already fixed the mapping; 86.2 added the server tests).
- This is a manual-verification change doc only.
