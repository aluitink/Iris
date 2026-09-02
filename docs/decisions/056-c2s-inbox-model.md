# Decision 056 — The C2S inbox: a persisted, owner-only, per-actor inbox collection

## Context

Phase 20.2 asks: **how does a local client user browse their own inbox in a client-to-server (C2S)
scenario, and how is that content accessible to the browser?** The investigation (change 161r) found the
current inbox is **write-only**: inbound federation is accepted at `POST /{handle}/inbox`, stored in the
activity store, and the per-type handlers record the content into the **recipient's outbox** (there is no
`GET /inbox`, no inbox query on `IActivityStore`, and no fetch-on-encounter of remote content). A local
client user therefore **cannot** read "what was delivered to me" as a distinct surface — the only per-actor
activity collection is the outbox, which conflates "I authored" with "I received."

This decision resolves the five 20.2 sub-questions and defines the inbox model the implementation follows.

## The decision

### The inbox is a first-class, persisted, per-actor collection (distinct from the outbox)

An **inbox** is the set of activities **delivered to** an actor, as opposed to the **outbox**, which is the
set the actor **authored**. They are different concepts and must not be conflated:

- **Outbox** (existing): `AddToOutboxAsync(actor, activity)` / `GetOutboxAsync(actor)` — what the actor
  authored (their posts, follows, likes, boosts, and the server-recorded echoes of federated content they
  chose to surface).
- **Inbox** (new): `AddToInboxAsync(actor, activity)` / `GetInboxAsync(actor)` — what was delivered to the
  actor by other actors/instances (inbound Creates, Follows, Likes, Announces, etc.).

