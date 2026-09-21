# Iris — ActivityPub .NET Libraries

A set of .NET libraries for ActivityPub, designed to be embedded in existing apps and services.

**This file is the single live operating document** for **two parallel workstreams**: a **dev** loop (code + tests + the Dev Queue) and a **QA** loop (live-app behavior + the QA Queue). It is the only thing either agent reads every turn; everything else in `docs/` is either rarely-touched reference or a write-once archive. Operating instructions: [DEV_LOOP.md](docs/reference/DEV_LOOP.md) (dev) and [QA_LOOP.md](docs/reference/QA_LOOP.md) (QA). The two loops own **disjoint sections** of this file and **disjoint files** (see the [ownership map](docs/reference/DEV_LOOP.md#ownership-map-planmd)) — neither edits the other's work.

## Documentation

**Loops (read every turn):** [DEV_LOOP.md](docs/reference/DEV_LOOP.md) — the dev workstream. [QA_LOOP.md](docs/reference/QA_LOOP.md) — the QA workstream. This file is the shared live document both loops drive; each owns disjoint sections ([ownership map](docs/reference/DEV_LOOP.md#ownership-map-planmd)).

**Live / working docs:**

| File | Contents | Read cadence |
|---|---|---|
| **PLAN.md (this file)** | Live state / Active Slice / Dev Queue / Re-verify / QA Queue / Recently Completed / Paused Questions | every turn |
| [docs/qa/](docs/qa/README.md) | QA findings — one doc per defect + brief pass log (findings go here, **not** in PLAN.md) | every QA pass; update a finding's status when fixed |
| [docs/plans/](docs/plans/) | Deep-dive scope for multi-turn workstreams (e.g. [community-simplification.md](docs/plans/community-simplification.md), [unified-home-feed.md](docs/plans/unified-home-feed.md)) | when picking up that workstream |

**Reference (rarely changes):**

| File | Contents |
|---|---|
| [docs/reference/ARCHITECTURE.md](docs/reference/ARCHITECTURE.md) | Design principles, solution layout, cross-cutting concerns |
| [docs/reference/PROJECTS.md](docs/reference/PROJECTS.md) | Per-project details for the Iris libraries |
| [docs/reference/TESTING.md](docs/reference/TESTING.md) | Integration-first testing strategy + fast-vs-full + blame/timeout |
| [docs/reference/CODING_STYLE.md](docs/reference/CODING_STYLE.md) | Binding conventions + ActivityStreams rules (read before every coding turn) |
| [docs/reference/INTEROP_CONFORMANCE_MATRIX.md](docs/reference/INTEROP_CONFORMANCE_MATRIX.md) | Living peer×capability conformance matrix — update as interop slices land |

**Archive (write-once / low-churn):**

| File | Contents |
|---|---|
| [docs/ROADMAP.md](docs/ROADMAP.md) | Append-only ledger of closed phases (one line each) — add a line on phase close, never rewrite |
| [docs/changes/](docs/changes/README.md) | One build-notes doc per slice/change |
| [docs/decisions/](docs/decisions/README.md) | One doc per substantial design decision |
| [docs/phase-notes/](docs/phase-notes/README.md) | Phase rationale + test-count notes |

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
- **Marking a test slow:** `[Trait(TestCategories.Category, TestCategories.Slow)]` (constants in `Iris.Testing.TestCategories`). Only mark tests that wait on real wall-clock time.

### Isolating slow / hanging tests (blame)

When the suite stalls or a test runs long, don't guess which one — let the runner name it:

1. **Per-test timings (fastest signal):**
   ```
   dotnet test tests/Iris.Web.Tests -v n --logger "console;verbosity=detailed"
   ```
   The detailed logger prints a line per test with its duration. Anything >15 s is the offender.
2. **Blame mode (isolates a hanging test):** `--blame` runs tests so that a problematic one can be isolated; `--blame-hang` adds a per-test timeout that terminates the testhost and names the hung test:
   ```
   dotnet test --blame-hang --blame-hang-timeout 15s --blame-hang-dump-type none
   ```
   On timeout the runner reports **which test** hung and exits (no dump, since we only want the name). Combine with a project filter to keep it quick:
   ```
   dotnet test tests/Iris.Web.Tests --blame-hang --blame-hang-timeout 15s --blame-hang-dump-type none
   ```
3. **Action on an offender:** if it legitimately waits on wall-clock time, mark it `Slow` (it drops out of the fast run); otherwise **skip/comment it out** with a date (`[Fact(Skip = "slow >15s — <date>")]`). **Log every deletion/skip** (test name, action, reason, restore-by) in the change doc — no silent deletions; the phase closeout reviews the ledger.

Details + the honest payoff note: [docs/reference/TESTING.md §Running the suite: fast vs. full](docs/reference/TESTING.md#running-the-suite-fast-vs-full).



## How the two loops work

- **Dev loop** → [DEV_LOOP.md](docs/reference/DEV_LOOP.md). Owns code + tests + the **Dev Queue** below. Deploys the live container and records the deployed commit in **Live state**.
- **QA loop** → [QA_LOOP.md](docs/reference/QA_LOOP.md). Owns the live app's behavior + the **QA Queue** (`docs/qa/`). Works in an isolated **worktree** (`scripts/qa-worktree.sh`) so it never writes dev's files.
- **Staleness is the #1 false-finding source.** Before QA starts a pass it checks **Live state `deployed:` == HEAD**; if they differ, the container is stale and no pass may start. When either loop sees an error that *might* be redeploy-related, the **redeploy-error rule** in [DEV_LOOP.md](docs/reference/DEV_LOOP.md#when-qa-reports-an-error-or-you-see-one-that-may-be-redeploy-related) decides whether to rebuild/redeploy or actually fix code.
- **Environment split:** the QA loop uses the QA cluster (`qa-iris-a`, `qa-iris-b`, `qa-lemmy`, `qa-mastodon`); the dev loop uses the dev cluster (`dev-iris-a`, `dev-iris-b` — legacy names `iris-dev1` / `iris-dev2` remain mapped to the same ports); `iris.luit.ink` is the production/public FQDN and is not used for dev or QA testing.
- **Ownership:** each loop writes only its own PLAN.md sections and files (the [ownership map](docs/reference/DEV_LOOP.md#ownership-map-planmd) is binding).

## Live state

- **Deployed commit:** `6b11799` (S32 — shared-inbox Delete/Update of a Note routes to the note's author, on top of `27b1ba6` S27 + `c00129e` S33 docs + `d6914d6` S33 + `7d290c4` S31 docs + `45f3038` S31 + `47fae1a` S34 + `d8d4303` + `8af708c` S29 + `e33d1f1` S25 + `2230de4` S24 Defect 3 + `f728af4` S4 + `0fe7f0f` S2/S14 + `cffe397` ⑤ un-follow feed-cache invalidation). **Redeployed 2026-09-21T04:38:xxZ on the BASE `docker-compose.yml` (no dev/fed2 overlays) — the user's service restructuring is the new steady state** — `irisweb-iris-web-1` rebuilt + recreated, healthy, on host `8088` (`https://iris.luit.ink`); `irisweb-db-1` healthy. **S32 + S33 + S27 live re-verification deferred to QA** (needs the two-instance QA federation stack; dev does not run it). (Previously the dev overlay `docker-compose.dev.yml` was used.) **S34 live-verified:** `s34a` (`manuallyApprovesFollowers=true`) + `s34b` follows → pending request recorded, **public `followers` = 0** (withheld while pending); after Accept → `followers` = 1 (`s34b`), request drained. **S29 live-verified:** `GET /.well-known/webfinger?resource=acct:!test-882@iris.luit.ink` → **200**, `self` → `…/ap/v1/c/test-882` (was 404); bare `acct:test-882@…` → 200 (no regression); person `acct:andrew@…` → 200. **S25 live-verification deferred to QA** (needs the two-instance QA federation stack; dev does not run it). **Prior live-verified (S24 Defect 3):** `GET /ap/v1/u/skinnylatte` (a stored remote actor) → **200** serving the remote document as-is (was 404); local `andrew` → 200; unknown handle → 404. **Prior live-verified (S4):** signed-in `andrew` Communities → Following shows local + the remote **"Iris Interop"** with a Leave button.
- **Container:** `irisweb-iris-web-1` — current (built with the S4 fetch-by-IRI fix on top of the S2/S14 anonymous-proxy signing fix).
- **Note:** **S4 remote-community Following-tab gap FIXED (2026-09-20):** `Communities.razor` `ResolveFollowingCommunitiesAsync` now fetches a followed IRI not in the local search via `Ui.GetActorAsync` and keeps `Group` results (the behavior its doc comment always promised). **Live-verified:** the followed remote interop community renders in the Following tab (22 consecutive QA passes had it missing). [change doc 14817](docs/changes/14817-communities-following-remote-fetch.md). Prior: S2/S14 anonymous-proxy signing [14816](docs/changes/14816-anonymous-proxy-signs-as-instance-actor.md); ⑤ un-follow feed-cache invalidation [14815](docs/changes/14815-unfollow-feed-cache-invalidation.md); Lemmy Undo 400 fix [14814](docs/changes/14814-lemmy-undo-follow-embedded.md); shared-inbox route [1598](docs/changes/1598-shared-inbox-route.md).

## Active Slice

- **S34 — gated (manually-approved) follow request appears in the public `followers` collection BEFORE acceptance — DONE + LIVE-VERIFIED (2026-09-20, commits `d8d4303` + `47fae1a`).** S2-sev privacy/authorization bug (same-instance gate path; confirmed reproducing on a fresh build). A local person with `manuallyApprovesFollowers = true` receives a `Follow`: the pending request is correctly queued (`/local/v1/u/{h}/requests` + notification), but the requesting actor **immediately appears in the public `followers` collection** (`GET /ap/v1/u/{h}/followers`) before the owner Accepts. **Root cause (confirmed, TWO paths):** (1) **inbound** `FollowActivityHandler.HandleAsync` person branch recorded the `Follow` edge **unconditionally** before the gate check; (2) **local outbox** `RecordFollowLocalAsync` did the same (recorded the edge unconditionally, then added a request edge). The public `followers` collection reads `EdgeKind.Follow` (`IFollowStore.GetFollowersAsync`), so the requester was publicly listed while pending. The community branch was already correct (withholds the membership edge when `manuallyApprovesMembers`). **Fix:** in both paths, check the gate **first**; when gated, withhold the `Follow` edge and record only the `FollowRequest` edge (pending). Accept's existing `RecordFollowAsync` (in `ApplyFollowDecisionEdgeAsync`) then materializes the `Follow` edge; Reject drains the request. The public `followers` endpoint + store need no change. **Tests:** updated the inbound `FollowActivityHandlerTests` person-gate test (withheld) + the two-instance `FederationSignatureIntegrationTests` reject-propagation test (was asserting the provisional edge); added a withheld-edge assertion to the local `FollowRequestQueueIntegrationTests` gated-follow test. Fast suite **1409 pass, 0 fail**; web **108 pass, 0 fail**. **Live-verified (dev):** `s34a` gated + `s34b` follows → pending request recorded, **public `followers` = 0** (withheld); after Accept → `followers` = 1 (`s34b`), request drained. [change doc 14821](docs/changes/14821-gated-follow-withheld-from-public-followers.md). [finding s34](docs/qa/s34-gated-follow-not-withheld-from-public-followers.md).

- **S29 — community (Group) not resolvable via WebFinger (`acct:!handle@host` → 404) — DONE + LIVE-VERIFIED (2026-09-20, commit `8af708c`).** S2-sev discovery bug. The Group document is correct and public at `/ap/v1/c/{handle}`, but WebFinger for the community form `acct:!{handle}@{host}` 404s (person form `acct:{handle}@{host}` works). **Root cause (confirmed, `WebFingerHandler`):** the handler takes `handle = acct[..at]`, so `acct:!devs@host` yields `handle = "!devs"` (leading `!` retained); both `BuildActorIri`/`BuildCommunityIri` build IRIs with a literal `!` in the segment and miss → 404. **Fix:** strip the leading `!` in the community branch (`handle.TrimStart('!')`) so `acct:!devs@host` → `/ap/v1/c/devs`; person + instance-actor paths untouched. 2 new integration tests. Fast suite **1409 pass, 0 fail**; web **108 pass, 0 fail**. **Live-verified:** `acct:!test-882@iris.luit.ink` → 200 `self` → `/ap/v1/c/test-882` (was 404); bare + person controls intact. [change doc 14820](docs/changes/14820-webfinger-bang-community-resolves-group.md). [finding s29](docs/qa/s29-community-webfinger-404.md).

- **S25 — remote post not in the follower's home feed — DONE + committed (2026-09-20; live-verify deferred to QA).** S2-sev interop bug (two fresh Iris instances): after a cross-instance follow + A's public post federating to B's inbox, B's follower home feed (`GET /u/{follower}/feed`) omitted the delivered remote `Note` (showed only the inbox-delivered `Follow`). **Fix:** `FeedService` unions the wire-walked remote outbox with the inbox-delivered object-store content attributed to that author (`GetDeliveredContentAsync` → `IObjectStore.ListByActorAsync`, each object wrapped in a synthetic embedded `Create` so it flows through the existing de-dup/coalesce + visibility filters); de-duplicated so a note in both renders once; tombstones skipped. 3 new unit tests; fast suite **1408 pass, 0 fail**; web **108 pass, 0 fail**. **Live-verification deferred to QA** (needs the two-instance QA federation stack; dev does not run it). [change doc 14819](docs/changes/14819-home-feed-surface-delivered-remote-content.md).

- **S24 — cross-instance follow state inconsistent — D3 DONE + LIVE-VERIFIED; D1/D2 pending two-instance repro (2026-09-20).** S2-sev interop bug (Interop A2, two fresh Iris instances): three defects. **Defect 3 (remote-actor direct GET 404s) — DONE + LIVE-VERIFIED (dev):** `ActorDocumentHandler` now falls back to the stored remote actor (keyed by its remote IRI, persisted by `RemoteActorPersister` during the follow flow) whose handle matches, and serves the document as-is; live-verified `GET /ap/v1/u/skinnylatte` → 200 (was 404), local + unknown-handle controls intact. [change doc 14818](docs/changes/14818-remote-actor-direct-get-serves-stored-document.md). **Defect 1 (Following tab omits remote actors) — NOT reproducible on dev** (the tab renders remote actors; server `following` wire-correct + client hydration resolves them) — needs a clean two-Iris-instance entry (QA federation stack) to re-confirm. **Defect 2 (spurious self-follow in outbox) — NOT reproducible on dev** (outbox empty after QA undid follows) — needs a fresh cross-instance follow on the two-instance stack to trigger + trace the follow handler's secondary write. Both D1/D2 remain OPEN pending a two-instance repro. **S4 (remote communities missing from the Following tab) — DONE (2026-09-20):** [change doc 14817](docs/changes/14817-communities-following-remote-fetch.md). **S2/S14 (proxy 401 for unsigned GETs) — DONE (2026-09-20):** [change doc 14816](docs/changes/14816-anonymous-proxy-signs-as-instance-actor.md).

## Dev Queue

**Work order (dev, per [DEV_LOOP.md step 2](docs/reference/DEV_LOOP.md#the-loop)):** Inbox → Re-verify debt → this queue (blockers → S2-sev QA fixes → feature scope). Keep it sorted; cap ~7 items, link the rest to plan docs.

**Inbox (user/loop injections — action oldest first):**

- **[2026-09-21] Service restructuring — run the plain app.** User is restructuring the service: stop the containers, then start the **base `docker-compose.yml`** (the "prod" app, no dev/fed2 overrides) — i.e. just `iris-web` + `db`, **not** the dev overlay (`docker-compose.dev.yml`) or the fed2 second instance. **Actioned same turn:** stopped `irisweb-iris-web-1` / `irisweb-db-1` / `irisweb-db2-1`, then started with base compose only; `irisweb-iris-web-1` healthy on `8088` (`https://iris.luit.ink`). Orphans `irisweb-iris2-web-1` / `irisweb-db2-1` left untouched. **Open for the next turn:** confirm the restructuring is intended to be the *new steady state* (plain app, no dev/fed2 overlays) before any future deploy assumes the dev overlay; if so, update the deploy procedure + Live state accordingly.

**Re-verify debt (committed fixes QA must confirm on a current build):**

- *(cleared Pass 27, 2026-09-20 — build now current (`a45f3d4` deployed); QA live-verified **S6** remote Join, the **S4** local-community following facet, the **S8** "All on this instance" list, and **S16** poll-vote persistence. S4's remote-community display caveat remains open in [s04](docs/qa/s04-communities-following-remote.md).)*

**QA fixes (by severity — one doc each in [docs/qa/](docs/qa/README.md)):**

- **S2-sev (cross-instance interop, A5–A10; live re-verify on the two-instance QA federation stack):** S30 [cross-instance community join/view blocked](docs/qa/s30-cross-instance-community-join-blocked.md) (blocks A8.2/A8.3). (S26 remote-reply threading — **fixed on the current build**: the F-12 parent→child edge is recorded unconditionally for any stored reply (`CreateActivityHandler`), covered by `CrossInstanceReplyThreadIntegrationTests` — live re-confirm deferred to QA. S27 Like-at-shared-inbox — **DONE + 2-instance unit-verified (`27b1ba6`)**, live re-verify deferred to QA. S28 remote-Announce `shares` — **fixed on the current build**: `AnnounceActivityHandler` records the `announcer → object` edge and `ObjectSharesAsync` (`GET {note}/shares`) reads it, covered by `CrossInstanceAnnounceIntegrationTests.BoostOfRemoteObject_FederatesToObjectHome_AndIsCountedThere` (asserts the edge + `/shares` lists the boost) — live re-confirm deferred to QA (S28's re-test: not reproducible via Playwright; needs a signed CLI/AP client). S29 WebFinger `!`-community + S34 gated-follow public exposure — **DONE + live-verified**; S32 Delete/Update-at-shared-inbox — **DONE + 2-instance unit-verified (`6b11799`)**, live re-verify deferred to QA; S33 unfollow `Undo` — **DONE + 2-instance unit-verified (`d6914d6`)**, live re-verify deferred to QA; S34 needs QA re-confirm of the two-instance A3 entry on a fresh stack.)
- **S3-sev:** ~~S31 [edit clears `published` timestamp](docs/qa/s31-edit-clears-published-timestamp.md)~~ — **DONE + unit-verified this turn (`45f3038`)** (the `UpdateActivityHandler` now carries the stored Note's `published` onto the incoming edit object before storing it; +1 test). Live note-edit re-verify deferred to QA on the two-instance stack (the signed outbox POST needs the browser session cookie; the pre-existing `alice` cookie password isn't the dev seed).




**Feature scope:**

- **⑤ Live Lemmy interop — COMPLETE + live-verified (2026-09-20).** All interop blockers fixed + deployed + live-verified end-to-end: actor `published` field, follow delivery for cached remote communities ([change doc 1597](docs/changes/1597-lemmy-follow-delivery-and-actor-published.md)), and the Undo (unfollow) 400 ([change doc 14814](docs/changes/14814-lemmy-undo-follow-embedded.md)). **Live-verified:** s7test Follow → Lemmy `c/interop` (count 1→2), Undo → 200 (count 2→1, no dead-letter). **⑤ Mastodon un-follow fan-out — fixed (2026-09-20, Active Slice):** the un-follower's home feed no longer serves the stale cached feed after an un-follow (per-actor feed cache invalidated on follow/unfollow) — [change doc 14815](docs/changes/14815-unfollow-feed-cache-invalidation.md).
- **②④⑤ Community simplification — unify members with followers (Lemmy-shape). ② Phases 1-6 DONE; ④ control surfaces DONE (incl. co-owner Leave); ⑤ live-interop unblocked (see above).** Members = followers, Join/Leave → Follow/Undo, `manuallyApprovesMembers` gates the Follow, single **Follow** button (labeled **Join**/**Leave** for communities), dead `ICommunityStore` member methods retired (kept `EdgeKind.CommunityMember` for the startup migration). **Peering** is the single owner-only extra. See [docs/plans/community-simplification.md](docs/plans/community-simplification.md) + [change doc](docs/changes/1593-community-management-my-communities-tab.md).
- **③④ Unified home feed — COMPLETE (Phases 1-7, 2026-09-20).** Two feed tabs + bottom strip + community IA rework. See [docs/plans/unified-home-feed.md](docs/plans/unified-home-feed.md).
- **① Minor UI fixes — feed load feel — COMPLETE (2026-09-20).** (1)(2)(3) Done. (4) Server-side per-actor follow-feed cache (30s TTL + `?refresh=true` bypass). [change doc](docs/changes/1588-server-side-follow-feed-caching.md)

## QA Queue

*(QA-owned — see [QA_LOOP.md](docs/reference/QA_LOOP.md). Findings live in [docs/qa/](docs/qa/README.md); this is a count + pointer only.)*

- **4 open. No blockers (all S2/S3-sev).** (Pass 96: S4 re-confirmed 22nd pass; S17 re-confirmed 39 outbox requests; S16-UX re-confirmed 1 votes NO badge. S23 FIXED (routes registered after redeploy). S22 FIXED. S3 FIXED (Create IRI now serves Note, Pass 68).)
- **Top priority:** S4 (remote communities missing from Following tab — 22nd pass), S17 (profile tabs over-fetch — 39 outbox requests for 3 tabs), S2 (proxy 401 for unsigned Mastodon GETs — 2 console errors), S14 (proxy 401 + CSP on actor detail). S16-UX (poll "You voted" badge missing on object detail page).
- **Last pass:** 96 (2026-09-20). **Resume checkpoint:** see [docs/qa/passes.md](docs/qa/passes.md) — S13, S15, S7, S8, S3 (UI + Create IRI), S18 partially fixed; S20, S21, S22, S19 (all facets), S23 FIXED. Remaining open: S2, S4 (remote), S14, S16-UX, S17, S18 (partial).

## Paused Questions

> **The loops never block on a question.** When either loop hits something it can't decide (a product fork, a conflict, a destructive action), it logs a short entry here and **moves on to another item** (stashing in-flight work first). A human clears this list when convenient; cleared entries fold their answer into the relevant slice/change doc. See [DEV_LOOP.md — Blocking without stopping](docs/reference/DEV_LOOP.md#blocking-without-stopping).

- *(empty)*

## Recently Completed

- **S32 Delete/Update of a local note dropped at the shared inbox ("unknown recipient") — fixed + 2-instance unit-verified (2026-09-21):** a `Delete` or `Update` of a local note delivered to the author's shared inbox was dropped as `unknown recipient <note-IRI>` — the shared inbox routed it to the note's own IRI (a content object, not an actor), so no local recipient was found and the peer's copy was never tombstoned or refreshed (stale live Note). The handler now special-cases `Delete`/`Update`: it resolves the object's owner (its `attributedTo`) from the object store (no wire hop) and routes the delivery to the owner, whose `DeleteActivityHandler` tombstones / `UpdateActivityHandler` refreshes the stored object. An unresolvable owner degrades to the object IRI and is dropped as before. +2 two-instance tests (Delete → tombstone, Update → refresh) that fail without the fix and pass with it. Fast suite 1416 pass / 0 fail (was 1413); web 108 / 0 fail. **Live re-verify deferred to QA** on the two-instance stack. Deployed `6b11799` on the base compose (app healthy).
- **S27 cross-instance Like dropped at the shared inbox ("no local recipient") — fixed + 2-instance unit-verified (2026-09-21):** a remote `Like` of a local note was delivered to the author's shared inbox but dropped as `no local recipient; accepting and dropping`. The shared inbox routed any non-content, non-Undo activity to its `object` — for a `Like` that's the note's IRI (a content object, not an actor) — so no local recipient was found and the note's `/likes` never updated. The handler now special-cases a `Like`: it resolves the object's owner (its `attributedTo`) from the object store (no wire hop) and routes the delivery to the owner, whose `LikeActivityHandler` records the like edge. An unresolvable owner degrades to the object IRI and is dropped as before (a remote note's like is recorded on the note's home instance, not here). +1 two-instance test (remote `alice` likes local `bob`'s note via the shared inbox → like edge recorded) that fails without the fix and passes with it. `Announce` is intentionally left on the fan-out branch (the existing `…FannedOutToFollowersAndStored` test depends on it; S27's finding scopes the defect to Like only). Fast suite 1413 pass / 0 fail (was 1412); web 108 / 0 fail. **Live re-verify deferred to QA** on the two-instance stack (S27's re-test noted a browser can't forge the signed outbox POST; needs a signed CLI/AP client). Deployed `27b1ba6` on the base compose (app healthy). [change doc 14824](docs/changes/14824-shared-inbox-like-routes-to-note-author.md)
- **S33 Unfollow (`Undo` of `Follow`) not propagated — peer edge remains — fixed + 2-instance unit-verified (2026-09-21):** an un-follow removes the edge on the unfollower's instance but not the followee's. When the followee advertises a **shared inbox** (the app always does), the unfollower's `Undo` is delivered to that shared inbox, and `SharedInboxHandler` routed non-content activities to `typedActivity.Object` — for an `Undo` of a `Follow` that's the original **follow IRI** (an activity, not an actor), so the recipient check 404s it as `unknown recipient <follow IRI>` and the peer's `followers` edge is left stale. The handler now special-cases an `Undo` of a `Follow`: it resolves the follow's **target** (the followee) from the embedded Follow's `object`, or from the follow stored in this instance's activity store when the object is a bare IRI link, and routes there; the followee's `UndoActivityHandler` then removes the edge. +2 integration tests (embedded + bare-IRI shapes) that fail without the fix and pass with it. Fast suite 1412 pass / 0 fail (was 1410); web 108 / 0 fail. **Live re-verify deferred to QA** on the two-instance stack. Deployed `d6914d6` on the base compose (app healthy). [change doc 14823](docs/changes/14823-shared-inbox-undo-follow-routes-to-follow-target.md)
- **S31 editing a Note clears its `published` timestamp — fixed + unit-verified (2026-09-21):** both the local edit and the inbound federation path route through `UpdateActivityHandler.HandleAsync`, which stored the incoming (bare) edit object directly — the client sends only id/content/audience and AP treats `published` as immutable, so the stored Note's `published` was dropped (and the cleared value propagated to peers). The handler now carries the stored object's `published` onto the incoming object before storing it (`contentObj.Published ??= (stored as ActivityObject)?.Published`), then stamps `updated`; since `updated` is the same instance the `Update` activity's `object` refers to, both the stored Note and the propagated object are fixed at once. +1 test (preserves `published`, sets `updated`); strengthened `…StampsUpdated`. Fast suite 1410 pass / 0 fail (was 1409); web 108 / 0 fail. **Live note-edit re-verify deferred to QA** on the two-instance stack (signed outbox POST needs the browser session cookie; the pre-existing `alice` cookie password isn't the dev seed). Deployed on the base compose (app healthy). [change doc 14822](docs/changes/14822-edit-preserves-note-published-timestamp.md)
- **S34 gated follow leaked into the public `followers` collection before acceptance — fixed + live-verified (2026-09-20):** the public `followers` collection reads the Follow edge; both the inbound `FollowActivityHandler` person branch and the local `RecordFollowLocalAsync` recorded that edge **unconditionally before the gate check**, so a pending (gated) follow was publicly listed before acceptance. Both paths now check the gate first and, when gated, withhold the Follow edge and record only the pending `FollowRequest` edge; Accept materializes the edge, Reject drains the request. Updated the inbound + two-instance tests (which asserted the provisional edge) + added a local withheld-edge assertion. Fast suite 1409 pass / 0 fail; web 108 pass / 0 fail. **Live-verified (dev):** `s34a` gated + `s34b` follows → pending recorded, public `followers` = 0 (withheld); after Accept → `followers` = 1, request drained. Deployed `47fae1a` (container healthy). [change doc 14821](docs/changes/14821-gated-follow-withheld-from-public-followers.md)
- **S29 community WebFinger `!`-form 404 — fixed + live-verified (2026-09-20):** `WebFingerHandler` took `handle = acct[..at]`, so `acct:!devs@host` → `handle = "!devs"`; `BuildActorIri`/`BuildCommunityIri` built IRIs with a literal `!` in the segment and both store lookups missed → 404 (the bare `acct:devs@host` form, the only one the old test covered, worked). The community branch now strips the leading `!` (`handle.TrimStart('!')`) so `acct:!devs@host` → `/ap/v1/c/devs`; person + instance-actor paths untouched. 2 new integration tests. Fast suite 1409 pass / 0 fail; web 108 pass / 0 fail. **Live-verified:** `acct:!test-882@iris.luit.ink` → 200 `self` → `/ap/v1/c/test-882` (was 404); bare + person controls intact. Deployed `8af708c` (container healthy). [change doc 14820](docs/changes/14820-webfinger-bang-community-resolves-group.md)
- **S4 remote communities missing from the Following tab — fixed (2026-09-20):** `Communities.razor` `ResolveFollowingCommunities()` matched each followed IRI against the **local search cache** only and had no fetch-by-IRI fallback, so a followed REMOTE community was silently dropped (re-confirmed 22 consecutive QA passes). It is now `async` (`ResolveFollowingCommunitiesAsync`) and, for each followed IRI not in the local search, fetches the actor document via `Ui.GetActorAsync` (routes a remote through the same-origin proxy) and keeps the doc only if it is a `Group`; a per-IRI fetch failure is swallowed so one unreachable remote doesn't break the tab. Web-UI change verified live per the web test policy (no harness coverage). `Iris.Web.Tests` 108 pass, 0 fail; build 0/0. **Live-verified:** signed-in `andrew` Following tab shows local + the remote "Iris Interop" (Leave button; `POST /ap/v1/proxy/…/lemmy.luit.ink/c/interop` → 200); 0 console errors. Deployed (container healthy). [change doc 14817](docs/changes/14817-communities-following-remote-fetch.md)
- **S2/S14 proxy 401 for unsigned GETs — fixed (2026-09-20):** the anonymous (signed-out) proxy seam relayed public remote GETs **unsigned**, but a strict remote (mastodon.social) requires a valid HTTP signature even for a public read (unsigned → 401 "Request not signed"). `ProxyHandler` now signs the anonymous GET as the **local instance actor** (`options.InstanceActorId`, key registered + served at the instance root) via the `X-Iris-Actor` override; the authenticated path is unchanged. Anonymous-retry test repurposed to assert signed-as-instance-actor; `Proxy_ResignsAsActingActor_NotInstanceActor` still passes. Full fast suite green: 1402 passed, 0 failed. **Live-verified:** signed-out proxy GET of mastodon.social actor → 200 (was 401). Deployed (container healthy). [change doc 14816](docs/changes/14816-anonymous-proxy-signs-as-instance-actor.md)
- **⑤ Mastodon un-follow fan-out fix (2026-09-20):** after an un-follow, the un-follower's home feed (`GET /u/{handle}/feed`) served the **stale** per-actor feed cache (30 s TTL) and kept showing the unfollowed user's posts. Added `IFollowFeedService.InvalidateActorFeedCache` (no-op default) + `FeedService` impl (`_feedCache.TryRemove`), called in `OutboxPublishHandler` on a `Follow` (gained a target) and an `Undo` of a `Follow` (lost a target). The delivery side was already correct (QA 993); this closes the client-facing feed-cache gap. New regression test drops only the named actor's entry. Full fast suite green: 1402 passed, 0 failed. Deployed (container healthy). [change doc 14815](docs/changes/14815-unfollow-feed-cache-invalidation.md)
  ## Keeping the docs lean

- **This file is the *only* one an agent must read and update every turn. Keep it short: bounded lists, not narrative.**
- **Clean up finished items roll them up - it's ok to keep some recently completed, but keep it short and point to change docs**
- Detail belongs in [docs/plans/](docs/plans/) (forward-looking scope), [docs/changes/](docs/changes/README.md) (what was built), or [docs/decisions/](docs/decisions/README.md) (why). Link, don't copy.
- [docs/ROADMAP.md](docs/ROADMAP.md) is append-only and low-churn — add a line when a phase closes, don't rewrite it.

Full operating rules — including the Inbox and Paused-Questions protocols — live in [docs/reference/DEV_LOOP.md](docs/reference/DEV_LOOP.md) (dev) and [docs/reference/QA_LOOP.md](docs/reference/QA_LOOP.md) (QA).
