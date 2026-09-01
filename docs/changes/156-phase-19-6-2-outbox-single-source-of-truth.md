# 156 — Outbox as the single source of truth: every authored activity, once, in order

> 2026-09-01 · Slice 19.6.2 (Phase 19.6 — Architectural expectations: client↔server interaction) · the outbox is pinned as the single source of truth for "what did this actor do"

## What was built

**19.6.2** asks: "Every activity a local actor/community authors appears in that actor's/community's
outbox collection (Follow, Accept, Create, Like, Announce, Undo, Delete, moderation) in a stable order;
the outbox is the single source of truth for 'what did this actor do.' Verify by enumerating the outbox
(UI + wire) after exercising every write screen and matching entries 1:1 with the actions taken."

The implementation already existed and was confirmed by reading the handlers:

- `OutboxPublishHandler` (`POST /ap/v1/u/{handle}/outbox`, `ActivityPubServerExtensions.cs`) records
  every client-authored activity in the actor's outbox **before** the type-specific work — covering
  `Create`, `Announce`, `Delete`, `Follow`, `Block`, `Flag`, `Like`, and `Undo`.
- `LocalFollowDecisionHandler` (`POST /ap/v1/u/{handle}/follows/{followId}`) records the server-produced
  `Accept` and `Reject` in the acting local actor's outbox (via `AddToOutboxAsync`).
- The community publish handler (`POST /ap/v1/c/{name}/outbox`) mirrors this for the community's
  `Follow`/`Undo`.

The activity store inserts each outbox entry at the front (`list.Insert(0, json)` — "newest first,
mirrors the in-memory store"), so the outbox collection renders in a stable, reverse-chronological order.

What was missing was a **pin**: no test exercised the *combined* "the outbox is the single source of
truth" invariant across every activity type at once. This slice adds that pin.

## Key types & files

- `src/Iris.Server/ActivityPubServerExtensions.cs` — **unchanged** (`OutboxPublishHandler` already
  records every client-authored activity in the outbox; `LocalFollowDecisionHandler` already records the
  `Accept`/`Reject`).
- `src/Iris.Server/Persistance/Stores/FileBackedActivityStore.cs` — **unchanged** (the outbox is stored
  newest-first; the in-memory store mirrors the same order).
- `tests/Iris.Server.Tests/OutboxSingleSourceOfTruthIntegrationTests.cs` — **new** (two integration
  tests; see below).

## Tests

1250 → **1252** passing (+2: the two outbox single-source-of-truth integration tests).
Full `dotnet test` green; `dotnet build` clean (`TreatWarningsAsErrors`); `dotnet format` clean on the
changed file (the pre-existing whitespace violations in unrelated test files are untouched).

- `EveryAuthoredActivity_AppearsInTheOutbox_OnceInStableOrder` — the central 19.6.2 assertion: a single
  instance hosts alice (the instance actor) plus a local bob, and a remote actor follows alice twice.
  Alice authors every supported activity type — `Follow` (bob), `Create` (a note), `Like`, `Announce`,
  `Block` (bob), `Undo` (the follow), and `Delete` (the note) via the signed outbox endpoint, plus an
  `Accept` and a `Reject` via the follow-decision endpoint. The test then enumerates alice's outbox and
  asserts it contains **exactly** that authored set — nothing more, nothing less, no duplicates — each
  once, in the store's stable order (newest-first: the `Reject` first, the `Follow` last).
- `OutboxHttpCollection_MatchesTheAuthoredSet` — a second activity is authored, and the HTTP outbox
  collection (`GET /ap/v1/u/alice/outbox`) is read over the wire and asserted to agree with the
  persistence-level outbox (both list the authored activities). This pins the collection endpoint as the
  read path over the same source of truth.

## Live verification (deferred — a live item)

The server-side invariant is pinned by the new tests (every activity type exercised; the outbox
enumerated 1:1 with the authored set). The **live** half — enumerating the outbox in the **raw
inspector (UI)** after exercising every write screen (compose, follow, like, block, delete, accept,
reject) and matching entries 1:1 with the actions taken, plus the wire read of the collection — is the
remaining live-verification item for 19.6.2. It requires the two-instance Docker environment
(dev1-public-host unreachable from CI), so it is deferred as a live item; the server-side invariant it
verifies is already covered in CI by the new tests.

## Decisions

- **The pin exercises every activity type the client can author, plus the two server-delivered
  decisions.** 19.6.2's "every activity a local actor authors" spans the client-published writes
  (`Create`/`Announce`/`Delete`/`Follow`/`Block`/`Flag`/`Like`/`Undo` through the outbox endpoint) and
  the server-produced `Accept`/`Reject` (the follow-decision endpoint). A single test that authors all
  of these and then asserts the outbox is *exactly* the authored set is the faithful expression of "the
  outbox is the single source of truth for what this actor did" — it cannot be decomposed into the
  per-handler tests (which each pin one type in isolation) without losing the "one actor, one outbox,
  the whole set" shape of the requirement.
- **`Block` and `Follow` use a local recipient (bob), not a remote hop.** The point of 19.6.2 is the
  *outbox* invariant, not cross-instance delivery. Using a local recipient keeps the test single-instance
  (no delivery worker, no routing handler, no fan-out to wait for) and focused: the activity is recorded
  in the author's outbox regardless of whether the recipient is local or remote. (The `Flag` type is
  exercised in the community-moderation tests; the `Undo` here is an un-follow.)
- **The order assertion is newest-first (the store's stable order), not authoring order.** The activity
  store inserts each outbox entry at the front (`Insert(0, ...)`), so the collection renders most-recent
  first. The test asserts the outbox equals the authored set *reversed* — this pins the stable order
  (deterministic, not insertion-order-fragile) that the requirement asks for ("in a stable order").
- **The `Delete` does not remove the original `Create` from the outbox in this test.** The `Delete`
  handler's inverse-removal only matches when the deleted object's `Create` IRI is the deterministic
  sibling of the object IRI (`{actor}/creates/{suffix}` for `{actor}/notes/{suffix}`). The test's
  `Create` uses a random IRI, so the inverse-removal is a no-op and the `Create` stays in the outbox.
  The point here is that the `Delete` *itself* is recorded as an authored activity; the `Delete`'s
  outbox-removal effect is a separate invariant already pinned by the 19.3.4 delete-propagation tests.
- **The `Delete` and `Undo` are real (not no-op) writes.** The `Undo` actually removes the follow edge
  (alice→bob) and the `Delete` tombstones the note — both are genuine state changes, so their presence in
  the outbox is a meaningful "the actor did this" record, not an artifact.
