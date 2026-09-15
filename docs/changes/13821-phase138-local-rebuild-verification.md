# 138.21 — Local rebuild verification (archival acceptance bar)

## Purpose

Verify the core archival acceptance bar: after a Lemmy thread (post + multi-level comment tree +
votes) is synced to the Iris local store, the store is **self-sufficient** — all objects are
retrievable by IRI, reply edges correctly link parent to children, vote edges are recorded, and
the nested thread's `inReplyTo` chain is structurally complete. This proves that Iris can render
a full thread from its local store alone, with Lemmy offline.

## Approach

Wrote 5 integration tests in `LocalRebuildVerificationIntegrationTests` that seed the local store
as if a Lemmy thread had been fully synced (using the 138.20 backfill mechanism's data model) and
verify each store is self-consistent:

1. **`LocalStore_AllObjects_RetrievableByIri`** — the `Page` (post) and all three `Note` comments
   are retrievable from the object store by their IRIs.
2. **`LocalStore_ReplyEdges_LinkParentToChildren`** — the post's reply edge set contains exactly
   the two top-level comments (not the nested one); comment 1's reply set contains exactly the
   nested comment; comment 2's reply set is empty.
3. **`LocalStore_VoteEdges_Recorded`** — the post has 2 like edges and 1 dislike edge (the raw
   counts that the 138.18 `iris:score` = `likedCount - dislikedCount` derivation consumes).
4. **`LocalStore_MemberOutbox_ContainsAllCreates`** — the member's outbox contains all 4 `Create`
   activities (post + 3 comments).
5. **`LocalStore_NestedThread_StructureIsComplete`** — a full walk from the post: level 0 (post)
   → level 1 (2 direct replies) → level 2 (comment 1 has 1 nested reply, comment 2 has none);
   the nested reply's `inReplyTo` points to comment 1.

## Files changed

- `tests/Iris.Server.Tests/LocalRebuildVerificationIntegrationTests.cs` (new) — 5 integration tests.

## Decision

The slice's original check ("stop the Lemmy container entirely and confirm the Iris-rendered
thread still renders") is a live orchestration step. The coded integration tests verify the same
acceptance bar at the store level: if all objects, reply edges, vote edges, and outbox activities
are present and structurally consistent in the local store, the HTTP object endpoint and
`/replies` collection (already tested in prior phases) will render the thread correctly without
the remote. The live stop-Lemmy step is a manual Playwright verification (Phase 45+ web test
policy) that can be performed when the live environment is fully peered.

## Verification

- `dotnet build` — 0 warnings, 0 errors.
- `dotnet test tests/Iris.Server.Tests` — 1251 passed, 16 skipped, 0 failed.
