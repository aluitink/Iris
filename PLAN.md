# Iris — ActivityPub .NET Libraries

A set of .NET libraries for ActivityPub, designed to be embedded in existing apps and services.

**This file is the single live operating document.** It is the only thing an autonomous agent needs to read to know what's happening now and what's next; everything else in `docs/` is either a rarely-touched reference or a write-once archive. See [docs/reference/AUTONOMOUS_LOOP.md](docs/reference/AUTONOMOUS_LOOP.md) for the loop this file drives.

## Documentation

| File | Contents | Read cadence |
|---|---|---|
| **PLAN.md (this file)** | Now / Active Slice / Up Next / Inbox / Paused Questions / Recently Completed | every turn |
| [docs/ROADMAP.md](docs/ROADMAP.md) | Append-only ledger of completed phases (one line each) | only to replenish "Up Next" or recall history |
| [docs/changes/](docs/changes/README.md) | One document per slice/change — the detailed build notes | write on completion; read rarely |
| [docs/decisions/](docs/decisions/README.md) | Substantial design decisions | write when a decision has real weight; read rarely |
| [docs/phase-notes/](docs/phase-notes/README.md) | Phase rationale and test-count notes | archival |
| [docs/plans/](docs/plans/) | Deep-dive scope docs for multi-turn workstreams (e.g. [phase-22-closeout.md](docs/plans/phase-22-closeout.md)) | read when picking up that workstream |
| [docs/reference/ARCHITECTURE.md](docs/reference/ARCHITECTURE.md) | Design principles, solution layout, cross-cutting concerns | reference |
| [docs/reference/PROJECTS.md](docs/reference/PROJECTS.md) | Per-project details for Iris libraries | reference |
| [docs/reference/TESTING.md](docs/reference/TESTING.md) | Integration-first testing strategy | reference |
| [docs/reference/CODING_STYLE.md](docs/reference/CODING_STYLE.md) | Binding conventions and ActivityStreams rules | before every coding turn |
| [docs/reference/AUTONOMOUS_LOOP.md](docs/reference/AUTONOMOUS_LOOP.md) | Operating instructions and doc-maintenance rules | every turn |

## Short version

- Clean, focused abstractions; no framework lock-in beyond .NET.
- One client, two directions: a single `net10.0` client used by both client apps and server-to-server flows.
- Server capability added to ASP.NET Core via `IServiceCollection` and `IApplicationBuilder` extensions.
- ActivityStreams is provided by `KristofferStrube.ActivityStreams`; Iris adds identity, signing, validation, and IRI helpers on top.
- Actor-keyed auth allows a client to fetch an actor document, then sign requests using that actor's private key.
- Community-aware by default, with Group-like actors and unified feed/collection APIs.
- API access uses versioned routes under `/ap/v1/...` and `iris:`-namespaced capabilities.
- Integration-first test model with multi-instance `TestServer` harnesses, not a sprawling unit-test suite.

## Solution layout

```text
Iris.slnx
├── src/
│   ├── Iris.Core/                  net10.0 — identity, keys, signatures, IRI, caching abstractions
│   ├── Iris.Client/                net10.0 — HTTP client, signing, auth, proxy fallback, paged collections
│   ├── Iris.Client.Extensions/     net10.0 — DI/runtime integration for client apps
│   ├── Iris.Server/                net10.0 — ASP.NET Core endpoints, middleware, community feeds
│   ├── Iris.Server.InMemory/       net10.0 — in-memory persistence implementation
│   └── Iris.WebCrypto/             net10.0 — browser/WebCrypto signing support
├── tests/
│   ├── Iris.Testing/               shared multi-instance test harness
│   ├── Iris.Core.Tests/            ├── Iris.Client.Tests/            ├── Iris.Client.Extensions.Tests/
│   ├── Iris.Server.Tests/          ├── Iris.LiveInterop.Tests/       ├── SampleServer.Tests/
│   └── SampleBlazorClient.Tests/
├── samples/
│   ├── SampleServer/               minimal ASP.NET Core host
│   └── SampleBlazorClient/         sample explorer using Iris.Client
└── tools/
    └── IrisSigner/
```

## Conventions