The inbox is recorded **once, on first delivery**, by the `InboxProcessor` (the single owner of "receive an
activity") — the same place that already does the add-if-absent `TryAddActivityAsync` (C-07 idempotency).
Recording into the inbox is gated on `firstDelivery` so a re-delivered (duplicate) activity does not
duplicate the inbox entry — the same loop-safety guard that prevents re-fan-out.

**Why a separate collection rather than reading the outbox or the activity store:**
- The outbox conflates authored + received and is the *authoring* surface (its wire contract, confirmed in
  20.1 decision (e), is "the actor's posted `Create`s"). Overloading it with "received" would break that
  contract and make the actor's own feed ambiguous.
- `GetAllActivitiesAsync` is instance-wide and unfiltered — not per-actor, and not "delivered to me."
- A dedicated inbox is the ActivityPub-standard shape (`{actor}/inbox` is a collection every AP server
  advertises; it is currently *write-only* on Iris). Making it readable completes the AP surface and is
  what a remote peer / the local client expects to exist.

### (a) Browser access — owner-only, Basic-authenticated GET

The inbox is **private** (the actor's delivery surface; it may contain DMs, direct notes, inbound follows
from strangers, moderation actions). It is **not** served on the public collection GET path (outbox /
followers / liked / … are public; an inbox must not be). It is read via an **owner-only**
`GET /ap/v1/u/{handle}/inbox` that requires the **owner's Basic auth** — the same
`IActorCredentialValidator.TryValidateAsync(actorIri, authorization, ct)` seam the actor-document endpoint
uses to gate the owner-only `privateKey` extension. The browser already holds the logged-on user's Basic
credentials (from log-on), so an owner-only GET is natural and requires no new auth mechanism.

- **Authenticated (owner):** `200` + the paged `OrderedCollection` of the inbox items.
- **Unauthenticated / wrong owner:** `403` (the collection exists but the requester is not the owner).
- **Unknown actor:** `404`.

The response is **not cached** through the `LocalCollectionPageCache` (private, owner-scoped data — the
same no-store treatment as the owner-only actor document). It is paged via `?page=N`/`?limit=N` like the
other collections.

**Why Basic auth (not a signed GET or a bearer token):** the C2S browser client authenticates by Basic-authing
the actor document to obtain `privateKey` and then *signing writes*. Reads of the actor's own private data
are already done as owner-authenticated requests (the `privateKey` fetch); the inbox read is the same
pattern. A signed GET would require the browser to hold + use its private key for reads (it does, but it is
unnecessary complexity for a same-user read); Basic auth reuses the existing, tested credential seam.

### (b) Content + attachments — store the inbound object locally (verbatim); serve remote attachments verbatim (link out) in this slice

When an inbound activity arrives carrying an embedded object (a federated `Create`'s `Note`), the server
**already stores the object locally** in the object store under the **originator's id** (decision 055:
inbound keeps the originator's id verbatim; `CreateActivityHandler.StoreEmbeddedObjectAsync`). That stored
object **is** the high-fidelity local copy the inbox view renders.

For **attachments** (an `Image`/`Document` whose `url` points at a **remote** host): in this slice the
attachment is stored and served **verbatim** (the browser links out to the remote URL). **Local media
download + URL-rewrite** (storing the bytes locally and rewriting the `url` to a same-origin `/media/…`
path so the browser never hits a cross-origin media host) is **explicitly deferred to 20.4 (media)** — it
is orthogonal to the inbox-collection foundation and is its own vertically-complete slice with its own
tests. Deferring it keeps this slice coherent and finishable; the decision records it as the staged plan.

**Rationale for link-out now:** media rewrite needs a media store, a media-serving route, and a URL
rewrite in the object serializer — a substantial, self-contained body of work that 20.4 scopes and tests.
Bundling it into the inbox foundation would make an unfinishable change. The inbox works (and is browsable)
with link-out attachments; 20.4 upgrades attachments to local copies.

### (c) Id rewrite — no rewrite; inbound objects keep the originator's id

Inbound objects keep the **originator's id** verbatim (decision 055). The inbox yields the received
activities/objects **by their originator id**; a request for that id serves the **local copy** already
stored in the object store (`ObjectDocumentHandler`). **No local-id rewrite** is introduced: rewriting ids
would break the federation invariant that an object's id is globally unique and owned by its originator,
would duplicate the object under two ids, and is unnecessary — the local copy is served *at* the originator
id, so the browser resolves the local copy without any rewrite.

### (d) Reply graph + like/boost sync — out of scope for this slice; recorded as follow-up

The inbox surfaces the **activities** (a received note, a received like, a received boost). Building the
**reply thread tree** (currently only a single `inReplyTo` link exists) and **accumulating per-object
like/boost counters** (currently `ILikeStore` is liker-side only; there is no `likedBy` index or counter)
are separate, self-contained concerns. They are **not** part of the inbox-collection foundation and are
recorded as follow-up work (the inbox view can derive a count by scanning `GetAllActivitiesAsync` filtered
by `object` IRI as an interim, but a proper per-object counter index is the clean solution). This slice does
not add them.

### (e) Pull-on-encounter fidelity — store-on-receive (the current behavior); no TTL for the inbox

Inbound objects are **persisted to the object store on receive** (a durable local copy — higher fidelity
than a TTL cache, which would lose content on eviction). The inbox **collection** is served from the store
on every request (no cache, owner-scoped). **Fetch-on-encounter** (proactively pulling a remote object
referenced *inside* an inbound activity — e.g. a note whose `inReplyTo` parent is remote — into the local
object store) is **not** done in this slice: it is a deeper fidelity enhancement with its own fetch
plumbing, and is recorded as follow-up. The store-on-receive model already gives a high-fidelity local copy
of everything *delivered*; fetch-on-encounter extends that to *referenced-but-not-delivered* objects.

## Consequences

- **New store surface** on `IActivityStore`: `AddToInboxAsync(actor, item)` (idempotent by IRI, newest
  first, mirroring the outbox) + `GetInboxAsync(actor)`. Implemented in `InMemoryActivityStore` and
  `FileBackedActivityStore` (a new `inbox` section in the file).
- **`InboxProcessor`** records the delivered activity into the recipient's inbox on first delivery.
- **New route** `GET /ap/v1/u/{handle}/inbox` (owner-only Basic auth; paged; no-store).
- **New client method** `GetInboxItemsAsync(actor, …)` (and the `IActivityPubClient` surface) — reads the
  owner's inbox (the browser carries the owner's Basic auth).
- The outbox contract (20.1 decision (e)) is **unchanged** — the inbox is additive, not a redefinition.

## What is deliberately NOT in this decision's first implementation (staged plan)

1. **Local media storage + URL-rewrite** for remote attachments → **20.4 (media)**.
2. **Reply thread tree** building → follow-up (the inbox surfaces activities; threads are derived later).
3. **Per-object like/boost counters** (a `likedBy`/`announcedBy` index) → follow-up.
4. **Fetch-on-encounter** of referenced-but-not-delivered remote objects → follow-up.

These are each self-contained, testable slices; the inbox-collection foundation (this decision) is the
prerequisite for all of them.

## Alternatives considered

- **Read the outbox as the inbox** — rejected: conflates authored/received; breaks the outbox contract.
- **A public `GET /inbox`** — rejected: the inbox is private (DMs, inbound follows, moderation).
- **Signed-GET inbox reads** — rejected: unnecessary for a same-user read; Basic auth reuses the existing,
  tested owner-auth seam.
- **Local-id rewrite of inbound objects** — rejected: breaks the 055 global-id invariant; the local copy is
  already served at the originator id.
- **TTL cache for the inbox** — rejected: the store-on-receive model is more durable; a TTL would lose
  content on eviction.
