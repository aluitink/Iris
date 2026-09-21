# 14824 — Shared-inbox Like of a Note routes to the note's author (fixes S27)

- **Finding:** [s27](../qa/s27-like-dropped-at-shared-inbox-no-local-recipient.md) — S2-sev, cross-instance interop (Iris↔Iris).
- **Commit:** `27b1ba6` (fix + tests), on top of `c00129e` (S33 docs).
- **Date:** 2026-09-21.

## What was built

`ii-a1` (A) posts a public note. `ii-b1` (B) presses **Like** on it. B records the `Like` in its own
outbox and server-delivers the signed `Like` to the note's author. A advertises an instance-wide
**shared inbox** (`endpoints.sharedInbox` → `{base}/ap/v1/shared-inbox`, which the app always
configures), so B delivers the `Like` to that shared inbox rather than to the author's per-actor
inbox. A's `SharedInboxHandler` must read the intended recipient from the payload and route the
delivery to the matching local actor.

**The bug (S27):** the `Like` reached A's shared inbox but was **dropped**:

```
Shared inbox: no local recipient; accepting and dropping. Peer: https://qa-iris-a.luit.ink/ap/v1/u/ii-a1#key-1
```

The note's `GET …/likes` stayed empty and `likedCount` did not reflect the remote like. The activity
was accepted (202) but never dispatched, so the note's `/likes` collection never updated.

## Root cause

`SharedInboxHandler` (`ActivityPubServerExtensions.cs`, `POST /ap/v1/shared-inbox`) routes an
incoming activity to a local recipient by reading the recipient from the payload:

- a **content** activity (`Create`, `Announce`) → fan out to the author's local followers;
- an **`Undo` of a `Follow`** → the follow's target (the followee) (S33);
- **any other activity** (Follow, Accept, Reject, Tombstone, …) → its **`object`**.

For a `Like`, the `object` is the **note's IRI** — a content object, not an actor. The recipient
resolved to the note IRI, which is not a local actor, so `recipients.Count == 0` and the handler
accepted and dropped the delivery. The note's `LikeActivityHandler` (which records the like edge on a
stored local note) never ran.

The per-actor-inbox path was never affected: `OutboxPublishHandler` resolves a `Like`'s recipient as
the object's **owner** (its `attributedTo`) via `RecordLikeLocalAsync` →
`ResolveObjectOwnerForDeliveryAsync`, and delivers to the owner's inbox. So a `Like` delivered
directly to the author's per-actor inbox is applied; only the **shared inbox** (the path the app
advertises) mis-resolved the recipient.

## The fix

`SharedInboxHandler` now special-cases a `Like` (detected via the `typedActivity is Like` pattern,
after the content and `Undo` branches): it resolves the object's owner (its `attributedTo`) with the
existing `ResolveObjectOwnerForDeliveryAsync(persistence, null, objectIri, ct)` helper — a local
object's owner is read from the object store (no wire hop) — and adds that owner as the recipient.
The routed recipient (the note's author) is a local actor, so `HandleInboxPostAsync`'s `exists` check
passes and the delivery proceeds to the author's `LikeActivityHandler`, which records the
`liker → note` edge (the note's `/likes` collection and `likedCount` now reflect the like).

When the owner cannot be resolved (a note not stored locally, or no `attributedTo`), the helper
returns the object IRI as a best-effort fallback; that IRI is not a local actor, so the existing
`recipients.Count == 0` check accepts and drops it as before. A `Like` of a **remote** note is
therefore still dropped on this instance (correct — the like edge is recorded on the note's home
instance, not here).

**`Announce` is intentionally left on the content fan-out branch** (`typedActivity is Create or
Announce`), unchanged: it fans out to the author's local followers, mirroring the per-actor-inbox
behavior, and the existing `Announce_DeliveredToSharedInbox_FannedOutToFollowersAndStored` test
depends on that routing. S27's finding scopes the defect to `Like` specifically ("Announce is NOT
affected by S27 — the remote Announce is received + accepted").

This is backward-compatible: the change only corrects *which local actor* the shared inbox routes a
`Like` to; `Follow`/`Announce`/`Create`/`Undo` routing is untouched.

## Tests

`tests/Iris.Server.Tests/Security/SharedInboxIntegrationTests.cs` (+1, +1 `BuildLike` helper):

- `LikeOfLocalNote_DeliveredToSharedInbox_RoutesToAuthorAndRecordsLikeEdge` — local `bob` (hosted by
  B) has a note stored in B's object store; remote `alice` likes it via the shared inbox over the
  wire; asserts B stored the `Like` (not dropped) **and** recorded the `alice → note` like edge
  (`Likes.HasLikedAsync`).

It **fails without the fix** (the `Like` is dropped — `TryGetActivityAsync` returns false — verified
by stashing the fix) and **passes with it**. The existing 5 shared-inbox tests (Follow, Announce
fan-out, Follow-to-remote dropped, two Undo-of-Follow S33 tests, Create non-local dropped) still pass
— the `Follow`/`Announce`/`Create`/`Undo` routing is unchanged.

**Fast suite:** `Iris.Server.Tests` **1413 pass, 0 fail** (was 1412; +1). Web **108 pass, 0 fail**.

## Live re-verification

Deferred to QA on the two-instance federation stack (dev does not run it). Re-verify from a clean
entry with a **signed CLI/AP client** (the browser cannot forge the signed outbox POST, and the UI
blocks liking remote objects — per S27's re-test note): `ii-a1` (A) posts a public note; `ii-b1` (B)
likes it. A log shows the `Like` **received and dispatched** (NOT "no local recipient; accepting and
dropping"). `GET A <note>` → `likes` includes `ii-b1` (`likedCount` reflects it).
