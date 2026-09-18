# 13814 — Iris Reply to Lemmy Comment (138.14)

## Summary

Verified that an Iris reply to a Lemmy-sourced post (a `Page`) is correctly delivered to the parent's
home instance (Lemmy) via Phase 136.7's cross-instance reply integrity path. No new production code
was required — the existing Phase 136.7 mechanism (delivers a reply's `Create` to the parent's author
when the parent is remote) already handles the Lemmy `Page`-as-parent case correctly.

## Verification

Two two-instance integration tests (`LemmyReplyToPostIntegrationTests`):

1. **`IrisReplyToLemmyPage_DeliveredToParentHome_AndThreadedThere`** — bob (A, the Lemmy stand-in) has
   a `Page`; alice (B) replies to it with a `Create(Note)` whose `inReplyTo` points to the `Page`.
   The reply is delivered to A (the parent's home), stored as a `Note`, and the reply edge
   (Page → Note) is recorded, so A can serve it under the `Page`'s `/replies` collection.

2. **`IrisReplyToLemmyPage_CarriesInReplyTo_PageIri`** — the reply `Note` stored on A carries
   `inReplyTo` pointing to the `Page` IRI (not a `Note` IRI), confirming the reply is correctly
   anchored to a Lemmy-format parent.

Both tests pass. The full test suite is green (1230 passed, 16 skipped, 1 known flake that passes in
isolation).

## Why no production code change

Phase 136.7's cross-instance reply integrity (in `OutboxPublishHandler`'s Create branch) resolves the
parent's author from the `inReplyTo` IRI (via `GetParentIri()` + `ResolveObjectAuthorForDeliveryAsync`)
and delivers the reply to that author if the author is remote. The parent's *type* (`Page` vs `Note`)
is irrelevant to this path — it only needs the parent IRI and its author. A `Page` parent works
identically to a `Note` parent.

## Files

- `tests/Iris.Server.Tests/LemmyReplyToPostIntegrationTests.cs` (new) — 2 integration tests
- `docs/plans/phase-138-lemmy-community-integration.md` (updated) — 138.14 marked `[x]`
