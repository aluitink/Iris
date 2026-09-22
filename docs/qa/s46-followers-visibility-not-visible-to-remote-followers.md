# S46 — Followers-visibility note not visible to followers on other instances

- **Found:** Pass 310 (2026-09-22), build `345286cc`.
- **Severity:** S2 (data-visibility) — Followers-visibility content is not delivered to followers on other instances, breaking a core ActivityPub visibility guarantee.
- **Status:** **CLOSED** (dev1 `2efadfbc`+`a5d655e2`, merged to main `d5b50948`, QA re-verified Pass 315).

## Repro

1. On A, signed in as `ii-a1`, compose a note with visibility **Followers** (hint: "A note visible to followers only…"). Post it (HTTP 202).
2. The Create activity is delivered to B (ii-b1 follows ii-a1). B's inbox processes it: `CreateActivityHandler processed … ok` + `Inbox accepted`.
3. The note object IS stored in B's `Objects` table (not tombstoned, type `Note`).
4. **But** the note does NOT appear in ii-b1's home feed on B, and `GET /ap/v1/u/ii-a1/notes/…` on B returns **404**.

## Evidence

- **Server doc (A):** `to` = `https://qa-iris-a.luit.ink/ap/v1/u/ii-a1/followers`, `cc` = same (NOT `as:Public`). The Create activity has `cc: https://qa-iris-b.luit.ink/ap/v1/u/ii-b1` (delivered to the remote follower).
- **B logs:** `Inbox received Create … to https://qa-iris-b.luit.ink/ap/v1/u/ii-b1` → `Handler CreateActivityHandler processed … ok` → `Inbox accepted: Create … targeting …/notes/06GCM8Q20N47TJ68TECYEYKPTM`.
- **B DB:** `Objects` table has the note (Id = `…/notes/06GCM8Q20N47TJ68TECYEYKPTM`, `IsTombstoned` = f, `ObjectType` = Note). `Activities` table has the Create (ObjectIri matches). **No `Edges` row** links ii-b1 to the note (whereas older Public-visibility cross-instance notes DO have Kind=2 edges).
- **B feed:** ii-b1's home feed shows only ii-b1's own posts — no cross-instance content from ii-a1, including this Followers note.
- **Comparison:** Public-visibility cross-instance notes from ii-a1 DO appear in ii-b1's feed (they have `to: as:Public` and Kind=2 edges).

## Root cause

`src/Iris.Server/Services/VisibilityFilter.cs` — `IsFeedItemVisibleTo` → `IsVisibleTo`:

For a non-public note, visibility is granted only when the requester IRI **string-matches** an entry in `to`/`cc` (`MatchesAudience`, line 167-171) or is the author. For a Followers-visibility note, `to`/`cc` is the **followers collection IRI** (`…/ii-a1/followers`), not individual actor IRIs. The filter compares ii-b1's IRI (`…/ii-b1`) against `…/ii-a1/followers` → no match → note dropped from the feed.

The filter does not resolve the followers collection to check membership. It treats the collection IRI as a literal recipient IRI, which it is not. This means Followers-visibility notes are invisible to ALL followers (local and remote) who are not the author — the author clause is the only path through.

Additionally, the object endpoint (`GET /ap/v1/u/ii-a1/notes/…`) on B returns 404 for the stored note, suggesting a separate visibility gate on the object-document endpoint (or the object is stored under a different IRI than the endpoint resolves). The 404 is a secondary symptom; the feed filter is the primary defect.

## Expected behavior

A Followers-visibility note from ii-a1 should appear in the home feed of every actor who follows ii-a1 (on any instance), per the ActivityPub visibility model (`to`/`cc` = followers collection ⇒ visible to all followers).

## Scope

- **Affected:** All Followers-visibility notes (cross-instance delivery to followers). Direct-visibility notes are unaffected (their `to`/`cc` names individual recipient IRIs, which DO string-match).
- **Local followers:** Likely also affected (the same filter runs on the local feed), but not separately verified — the cross-instance case is confirmed.