- Target framework: `net10.0` everywhere.
- `System.Text.Json` is the serialization surface; ActivityStreams/ActivityPub objects come from the third-party package model.
- Dependency flow: `Iris.Core` -> ActivityStreams + BCL; `Iris.Client` depends on `Iris.Core`; `Iris.Server` depends on `Iris.Core` + `Iris.Client` + ASP.NET Core.
- Caching is explicit: every read path that is cached exposes a `bypassCache` escape hatch.
- Versioned endpoints and `iris:` capability terms are authoritative.
- Testing is integration-first and browser-assisted where UI behavior matters.

## Test runs (fast vs. full)

- **Fast (default for the loop):** `dotnet test --filter "Category!=Slow"` — excludes the slow tests (those that wait out a real delivery backoff budget). Use this for the everyday "is it green?" check.
- **Full (source of truth):** `dotnet test` — every test including the slow ones. Use for the final green check before a phase closes.
- To mark a test slow: `[Trait(TestCategories.Category, TestCategories.Slow)]` (constants in `Iris.Testing.TestCategories`). Only mark tests that wait on real wall-clock time. Details + honest payoff note: [docs/reference/TESTING.md §Running the suite: fast vs. full](docs/reference/TESTING.md#running-the-suite-fast-vs-full).


### Loop protocol

> **CRITICAL RUNTIME RULE FOR WEBPAGE ACCESS:** You must prevent stale data or frontend caching on every action. For every new task, navigation, or data-refresh step, execute a **hard reload / cache bypass** *before* reading any data or evaluating elements — navigate with the `networkidle` lifecycle wait and do not proceed until all fresh server network requests have completely settled. Never rely on previously opened states, in-memory page references, or a cached (content-hash) Blazor WASM; when re-verifying after a rebuild, use a fresh browser context (close/reopen).

Each slice is a **Playwright-driven pass**, not a code-first slice.

1. **Build**: `cd /workspace && dotnet build apps/Iris.Web/Iris.Web.csproj -c Release`
2. **Docker**: `cd /workspace/apps/Iris.Web && docker compose build iris-web && docker compose up -d --force-recreate iris-web` — **avoid `--no-cache`** (repeated no-cache fills the host disk; if the build fails with `No space left on device`, run `docker builder prune -af` first). For pgAdmin, start the dev layer instead: `docker compose -f apps/Iris.Web/docker-compose.yml -f apps/Iris.Web/docker-compose.dev.yml up --build -d`.
3. **Clean entry (every slice, every re-verification)**: close the browser entirely, clear cookies + storage, reopen, enter the app fresh. Never carry state between slices or between a defect and its re-verification.
4. **Manual test (MCP Playwright)** at `https://iris.luit.ink`:
   - **Primary account: `andrew` / `Password1`** (has real content + external contacts — use it to evaluate every page).
   - **Secondary accounts**: `bob`, `carol`, `dave` (register as needed) for multi-account flows (follows, communities, moderation, notifications).
   - **Authless pass**: every page visited signed-out — verify gating (302 to login), no data leaks, no console errors, sensible signed-out UI.
    - **Work the page inventory, not "every page"**: the tracker's **Page coverage** table lists all 18 routes. Work it top-to-bottom; mark each route's signed-in + authless boxes as done, or `skipped(<reason>)`. Update the tracker's **Resume checkpoint** at the end of the slice so the next slice continues where this one stopped — never restart from scratch.
    - **Deep dive per page**: exercise every control, every state (empty/populated/error), deep links + hard refresh on each route.
    - Capture **console errors** (`browser_console_messages`). For screenshots, call the Playwright screenshot tool **with no `filename`** (it can't write to an arbitrary path); it auto-saves to `tmp/.playwright-mcp/page-<ts>.png` and returns that path — cite the returned path in the tracker rather than promising to attach a file.
     - **Network watch — count calls, not bytes (always on)**: the point is to catch **request spam** — duplicate/redundant calls fired when a page or control loads (same fetch twice on mount, a refetch on re-render, an N+1 fan-out). For each distinct request pattern record: method+path, status, **how many times it fired (the count is the spam signal)**, and *what triggered it* (which load/control).
     - **MCP Runs in docker** - If the MCP server seems down, stuck, or returns stale/cached page state for any reason, restart it: `bash scripts/start-playwright.sh` (recreates the `playwright-mcp-service` container with caching fully disabled, `--isolated`, on host port 8931).
5. **Triage**: log every finding (page, repro, expected vs actual, **class** + **severity**) in the **shared tracker**. Class is the routing axis (blocker → current phase's blocker slice, bug → current phase's fix slice, UX → a later UX phase, perf → a later efficiency phase); severity (S1/S2/S3) sets priority *within* the class. Set both on every row.
6. **Fix in scope**: implement fixes for this slice's assigned defects; **re-verify each fix from a clean entry** (step 3) and record the evidence (`console-clean + <control/state> works`, optionally + the auto-saved screenshot path) before flipping Status to `fixed`. No evidence, no `fixed`.
7. **Web tests**: `cd /workspace && dotnet test --no-build -c Release` — keep passing tests; **delete** any test broken by the change; **skip/comment out** any single test >15 s (find offenders via `dotnet test tests/Iris.Web.Tests -v n --logger "console;verbosity=detailed"` per-test timings). No new coded tests. **Every deleted or skipped test is logged** (test name, action, reason, restore-by) — no silent deletions; the phase's closeout reviews the ledger.
8. **Update PLAN.md**: move the finished slice to Recently Completed; keep Up Next sorted by priority (blockers first). At phase closeout: distill the next-next phase's topics into this file.

## Up Next

- **136.16 Search and discoverability checks**
    - Confirm federated communities and posts become discoverable in both UIs after handshake + first delivery.
    - Validate direct URL deep links resolve for remote posts/comments without requiring prior local cache.
    - Measure discovery lag (publish to searchable/visible) and record expected eventual-consistency window.
    - Exit when operators can reliably find remote communities/content by name or URL on both sides.

- **136.17 Duplicate/replay defense validation**
    - Re-send identical activities (same ID/signature window) and confirm strict idempotent handling.
    - Replay near-expiry and expired signed requests to verify acceptance/rejection boundaries are enforced.
    - Re-order benign headers in equivalent signed requests to confirm canonical validation is robust, not brittle.
    - Exit when replay attempts do not create state duplication and all decisions are observable in logs.

- **136.18 Privacy and visibility policy alignment**
    - Validate visibility mapping (public/unlisted/restricted where supported) from source intent to remote presentation.
    - Verify non-public or scope-limited content is not leaked through timeline APIs, deep links, or backfill.
    - Confirm mismatched visibility capabilities degrade safely with explicit operator notes.
    - Exit when visibility semantics are documented and enforced without over-sharing.

- **136.19 Data lifecycle and tombstone retention**
    - Define retention/expiry expectations for tombstones and deleted remote references in Iris.
    - Validate behavior when old remote links are revisited after delete propagation (UI, API, cache, and logs).
    - Confirm retention policy does not reanimate deleted content during re-sync/backfill.
    - Exit when delete lifecycle outcomes are deterministic and operator-documented.

- **136.20 Final release federation gate**
    - Create a concise go/no-go checklist referencing mandatory green scenarios across all prior phases.
    - Mark known interop gaps as explicit release exceptions with severity, impact, and workaround.
    - Require sign-off evidence artifacts (trace IDs, screenshots, console-clean confirmations) per critical flow.
    - Exit when a release can be approved or blocked using this gate without ad hoc interpretation.

## Inbox

- *(empty)*

## Paused Questions

- *(empty)*

## Recently Completed

- **136.15 Pagination and backfill consistency** — three cross-instance integration tests
  (`CrossInstancePaginationIntegrationTests`, two-instance `TestServer` fixture: A
  `page-a.domain.local` alice with a 50-post outbox, B `page-b.domain.local` lumen community
  with `FeedOptions.PagesPerActor=3, MaxItems=60`) verify that federated community content
  pages correctly across instance boundaries and that the historical backfill window includes
  the expected post count with stable ordering: A's outbox paginates correctly (page 1
  `OrderedCollection` with `first`/`next`, page 2 `OrderedCollectionPage` with
  `startIndex=21`/`prev`/`next`/`partOf`, 20 items per page newest-first); B's community feed
  includes all 50 posts from alice's outbox (3 pages × 20 capacity); the feed returns the same
  items in the same order across repeated reads (with and without `?refresh=true`). Key design
  decision: the `ICommunityFeedService` is registered by `AddActivityPubServer` with a factory
  that creates its own `IActivityPubClient` via `IActivityPubClientFactory.Create()` with a real
  `HttpClientHandler` (not the DI-registered client), so overriding `IActivityPubClient` in DI
  has no effect — the fix is to override `IActivityPubClientFactory` in a new `PreServices`
  property (runs before `AddActivityPubServer`) with a `RoutingClientFactory` that creates
  clients routed to A's `TestServer` with `Caches = null`. Cache-bypass testing (new remote post
  appears after `?refresh=true`) is deferred: the feed endpoint is not served through the
  `LocalCollectionPageCache` (it re-walks remote outboxes on every request), so `?refresh=true`
  is a no-op; the `IActivityPubClient`'s `CollectionPageCache` is the relevant cache and
  clearing it on `?refresh=true` is a production change for a separate slice. 136.16 is now the
  top of Up Next. →
  [docs/changes/13615-phase136-pagination-backfill-consistency.md](docs/changes/13615-phase136-pagination-backfill-consistency.md)

