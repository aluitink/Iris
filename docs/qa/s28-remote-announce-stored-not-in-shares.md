# S28 — Remote Announce (Boost) delivered+stored on the author, but NOT surfaced in the note's `shares`

- **Class:** bug / data-integrity — **Severity:** S2
- **Status:** open
- **Found:** Interop suite A7 (Iris↔Iris), 2026-09-20, QA federation stack (Iris A `qa-iris-a.luit.ink`, Iris B `qa-iris-b.luit.ink`)
- **Related:** [S25](s25-remote-post-not-in-followers-home-feed.md) (delivered+stored, not surfaced), [S26](s26-remote-reply-not-threaded-under-parent.md) (stored, not threaded) — same family. **Distinct from [S27](s27-like-dropped-at-shared-inbox-no-local-recipient.md):** the Announce is **received + accepted** by A (not dropped at the shared inbox as the Like is).

## Symptom

`ii-a1` (A) posted a Public note `II-A4-1 hello cross-instance` (Note IRI `…/ii-a1/notes/06GC0JFR051T63GG17JPSRW928`). `ii-b1` (B) pressed **Boost**.

**The Announce is created, delivered, and accepted:**
- B `ii-b1/outbox` → `Announce`, `actor` = `ii-b1`, `object` = the Note IRI, `id` = `…/ii-b1/announces/06GC0PAK5ZQ7T62FPW3VEM983R` ✓
- **A `qa-iris-a` log** (decisive — *not* dropped, unlike the Like in S27):
  - `Inbox received Announce https://qa-iris-b.luit.ink/ap/v1/u/ii-b1/announces/06GC0PAK5ZQ7T62FPW3VEM983R … to https://qa-iris-a.luit.ink/ap/v1/u/ii-a1`
  - `Handler AnnounceActivityHandler processed Announce … — ok`
  - `Inbox accepted: Announce from …/ii-b1 targeting …/notes/06GC0JFR051T63GG17JPSRW928. Recipient: …/ii-a1, Peer: qa-iris-b.luit.ink`

**But it is not surfaced:**
- `GET A <parent Note>` → `shares` = **empty** (count 0), even though `sharedCount` = **1** (local count is incremented) and the Announce was accepted.
- So the remote Boost is accepted/stored on the author, but the note's `shares` collection does not include the booster (the Shares tab / `shares` wire is empty on A).

## Root cause (suspected)

`AnnounceActivityHandler` accepts the remote Announce and increments the local `sharedCount`, but does not record the booster in the note's `shares` `OrderedCollection` (or the `shares` query does not join stored remote Announces). Compare S26 (`replies`) and S25 (feed): stored/accepted remote objects are not surfaced in their note-level collections. No `file:line` yet — needs a code pass on `AnnounceActivityHandler` (does it append to `shares`?) and the `shares` collection query.

## Fix (agreed approach)

- When a remote `Announce` is accepted for a **local** Note, record the announcer in that Note's `shares` `OrderedCollection` (and the Shares tab / `shares` endpoint must return it), consistent with the local `sharedCount` that is already incremented.

## Re-verify (clean entry)

1. `ii-a1` (A) posts a Public note `II-A4-1 hello cross-instance`.
2. `ii-b1` (B) presses **Boost**.
3. A log shows `Inbox accepted: Announce … Recipient: ii-a1@A`.
4. `GET A <parent Note>` → `shares` includes `ii-b1` (Shares tab shows the boost). ← the fix
5. `sharedCount` stays consistent with the `shares` items.

**Re-verification evidence (Interop A7, 2026-09-20, QA stack):** B outbox `Announce` correct (actor `ii-b1`, object = Note IRI); A log shows Announce received + processed ok + **accepted** (not dropped); `GET A <note>` `shares` = empty but `sharedCount` = 1. **S28 OPEN.**

## Re-test (fresh rebuild, 2026-09-20)

**CANNOT REPRODUCE via UI (same tooling limitation as S27).** A cross-instance `Announce`/Boost is POSTed to the **author's** outbox (`POST /ap/v1/u/{handle}/outbox`), which requires **AP-HTTP-Sign** and cannot be forged with a raw in-browser `fetch`. The **UI also blocks boosting remote objects** (`apps/Iris.Web.Client/Ui/UiContext.cs:704`), so `ii-b1` (B) has no Boost control for a remote A note to drive via Playwright. The original S28 evidence came from a signed client. To re-verify on the fresh build, repeat with a **signed CLI/AP client**. **S28 status this run: not re-testable via Playwright; OPEN (unconfirmed on fresh build).**
