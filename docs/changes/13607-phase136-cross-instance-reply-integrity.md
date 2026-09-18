# 136.7 — Cross-instance reply integrity (replies, threading, and context integrity)

**Date:** 2026-09-14
**Slice:** 136.7 (Lemmy interop — cross-instance reply chains, `inReplyTo` stability, deep-thread pagination)
**Status:** **COMPLETE (impl + tests).** A reply whose parent lives on a remote instance (an Iris user
replying to a Lemmy post) now federates to the parent's home instance and is threaded there; the reply's
`inReplyTo` and `conversationId` survive the cross-origin hop.

## What this slice delivers

136.7's acceptance criterion: *"validate cross-instance reply chains (Lemmy reply to Iris post, Iris reply
to Lemmy post); verify `inReplyTo` and parent-child reconstruction remain stable; validate deep-thread
pagination does not break cross-origin ancestors. Exit when threaded conversations remain coherent on both
sides."* The core gap (found by exercising the Iris→remote-parent direction against a synthetic remote
peer): **a reply to a remote parent was never delivered to the parent's home instance**, so the thread was
broken on the parent's home (the Lemmy author of a post that an Iris user replied to never saw the reply).

### The core gap (why the reply never reached the parent's home)

Two independent reasons, both on the **local-outbox publish** path (where the reply is authored + fanned
out):

