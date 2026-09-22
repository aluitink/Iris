# S46 — Followers-visibility note not visible to followers on other instances

- **Found:** Pass 310 (2026-09-22), build `345286cc`.
- **Severity:** S2 (data-visibility) — Followers-visibility content is not delivered to followers on other instances, breaking a core ActivityPub visibility guarantee.
- **Status:** Open (dev-owned).

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