- **136.14 Media and attachment interoperability** — six cross-instance integration tests
  (`CrossInstanceMediaInteropIntegrationTests`, two-instance `TestServer` fixture) verify that image,
  link, and rich-text attachment payloads survive an Iris→Iris federation round-trip without silent
  drops or truncation: an `Image` attachment's id survives + the media proxy serves fetched bytes
  with the correct content-type; a dead image URL does not drop the object (proxy returns 502); a
  `Document` link attachment survives with name/type intact; a note's HTML content survives; a
  100 000-character body is stored in full; a MIME-mismatched attachment is stored + the proxy
  serves the actual fetched type. Key finding: the embedded object is stored under its original IRI
  (author's host), so the receiving instance's object-document endpoint (which reconstructs IRIs
  from its own BaseUri) 404s for cross-instance objects — the tests verify via the receiving
  instance's persistence directly (the production read path) and the media proxy via HTTP
  (host-agnostic). 136.15 is now the top of Up Next. →
  [docs/changes/13614-phase136-media-attachment-interop.md](docs/changes/13614-phase136-media-attachment-interop.md)

- **136.13 Regression harness + closeout checklist** — produced the standing Lemmy interop regression
  checklist (`docs/reference/LEMMY_INTEROP_REGRESSION_CHECKLIST.md`): 68 checkable items across 8
  categories (discovery, auth/signatures, peering, delivery, threading, lifecycle, moderation,
  observability), each mapped to its authoritative in-process two-instance integration test. Includes
  a pass/fail matrix (release gate rule: GO when every FAIL is empty or has a documented exception),
  10 known incompatibilities/Lemmy-specific notes (K1–K10) with severity + workaround (Lemmy
  signature-parse gap, WebFinger egress gap, Lemmy group semantics, vote-score limitation, nginx 400,
  EdDSA gap, etc.), and the automated regression gate (test-class-to-category mapping + `dotnet test`
  as the primary gate). The checklist is executable by another operator: run `dotnet test` for the
  automated gate, execute LIVE items against a running topology for the secondary gate, record
  outcomes in the pass/fail matrix. 136.14 is now the top of Up Next. →
  [docs/reference/LEMMY_INTEROP_REGRESSION_CHECKLIST.md](docs/reference/LEMMY_INTEROP_REGRESSION_CHECKLIST.md)

- **136.12 Performance and request-spam audit** — audited federation delivery/fetch patterns
  across outbox publish, follow-feed hydration, community-feed hydration, public-feed hydration,
  thread/replies hydration, remote-object fetch caching, and activity-store sweeps. Fixed the
  highest-impact finding (F-136.12.8): the `/likes` and `/shares` endpoints previously called
  `GetAllActivitiesAsync` once per liker/announcer (O(k × total_activities)); now a single sweep
  (O(total_activities)) + in-memory `Dictionary<Iri, IObjectOrLink>` lookup. Triaged remaining
  findings: F-136.12.7 (outbound client without `Caches` — perf follow-up), F-136.12.1 (duplicate
  remote object fetch per publish — perf follow-up), F-136.12.3/5 (O(follows × pages) uncached
  outbox page GETs — perf follow-up), F-136.12.2 (O(followers+relays) serialized delivery jobs —
  perf/design), F-136.12.6 (client-side per-reply object GET — inherent to AP model), F-136.12.10
  (no read-side request metrics — observability follow-up). 2 new integration tests pin the
  full-activity-document resolution for `/likes` and `/shares` with multiple likers/announcers.
  Iris.Server.Tests 1177 passed (+2), 16 skipped, 0 failed.
  136.13 is now the top of Up Next. →
  [docs/changes/13612-phase136-performance-request-spam-audit.md](docs/changes/13612-phase136-performance-request-spam-audit.md)

- **136.11 Delivery reliability, retries, and dead-letter handling** — verified the core retry/dead-letter
  logic in `DeliveryWorker` is **already correct** (exponential backoff, 4xx=permanent/5xx+429=transient,
  Retry-After delay-seconds, dead-letter store, circuit breaker) — no change needed there. Pinned three
  genuine gaps with new integration tests: (1) **idempotency across retries in a real topology** — a
  re-POSTed delivery (simulating at-least-once retry) is stored exactly once (C-07:
  `TryAddActivityAsync` dedupes by IRI; `AddToInboxAsync` is idempotent); (2) **end-to-end dead-lettering
  in a real topology** — a real two-instance topology where A's delivery to B's inbox always fails (500);
  the worker retries (5 attempts, default backoff 1s+2s+4s+8s ≈ 15s) and dead-letters the job with the
  correct inbox, actor, kind, detail, and attempt count; also unskipped the pre-existing
  `DeliveryDeadLetterIntegrationTests` test (root cause: initialization-order bug — A was created before B,
  so the `LazyHandler` did not have its inner handler ready); (3) **Retry-After HTTP-date form** — a 429
  response with a `Retry-After` HTTP-date header (3s in the future) is honored (the worker waits until the
  specified date before retrying; the gap is ≥ 2000ms, well above the zero-base-delay backoff).
  `DeliveryReliabilityIntegrationTests` (two-instance, A: alice, B: bob; `RoutingFetcher` for key
  resolution; `FailingInboxHandler` for the dead-letter test; `RetryAfterHttpDateHandler` for the
  HTTP-date test). 3 new tests + 1 unskipped; Iris.Server.Tests 1174 passed (+4), 17 skipped, 0 failed.
  136.12 is now the top of Up Next. →
  [docs/changes/13611-phase136-delivery-reliability.md](docs/changes/13611-phase136-delivery-reliability.md)

- **136.10 Moderation and trust-boundary behavior (cross-instance block/report/flag, blocked-content-not-reintroduced)** —
  verified cross-instance block/report/flag behavior in **both directions** (forward: local actor blocks remote;
  reverse: remote actor blocks local — the `BlockActivityHandler` records the edge when either party is local;
  Undo removes the edge on the remote instance) — no change needed there. Pinned two genuine gaps with new
  cross-instance integration tests: (1) **reverse-direction block** — a remote actor (bob on B) blocks a local
  actor (alice on A); the Block federates B→A; A records the `bob→alice` edge + inverse index
  (`GetBlockersAsync(alice)` includes bob); (2) **blocked content not reintroduced via new delivery** (the
  trust-boundary guarantee) — when alice (A) blocks bob (B), bob's new content (published after the block)
  federates B→A and is **stored** on A (fetchable by direct IRI) but is **excluded from alice's feed** (the
  `FeedService` applies the block edge on the reader's side — the authoritative guarantee). The delivery
  suppression on the deliverer's side is a local optimization that does not apply cross-instance (the
  deliverer B does not have alice's block edge — it is on A). `CrossInstanceBlockedContentIntegrationTests`
  (two-instance, A: alice, B: bob; signed outbox publish; `RoutingFetcher`). 2 new tests; Iris.Server.Tests
  1171 passed (+2), 0 failed; Iris.Core.Tests 445 passed. 136.11 is now the top of Up Next. →
  [docs/changes/13610-phase136-moderation-trust-boundary.md](docs/changes/13610-phase136-moderation-trust-boundary.md)

- **136.9 Update/Delete/Undo propagation (edit, delete/tombstone, undo flows)** — verified the lifecycle
  propagation is **already correct in both directions** (Update federates to remote followers + relays and
  refreshes the remote copy; Delete federates + tombstones the remote copy (AS2.0 `Tombstone`, F-10); Undo
  removes the follow/like/announce edge on the remote instance) — no change needed there. The one genuine
  gap (the delete/tombstone "remote visibility" half): deleting a **parent** post left its **replies
  orphaned but still listed and served** under the now-tombstoned parent, on both the home and every remote
  instance holding a copy. Fix: `DeleteActivityHandler` now **collapses the thread under a deleted parent**
  (removes each child's `parent → child` reply edge via `IReplyStore.GetRepliesAsync`/`RemoveReplyAsync`), so
  the tombstoned parent's `/replies` collection is empty (the child objects remain stored — fetchable by
  direct IRI; only the thread listing is collapsed). The federated half is the existing Delete propagation
  (the `Delete` reaches every copy-holder, which applies the same cleanup via the same handler). Bounded
  stale artifacts documented (children's content not removed; like/announce edges on a tombstoned object not
  swept; ghost-parent reply edges). `CrossInstanceDeleteThreadCollapseIntegrationTests` (two-instance,
  A: `bob` the parent's home; B: `alice` bob's follower) + 2 unit tests in `DeleteActivityHandlerTests`.
  Verified non-vacuous (the test fails when the parent-collapse fix is disabled — B's reply edge is not
  collapsed). Iris.Server.Tests 1169 passed (+3), 0 failed; Iris.Core.Tests 445 passed. 136.10 is now the top
  of Up Next. → [docs/changes/13609-phase136-update-delete-undo-propagation.md](docs/changes/13609-phase136-update-delete-undo-propagation.md)

- **136.8 Reactions and engagement interoperability (reactions, boosts, counters, graceful degradation)** —
  a local boost (Announce) of a **remote** object now **federates to the object's home instance**, so the
  object's author's per-object boost counter (the `/shares` collection, decision 056 (d)) sees the Iris boost
  — even when the object's author is not a follower of the announcer. The core gap (the 136.7
  reply-parent-author gap, now for the Announce path): the `OutboxPublishHandler`'s Announce branch fanned
  out the Announce **only to the announcer's followers + relays** and **discarded** the owner
  `RecordAnnounceLocalAsync` had resolved (a remote object's author, via a 24.1 remote fetch), so a boost of a
  Lemmy post never reached the post's home when the author was not a follower (the post's `/shares` counter
  never saw it). The **Like** path was already correct (it went through the generic delivery block +
  `RecordLikeLocalAsync` owner resolution) — no change. Fix: the Announce branch now captures the owner and,
  after the follower/relay fan-out, **delivers the Announce to it** when it is a resolvable remote
  (non-local) author (local owner = no-op; the object-IRI fallback is skipped); the Announce's `to` audience
  now names the object's author (mirrors the 136.7 reply's parent-author audience);
  `ResolveReplyParentAuthorAsync` is renamed `ResolveObjectAuthorForDeliveryAsync` to reflect its now-general
  use (both the reply-parent and announce-owner paths). `CrossInstanceAnnounceIntegrationTests` (two-instance,
  A: `bob` the object's home; B: `alice` the announcer): alice boosts bob's remote note m1 via a signed outbox
  POST; the boost federates to A, is recorded in A's announcers reverse index, and is surfaced on
  `GET {m1}/shares` (asserted on A: edge recorded, reverse index lists alice, `/shares` lists the boost with
  `actor`=alice, `object`=m1). Verified non-vacuous (the test times out when the delivery is disabled — the
  boost never reaches A). Like/Undo(Like)/Undo(Announce) cross-instance semantics were already correct
  (covered by `LikeAnnounceUndoPropagationIntegrationTests`, no regression); graceful degradation of
  unsupported interaction types is covered by `InboxProcessorTests` (an unhandled activity is stored, not
  dropped, and nothing is dispatched). The live Iris↔Lemmy leg stays blocked by the Lemmy-side
  signature/egress gap (136.3/136.2), not an Iris code gap. 1 new test; Iris.Server.Tests 1166 passed,
  0 failed; Iris.Core.Tests 445 passed. 136.9 is now the top of Up Next. → [docs/changes/13608-phase136-reactions-engagement-interop.md](docs/changes/13608-phase136-reactions-engagement-interop.md)

- **136.7 Cross-instance reply integrity (replies, threading, and context integrity)** — a reply whose
  parent lives on a remote instance (an Iris user replying to a Lemmy post) now **federates to the
  parent's home instance and is threaded there**, with the reply's `inReplyTo` and `conversationId`
  surviving the cross-origin hop. The core gap: a reply to a remote parent was never delivered to the
  parent's home — `RewriteOutboundAudienceAsync` resolved the parent author from the **local** object
  store only (a remote parent has no local author, so the parent author was never added to the reply's
  `to` audience), and the Create fan-out delivered **only to followers** (the parent author is not a
  follower). Fix: new `ResolveReplyParentAuthorAsync` (local parent from the object store; remote parent
  by fetching its object document over the wire, best-effort) feeds both the audience rewrite (parent
  author added to `to`) and a new parent-author delivery in `OutboxPublishHandler` (delivers the reply to
  the parent author when that author is a remote, non-local actor). Secondary fixes:
  `EnsureConversationIdAsync` anchors the reply's `conversationId` to the parent IRI (thread root) when
  the parent is remote/un-stored (was left unset); `ObjectRepliesAsync` serves a parent's `/replies` when
  the instance knows reply edges for it even though the parent object was never stored locally (404 only
  when there is neither a stored object nor reply edges). `CrossInstanceReplyThreadIntegrationTests`
  (two-instance, A: `bob` the parent's home; B: `alice` the replier): alice replies to bob's remote note
  m1 via a signed outbox POST; the reply federates to A, is threaded under m1 on A, and its `inReplyTo`
  + `conversationId` survive the hop (asserted on A: reply stored, parent→child reply edge recorded,
  `GET {m1}/replies` lists the reply). Verified non-vacuous (the test fails when the audience/delivery
  changes are reverted). The live Iris↔Lemmy leg stays blocked by the Lemmy-side signature/egress gap
  (136.3/136.2), not an Iris code gap. 1 new test; Iris.Server.Tests 1165 passed, 0 failed;
  Iris.Core.Tests 445 passed. 136.8 is now the top of Up Next. → [docs/changes/13607-phase136-cross-instance-reply-integrity.md](docs/changes/13607-phase136-cross-instance-reply-integrity.md)

- **136.6 Outbound federation from Iris communities (Iris -> Lemmy)** — a community-attributed post
  (a member posting a Note whose `attributedTo` is the community IRI) now federates to the member's
  remote followers with the posting community's identity intact, and the receiving instance fetches +
  persists that community's actor document. The community does **not** author content through its own
  outbox (by design — `CommunityOutboxPublishHandler` 400s on `Create`/`Announce`); the post flows
  through the **member's** outbox, signed as the member. The only source change: `CreateActivityHandler`
  gains an optional `IActorDocumentFetcher?` dependency and, in `StoreEmbeddedObjectAsync`, fetches +
  persists the embedded object's first `attributedTo` actor when it is **remote** (skipping local
  persons/communities), best-effort — closing the community-provenance gap the signature path alone
  never resolved (it only ever saw the signing member). `CommunityOutboundContentIntegrationTests`
  (two-instance, A: `alice` the remote follower; B: `bob` + community `iris`): (1) the community-
  attributed Create reaches the remote follower with `attributedTo` = the community IRI intact; (2) the
  remote instance persists the posting community's `Group` document (verified non-vacuous — the test
  fails when the source change is reverted). The live Iris→Lemmy leg stays blocked by the Lemmy-side
  signature/egress gap (136.3/136.2), not an Iris code gap. 2 new tests; Iris.Server.Tests 1164 passed,
  0 failed; Iris.Core.Tests 445 passed. 136.7 is now the top of Up Next. → [docs/changes/13606-phase136-outbound-federation-from-communities.md](docs/changes/13606-phase136-outbound-federation-from-communities.md)

## Keeping the docs lean

- **This file is the *only* one an agent must read and update every turn. Keep it short: bounded lists, not narrative.**
- **Clean up finished items roll them up - it's ok to keep some recently completed, but keep it short and point to change docs**
- Detail belongs in [docs/plans/](docs/plans/) (forward-looking scope), [docs/changes/](docs/changes/README.md) (what was built), or [docs/decisions/](docs/decisions/README.md) (why). Link, don't copy.
- [docs/ROADMAP.md](docs/ROADMAP.md) is append-only and low-churn — add a line when a phase closes, don't rewrite it.

Full operating rules, including the Inbox and Paused-Questions protocols, live in [docs/reference/AUTONOMOUS_LOOP.md](docs/reference/AUTONOMOUS_LOOP.md).
