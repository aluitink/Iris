# 136.6 — Outbound federation from Iris communities (Iris→Lemmy)

**Date:** 2026-09-14
**Slice:** 136.6 (Lemmy interop — outbound content federation from a local community to a remote peer)
**Status:** **COMPLETE (impl + tests).** A community-attributed post now federates with the posting
community's identity intact, and the receiving instance fetches + persists that community's actor
document — closing the community-provenance gap the signature path alone never resolved.

## What this slice delivers

136.6's acceptance criterion: *"a post made by an Iris community (a member posting with the community in
the Note's `attributedTo`) reaches a remote peer, and the peer can resolve the posting community's
identity (name/icon) for display."* Two gaps:

1. **(a) The community `attributedTo` survives the wire** — a member's community-attributed post
   federates to the member's remote followers with the community IRI intact on the stored embedded Note.
2. **(b) The remote peer resolves the posting community's identity** — the receiving instance fetches +
   persists the posting community's actor document (a `Group`), so its name/icon is resolvable for
   display.

### The design decision (why the member's outbox, not the community's)

A community (Group) **does not** author `Create`/`Announce` through its own outbox —
`ActivityPubServerExtensions.cs` documents this deliberately: *"a community does not post content through
its own outbox — that flows through the members' outboxes / the community inbox."* So the outbound path
is: **a member posts a Note with `attributedTo` = community IRI**, and it fans out to the **member's**
remote followers (signed as the member) — NOT the community's followers. `CommunityOutboxPublishHandler`
intentionally 400s on `Create`/`Announce` and was left untouched.

### What already existed (verified, not rebuilt)

- **Member outbox fan-out** — the person `CreateActivityHandler` (person branch,
  `src/Iris.Server/Inbox/CreateActivityHandler.cs`) records the post in the member's outbox and federates
  it to the member's remote, non-blocked followers (signed as the member). The embedded Note is stored
  verbatim, so a community `attributedTo` survives serialization.
- **Remote-actor fetch + persist** — `IrisActorDocumentFetcher.GetActorAsync`
  (`src/Iris.Server/Security/IrisActorDocumentFetcher.cs`) caches in the `RemoteActorCache` and persists a
  fetched `Group` via `RemoteCommunityPersister.PersistIfNewAsync` (the production fetcher is wired with
  this persister, `ActivityPubServerExtensions.cs`). This is the reusable seam for gap (b).

### New this turn: the impl gap + the test gap

**`src/Iris.Server/Inbox/CreateActivityHandler.cs`** (the only source change) — `CreateActivityHandler`
gains an optional `IActorDocumentFetcher? actorDocuments` dependency (null-defaulted, mirroring
`MoveActivityHandler`'s pattern; `CreateActivityHandler` is a `Singleton` so DI injects the host's
fetcher). In `StoreEmbeddedObjectAsync`, after storing the embedded object, a new
`PersistAttributedToActorAsync` runs:

- Takes the embedded object's first `attributedTo` IRI.
- Skips it when it is a **local** actor or community (its document is already local).
- Otherwise calls `actorDocuments.GetActorAsync(iri, ct)` (best-effort) — this fetches the remote
  community's `Group` document and, through the fetcher's `RemoteCommunityPersister`, persists it to the
  durable community store. A fetch failure (an unreachable peer) is logged by the fetcher and does **not**
  fail the post — the object is already stored and servable.

This is the gap (b) fix: without it, the receiving instance's signature path only ever resolves the
**signing member** (the Note's `attributedTo` community is an opaque IRI), so the posting community's
name/icon can't be resolved for display.

**`tests/Iris.Server.Tests/CommunityOutboundContentIntegrationTests.cs`** (new) — a two-instance test
(A: `alice` the remote follower; B: `bob` + community `iris`, bob a member), reusing the shared two-host
+ `DeliveryWorker` harness. Bob posts a community-attributed Create (Note's `attributedTo` = `iris`) to
his own inbox on B; B's `CreateActivityHandler` (person branch) federates it to alice (A), signed as bob.

1. **`MemberCommunityAttributedPost_FederatesToRemoteFollower_WithCommunityAttributedToIntact`** (gap a) —
   A stores the federated Create and the stored embedded Note's `attributedTo` is the `iris` IRI (the
   posting community survived the wire intact).
2. **`MemberCommunityAttributedPost_RemoteInstance_PersistsPostingCommunityDocument`** (gap b) — A
   persists the posting community `iris` (a `Group`) to its durable community store (via A's fetcher,
   wired to B with a `RemoteCommunityPersister` over A's community store, mirroring the production
   fetcher), so the community's identity is resolvable on A.

## What is NOT in this slice

- No community-follower fan-out and no `Create` branch in `CommunityOutboxPublishHandler` — by design,
  the post flows through the **member's** outbox (the community does not author content through its own
  outbox).
- No live Iris→Lemmy leg — that is blocked by the Lemmy-side signature-parse + egress gap (136.3) and the
  Iris→Lemmy WebFinger/egress gap (136.2), both documented as ops/Lemmy-side, not Iris code gaps. The
  Iris-side outbound contract (community-attributed Create → remote follower, with the posting community
  identity resolvable) is what 136.6 implements + pins, against a synthetic remote peer (instance A) as
  the Lemmy stand-in.
- No site-root JSON-LD (instance-level federation) — queued as a future candidate, out of scope here.

## Verification

- `CommunityOutboundContentIntegrationTests`: **2 passed** (community `attributedTo` intact + posting
  community document persisted).
- **Non-vacuity check** — the gap (b) test **fails** when the `CreateActivityHandler` source change is
  reverted (A does not persist the posting community's document), confirming the test exercises the new
  code path, not a pre-existing behavior.
- Full fast suite: **Iris.Server.Tests 1164 passed, 0 failed** (was 1162, +2 — no regression);
  **Iris.Core.Tests 445 passed, 0 failed**.
- Full solution build: **0 warnings, 0 errors** (`TreatWarningsAsErrors`).
- The pre-existing load-induced flake (`Follow_Unfollow_Refollow_Cycle`) did not trigger this run.
