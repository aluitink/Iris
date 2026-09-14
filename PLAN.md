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

- **136.9 Update/Delete/Undo propagation**
    - Verify edit/update propagation from each source instance to the remote copy.
    - Verify delete/tombstone behavior for posts and comments including remote visibility changes.
    - Verify undo flows (unfollow/unlike) remove or adjust remote state as expected.
    - Exit when lifecycle changes converge and stale artifacts are bounded and documented.

- **136.10 Moderation and trust-boundary behavior**
    - Test remote actor/community block behavior in both directions (instance-level and actor-level where supported).
    - Validate report/flag activities and how moderation signals are represented cross-instance.
    - Ensure blocked content is not reintroduced via backfill or retries.
    - Exit when moderation actions enforce expected visibility and delivery boundaries.

- **136.11 Delivery reliability, retries, and dead-letter handling**
    - Induce transient failures (timeouts/5xx) and verify retry budgets, backoff, and eventual success/failure behavior.
    - Confirm idempotency across retries (no duplicated posts/comments/reactions).
    - Capture and classify permanent failures (4xx) with operator-facing diagnostics.
    - Exit when delivery behavior matches policy and is observable end-to-end.

- **136.12 Performance and request-spam audit**
    - For each critical scenario, count federated requests by method+path+trigger to detect duplicate fan-out.
    - Identify N+1 or redundant fetch patterns in community timeline and thread hydration.
    - Define acceptable request-count budgets for baseline scenarios.
    - Exit when high-noise patterns are triaged into blocker/bug/perf classes.

- **136.13 Regression harness + closeout checklist**
    - Convert verified interop scenarios into a repeatable manual checklist for each release slice.
    - Record known incompatibilities and Lemmy-specific behavior notes with severity and workaround.
    - Produce a final pass/fail matrix: discovery, auth, peering, delivery, threading, lifecycle, moderation, reliability.
    - Exit when the checklist can be executed by another operator with consistent results.

- **136.14 Media and attachment interoperability**
    - Validate image, link, and rich-text/markdown payloads from Iris -> Lemmy and Lemmy -> Iris.
    - Verify media fetch/render behavior for remote assets (authless/public paths, broken-link handling, MIME mismatches).
    - Stress large attachments and long-body posts to confirm truncation, preview, and storage behavior is explicit.
    - Exit when media-bearing content round-trips with expected rendering and no silent drops.

- **136.15 Pagination and backfill consistency**
    - Validate cross-instance timeline paging boundaries (first/next/previous pages) for federated community content.
    - Confirm historical backfill after peering includes expected post/comment windows and stable ordering.
    - Verify cache bypass/reload does not lose older remote objects or create duplicate entries.
    - Exit when paged and backfilled views are consistent across refreshes and both instances.

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

- **136.5 Inbound federation to Iris communities (Lemmy -> Iris)** — the inbound-Create-to-community
  pipeline (handler → community content recorder → member outboxes → feed projection) and the C-07
  idempotency guard were already built and happy-path tested; this turn pinned the two remaining gaps.
  `CommunityInboundContentIntegrationTests` (two-instance, A: `alice`; B: `bob` + community `iris`):
  (1) a Create whose activity **and** embedded Note carry a fixed `published` value reaches the member's
  outbox and the community feed with the **originator's timestamp preserved** (not delivery time);
  (2) the same Create (identical activity IRI) delivered **twice** is stored once and reaches the member's
  outbox and the community feed **exactly once** (the C-07 guard in `InboxProcessor`, shared pre-dispatch,
  now locked for the community-inbound-Create path). **No production source change** — the pipeline and
  guard already worked; this pins the timestamp + idempotency attributes. The live Lemmy→Iris leg stays
  blocked by the Lemmy-side signature/egress gap (136.3/136.2), not an Iris code gap. 2 new tests;
   Iris.Server.Tests 1162 passed, 0 failed. → [docs/changes/13605-phase136-inbound-federation-to-communities.md](docs/changes/13605-phase136-inbound-federation-to-communities.md)

- **136.4 Community peering handshake (Follow/Accept)** — the community peering handshake is now
  pinned in **both** directions across two instances. The auto-accept direction was already covered
  (`CrossInstanceAcceptPropagationIntegrationTests`); this turn added the **gated** (manually-approving)
  direction — `CommunityGatedPeeringIntegrationTests`: a remote community follows a manually-approving
  community → the follow is held (edges recorded, no auto-Accept) → the operator publishes an `Accept`
  to the community's outbox (signed as the community) → it is server-delivered back and the remote
  `AcceptActivityHandler` (G-3) finalizes the follower's edge. New `SeedManuallyApprovingCommunityWithExistingKey`
  seeder (existing-key form) for the two-host fixture re-seed. **No production source change** — the
  gated path already worked; this pins it. The live Iris→Lemmy leg stays blocked by the Lemmy-side
   signature/egress gap (136.3/136.2), not an Iris code gap. 1 new test; Iris.Server.Tests 1159 passed.
   → [docs/changes/13604-phase136-community-peering-handshake.md](docs/changes/13604-phase136-community-peering-handshake.md)

## Keeping the docs lean

- **This file is the *only* one an agent must read and update every turn. Keep it short: bounded lists, not narrative.**
- **Clean up finished items roll them up - it's ok to keep some recently completed, but keep it short and point to change docs**
- Detail belongs in [docs/plans/](docs/plans/) (forward-looking scope), [docs/changes/](docs/changes/README.md) (what was built), or [docs/decisions/](docs/decisions/README.md) (why). Link, don't copy.
- [docs/ROADMAP.md](docs/ROADMAP.md) is append-only and low-churn — add a line when a phase closes, don't rewrite it.

Full operating rules, including the Inbox and Paused-Questions protocols, live in [docs/reference/AUTONOMOUS_LOOP.md](docs/reference/AUTONOMOUS_LOOP.md).
