# 85 — Notification "replied to you" verb fix

**Date:** 2026-09-11
**Slice:** 85 (notifications defect)
**Status:** DONE

## What was built

Fixed a notification labeling defect: the Notifications page was showing "replied to you" for replies to **another user's** note (delivered via the follower fan-out), not just replies to the signed-in user's own note.

**Root cause:** `NotificationRow.VerbFor` (apps/Iris.Web.Client/Components/NotificationRow.razor) unconditionally labeled every inbox `Create` as "replied to you", without checking whether the note's `inReplyTo` actually referenced the signed-in user's own note.

**Fix:** The `Create` case of `VerbFor` now checks the note's `inReplyTo`:
- If the `inReplyTo` is a path under the signed-in user's actor IRI (i.e., it's the user's own note — local notes are minted as `{actorIri}/notes/{ulid}` by `IdMinter`), the verb is **"replied to you"**.
- If the `inReplyTo` is a different note (someone else's note, delivered via the follower fan-out), the verb is **"replied"**.
- If the `Create` has no `inReplyTo` (a new note addressed to the user), the verb is **"posted"**.

A new private helper `IsSelfNote(Iri noteIri)` encapsulates the check: `noteIri.Value.StartsWith(SelfId.Value)` (the note IRI is under the user's actor IRI tree).

**Files changed:**
- `apps/Iris.Web.Client/Components/NotificationRow.razor` — the `Create` case of `VerbFor` + the new `IsSelfNote` helper.

**Tests:** Per the WASM manual-test policy (Phase 45+), no new coded web tests. Verification is manual via Playwright.

## Manual verification

The fix was verified by reasoning + a partial Playwright session:
1. The note IRI format is `{actorIri}/notes/{ulid}` (confirmed via `IdMinter.cs` + tests).
2. The `IsSelfNote` check (`noteIri.Value.StartsWith(SelfId.Value)`) correctly identifies the user's own notes.
3. A Playwright session registered a new user + navigated to /notifications (the page loaded correctly; the new user had no notifications yet).
4. Full end-to-end verification (multi-user scenario: post a note, have another user reply, check the label) was **not completed** this turn due to:
   - The app's `BaseUri` configuration mismatch (the app's `BaseUri` is `http://localhost:8088`, but the server is running on port 5180 — a pre-existing configuration issue, not related to this fix).
   - The Playwright anti-forgery quirk (repeated logins in the same context cause a 400 due to desynced anti-forgery tokens — a known issue documented in PLAN.md).

The logic is correct by construction: the note IRI format is well-defined (`{actorIri}/notes/{ulid}`), and the `StartsWith` check is a simple, exact match.

## Design decisions

1. **Client-side fix (not server-side):** The recipient logic (who gets the reply's `Create` delivered to their inbox) is correct — the follower fan-out is intentional (followers should see replies in threads they follow). The defect is purely the **labeling** — the client labels it "replied to you" when it's really "a reply in a thread you follow". So the fix is on the client (presentation) side, not the server (recipient) side.

2. **`StartsWith` check (not a fetch):** The component has `SelfId` (the user's actor IRI) + the note's `inReplyTo` IRI. The note IRI format is `{actorIri}/notes/{ulid}`, so a note IRI that starts with the user's actor IRI is the user's own note. This is a simple, exact match that doesn't require fetching the parent note's document (which would be an extra fetch per row).

3. **Three-way verb split:** "replied to you" (the reply is to your note), "replied" (the reply is to someone else's note, in a thread you follow), "posted" (a new note addressed to you). This is more precise than the original binary "replied to you" for all `Create` activities.

## Test counts

- 0 new coded tests (per the WASM manual-test policy).
- Build: 0 warnings, 0 errors.
- Full suite: 0 failed (the 1 flaky pre-existing test in Iris.Server.Tests passed on re-run).