## Fix direction

`VisibilityFilter.IsVisibleTo` must resolve the followers collection when the audience is a collection IRI: if `to`/`cc` contains a followers-collection IRI (e.g. ends in `/followers` or is a known collection), check whether the requester is a member of that collection (via `IFollowStore.GetFollowersAsync` or a membership lookup) rather than string-matching the collection IRI. The object-document endpoint's 404 also needs investigation (whether it applies the same VisibilityFilter or a different gate).

## Re-verification (Pass 314, 2026-09-22)

Dev1 committed fix `2efadfbc` (merged `ef2d24df`) adding async overloads `IsVisibleToAsync`/`IsFeedItemVisibleToAsync` that accept a follower-membership predicate and resolve `…/followers` audience entries to their owner actor. QA stack rebuilt with `--no-cache` to carry the fix.

**Result: Fix is NOT effective.**

- Composed a Followers-visibility note as ii-a1@A (HTTP 202, Note IRI `06GCMZMMD6JJA5KQWZZPWG5YYC`, `to`/`cc` = `…/ii-a1/followers`).
- Create delivered to B, processed, and the Note IS stored in B's `Objects` table (not tombstoned).
- **BUT** the note does NOT appear in ii-b1@B's home feed (verified via UI refresh).
- `GET /ap/v1/u/ii-a1/notes/06GCMZMMD6JJA5KQWZZPWG5YYC` on B returns **404** (with Basic auth as ii-b1).
- The new code IS deployed: `strings /app/Iris.Server.dll | grep IsVisibleToAsync` returns 9 matches.
- The follow edge exists: `Edges` table has `(ii-b1, ii-a1, Kind=0)` on B.
- The Create activity's `object` field contains the full Note document with correct `to`/`cc` = followers collection.

**Hypothesis:** The `isFollowerOfAsync` predicate may not be invoked (e.g., `FollowersCollectionOwners` returns empty, or the predicate is passed as null), OR the feed service is using a cached/compiled version of the filter that doesn't include the new async path. Further investigation needed (e.g., temporary logging in `IsFeedItemVisibleToAsync` to trace whether `isFollowerOfAsync` is called and what it returns).

## Re-verification (Pass 315, 2026-09-22) — FIXED

Dev1 committed a **second** fix `a5d655e2` (merged `d5b50948`) that addresses the **root cause** differently: `FeedService.IsFollowReply` was treating a `to` audience that is a followers collection (`…/followers`) as a directed reply (because `IsPublicAudience()` returns false for it), so the note was **filtered out of the feed as a "reply"** before the visibility filter ever ran. The fix adds `IriExtensions.IsFollowersCollection()` and updates the `IsFollowReply` fallback to skip followers-collection `to` entries (same as public sentinel).

**Result: S46 is FIXED.**

- QA stack rebuilt (`--no-cache`) to carry both fixes (`2efadfbc` + `a5d655e2`).
- Composed a Followers-visibility note as ii-a1@A (HTTP 202, Note IRI `06GCN9MA0MMBS99BP4SVCP678G`, `to`/`cc` = `…/ii-a1/followers`).
- Create delivered to B, processed, Note stored in B's `Objects` table.
- **The note APPEARS in ii-b1@B's home feed** (verified via UI: "QA Pass 315 S46 re-verify: Followers-visibility note for remote followers" visible, 1m ago, 0 console errors).
- **Residual:** `GET /ap/v1/u/ii-a1/notes/…` on A (as author ii-a1) and B (as follower ii-b1) still returns **404**. The Note IS stored (not tombstoned) in both A's and B's `Objects` tables. The object-document endpoint's visibility gate still does not resolve the followers-collection audience. This is the same class of issue as **S47** (Direct-visibility AP object URL 404) — the object-doc endpoint needs the same `IsFollowersCollection` / membership-resolution fix that the feed path now has. Tracked as a residual facet of S47.