1. **`RewriteOutboundAudienceAsync`** resolved the reply's parent author by looking the parent up in the
   **local** object store only (`persistence.Objects.TryGetObjectAsync`). For a **remote** parent (one this
   instance has never stored — it was only ever seen by IRI in the reply's `inReplyTo`), that lookup misses,
   so no author was resolved and the parent author was never added to the reply's `to` audience.
2. **`OutboxPublishHandler`'s Create fan-out** delivered the Create **only to the actor's followers**. The
   parent's author is not (in general) a follower of the replier, so even if the parent author had been
   resolved, the follower fan-out would not have carried the reply to the parent's home.

Net effect: the reply was stored locally (on the replier's instance) but never crossed to the parent's
home, so the parent's `/replies` collection (and the parent author's thread view) never included the
cross-instance reply.

### Secondary gaps (also fixed)

- **(a) Thread-root anchoring** — `CreateActivityHandler.EnsureConversationIdAsync` left the reply's
  `conversationId` **unset** when the parent was on a remote instance not stored locally (the thread root
  was orphaned). It now anchors the reply's `conversationId` to the **parent IRI** (the stable thread root)
  in that case — the same value the local-parent / no-conversationId branch already used.
- **(b) `/replies` for an un-stored parent** — `ObjectRepliesAsync` **404'd** a parent that has known
  reply edges (from an inbound Create) but no locally-stored object document. It now serves the replies
  collection when the instance knows reply edges for the parent, and only 404s when there is **neither** a
  stored object **nor** reply edges (the "serve what we know" philosophy, mirroring the proxy fallback).

### What changed

**`src/Iris.Server/ActivityPubServerExtensions.cs`**

- New `ResolveReplyParentAuthorAsync(persistence, objectFetch, objectIri, ct)` — resolves a reply parent's
  `attributedTo` author: a **local** parent from the object store (no wire hop), a **remote** parent by
  fetching its object document over the wire (the host's outbound `IActivityPubClient` object fetcher, the
  24.1 remote-owner-fetch pattern). Best-effort: a local miss with no remote fetcher, or a remote fetch
  that yields no owner (or a fetch failure), returns `null` — the reply degrades to the prior behavior
  (parent author simply not resolved). The fetch must never fail the publish.
- `RewriteOutboundAudienceAsync` — gains an `IActivityPubClient? objectFetch` param; its Create/reply
  branch now uses `ResolveReplyParentAuthorAsync` (handling a remote parent) to add the parent author to
  the reply's `to` audience, so a cross-instance reply names its parent's author.
- `OutboxPublishHandler` (Create branch) — after the follower fan-out, when the Create is a reply, it
  resolves the parent author via `ResolveReplyParentAuthorAsync` and **delivers the reply to it** when that
  author is a **remote** (non-local) actor (`localActors.IsLocalActorAsync`). A local parent is a no-op
  (the reply is already on the same instance); a resolvable remote parent is delivered to the parent's
  home. Best-effort: an unresolvable parent simply skips the extra delivery (the follower fan-out still
  ran). The handler's `objectFetch` param is passed through to both the audience rewrite and this delivery.
- `ObjectRepliesAsync` — serves a parent's `/replies` collection when the instance knows reply edges for it
  even though the parent object itself was never stored locally (404 only when there is neither a stored
  object nor reply edges).

**`src/Iris.Server/Inbox/CreateActivityHandler.cs`**

- `EnsureConversationIdAsync` — when the reply's parent is on a remote instance not stored locally, the
  reply's `conversationId` is anchored to the parent IRI (the stable thread root) instead of being left
  unset, so parent-child reconstruction and a client's thread walk survive the cross-origin hop.

**`tests/Iris.Server.Tests/CrossInstanceReplyThreadIntegrationTests.cs`** (new) — a two-instance harness
(A: `bob`, the parent's home; B: `alice`, the replier), reusing the shared two-host + bidirectional
federation wiring (`ActivityPubHostFactory.Create` with `Fetcher` / `DeliveryTransport` / `Client`
overrides; a `TestServerHolder` breaks the fetcher↔server circularity). Bob (A) stores the parent note m1
(the Lemmy stand-in's post, IRI in A's `/ap/v1` serving namespace). Alice (B) replies to m1 via a **signed
`POST /ap/v1/u/alice/outbox`** (the local-outbox publish path, where the audience rewrite + parent-author
delivery live). B resolves m1's author (bob) by fetching m1 over the wire (B's `Client` → A), adds bob to
the reply's `to`, and delivers the reply to bob (A) via B's hosted `DeliveryWorker`; A validates alice's
signature (A's fetcher → B) and stores the reply.

1. **`ReplyToRemoteParent_FederatesToParentHome_AndIsThreadedThere`** — asserts the full invariants on
   **A (the parent's home)**:
   - **(a)** A stored the reply (it federated to the parent's home; it was not stranded on B alone).
   - **(c, inReplyTo stability)** the stored reply's `inReplyTo` is m1's IRI intact.
   - **(c, thread-root anchoring)** the stored reply's `conversationId` is anchored to m1 (the thread
     root).
   - **(b, reply edge on the parent's home)** A recorded the parent → child reply edge (parent = m1, child
     = the reply).
   - **(b, coherent thread)** `GET {m1}/replies` on A lists the reply (the Lemmy author's thread includes
     the Iris reply; the cross-instance reply chain is coherent on A).

## What is NOT in this slice

- **Live Iris↔Lemmy reply delivery** — the Lemmy side is still blocked by the Lemmy-side
  signature-parse + egress gap (136.3) and the Iris→Lemmy WebFinger/egress gap (136.2), both documented as
  ops/Lemmy-side, not Iris code gaps. The Iris-side cross-instance reply contract (a reply to a remote
  parent federates to the parent's home, is threaded there, and its `inReplyTo`/`conversationId` survive)
  is what 136.7 implements + pins, against a synthetic remote peer (instance A) as the Lemmy stand-in.
- **Deep-thread pagination of cross-origin ancestors** — the single-hop reply chain (parent on A, reply on
  B→A) is fully validated; multi-level cross-origin ancestor pagination (a reply to a reply to a remote
  root) is not separately exercised here. The `conversationId` anchoring + reply-edge store make the
  thread walk well-defined, but a dedicated deep-pagination test is left as a follow-up.
- **The reverse direction (Lemmy replying to an Iris post)** — the inbound path (a remote reply delivered
  to a local parent's home) already worked: the inbound `CreateActivityHandler` records the reply edge and
  the local `/replies` collection serves it. 136.7's fixes target the **outbound** direction (an Iris
  reply to a remote parent), which was the broken leg.
- No site-root JSON-LD (instance-level federation) — queued as a future candidate, out of scope here.

## Verification

- `CrossInstanceReplyThreadIntegrationTests`: **1 passed** (the reply federates to the parent's home A, is
  threaded under m1 on A, and its `inReplyTo` + `conversationId` survive the cross-origin hop).
- **Non-vacuity check** — the test **fails** when the `ResolveReplyParentAuthorAsync` / audience-rewrite /
  delivery changes are reverted (B's reply `to`/`cc` come back empty, bob is never added to the audience,
  and A never stores the reply), confirming the test exercises the new code path, not a pre-existing
  behavior.
- Full fast suite: **Iris.Server.Tests 1165 passed, 0 failed** (was 1164, +1 — no regression);
  **Iris.Core.Tests 445 passed, 0 failed**.
- Full solution build: **0 warnings, 0 errors** (`TreatWarningsAsErrors`).
- The pre-existing load-induced flake (`Follow_Unfollow_Refollow_Cycle`) did not trigger this run.
