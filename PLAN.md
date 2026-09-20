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
- **Ownership:** each loop writes only its own PLAN.md sections and files (the [ownership map](docs/reference/DEV_LOOP.md#ownership-map-planmd) is binding).

## Live state

- **Deployed commit:** `0fe7f0f` (code) + S4 communities-following-remote fix (code `14817` commit, docs-only delta since `0fe7f0f`). Rebuilt (`--no-cache`) + redeployed 2026-09-20, `irisweb-iris-web-1` recreated, healthy. **Live-verified (S4):** signed-in `andrew` Communities → Following shows local communities **plus** the remote **"Iris Interop"** (`lemmy.luit.ink/c/interop`) with a Leave button; client `POST /ap/v1/proxy/…/lemmy.luit.ink/c/interop` → 200; 0 console errors.
- **Container:** `irisweb-iris-web-1` — current (built with the S4 fetch-by-IRI fix on top of the S2/S14 anonymous-proxy signing fix).
- **Note:** **S4 remote-community Following-tab gap FIXED (2026-09-20):** `Communities.razor` `ResolveFollowingCommunitiesAsync` now fetches a followed IRI not in the local search via `Ui.GetActorAsync` and keeps `Group` results (the behavior its doc comment always promised). **Live-verified:** the followed remote interop community renders in the Following tab (22 consecutive QA passes had it missing). [change doc 14817](docs/changes/14817-communities-following-remote-fetch.md). Prior: S2/S14 anonymous-proxy signing [14816](docs/changes/14816-anonymous-proxy-signs-as-instance-actor.md); ⑤ un-follow feed-cache invalidation [14815](docs/changes/14815-unfollow-feed-cache-invalidation.md); Lemmy Undo 400 fix [14814](docs/changes/14814-lemmy-undo-follow-embedded.md); shared-inbox route [1598](docs/changes/1598-shared-inbox-route.md).

## Active Slice

- **S24 — cross-instance follow state inconsistent — NEXT (2026-09-20).** S2-sev interop bug: after a cross-instance Follow, the follower's **Following** tab omits the remote actor, a spurious self-follow appears in the outbox, and a GET of the remote actor 404s. Next in the interop batch (S24–S33, all S2-sev, from Interop A2). See [s24](docs/qa/s24-cross-instance-follow-state-inconsistent.md). **S4 (remote communities missing from the Following tab) — DONE (2026-09-20):** `ResolveFollowingCommunitiesAsync` fetches a followed IRI not in the local search via `Ui.GetActorAsync` and keeps `Group` results; live-verified the remote interop community renders in the Following tab. [change doc 14817](docs/changes/14817-communities-following-remote-fetch.md). **S2/S14 (proxy 401 for unsigned GETs) — DONE (2026-09-20):** the anonymous seam now signs the public GET as the local instance actor; live-verified 200 on mastodon.social. [change doc 14816](docs/changes/14816-anonymous-proxy-signs-as-instance-actor.md).

## Dev Queue

**Work order (dev, per [DEV_LOOP.md step 2](docs/reference/DEV_LOOP.md#the-loop)):** Inbox → Re-verify debt → this queue (blockers → S2-sev QA fixes → feature scope). Keep it sorted; cap ~7 items, link the rest to plan docs.

**Inbox (user/loop injections — action oldest first):**

- *(empty)*

**Re-verify debt (committed fixes QA must confirm on a current build):**

- *(cleared Pass 27, 2026-09-20 — build now current (`a45f3d4` deployed); QA live-verified **S6** remote Join, the **S4** local-community following facet, the **S8** "All on this instance" list, and **S16** poll-vote persistence. S4's remote-community display caveat remains open in [s04](docs/qa/s04-communities-following-remote.md).)*

**QA fixes (by severity — one doc each in [docs/qa/](docs/qa/README.md)):**

- *(empty — S19 complete)*




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

- **S4 remote communities missing from the Following tab — fixed (2026-09-20):** `Communities.razor` `ResolveFollowingCommunities()` matched each followed IRI against the **local search cache** only and had no fetch-by-IRI fallback, so a followed REMOTE community was silently dropped (re-confirmed 22 consecutive QA passes). It is now `async` (`ResolveFollowingCommunitiesAsync`) and, for each followed IRI not in the local search, fetches the actor document via `Ui.GetActorAsync` (routes a remote through the same-origin proxy) and keeps the doc only if it is a `Group`; a per-IRI fetch failure is swallowed so one unreachable remote doesn't break the tab. Web-UI change verified live per the web test policy (no harness coverage). `Iris.Web.Tests` 108 pass, 0 fail; build 0/0. **Live-verified:** signed-in `andrew` Following tab shows local + the remote "Iris Interop" (Leave button; `POST /ap/v1/proxy/…/lemmy.luit.ink/c/interop` → 200); 0 console errors. Deployed (container healthy). [change doc 14817](docs/changes/14817-communities-following-remote-fetch.md)
- **S2/S14 proxy 401 for unsigned GETs — fixed (2026-09-20):** the anonymous (signed-out) proxy seam relayed public remote GETs **unsigned**, but a strict remote (mastodon.social) requires a valid HTTP signature even for a public read (unsigned → 401 "Request not signed"). `ProxyHandler` now signs the anonymous GET as the **local instance actor** (`options.InstanceActorId`, key registered + served at the instance root) via the `X-Iris-Actor` override; the authenticated path is unchanged. Anonymous-retry test repurposed to assert signed-as-instance-actor; `Proxy_ResignsAsActingActor_NotInstanceActor` still passes. Full fast suite green: 1402 passed, 0 failed. **Live-verified:** signed-out proxy GET of mastodon.social actor → 200 (was 401). Deployed (container healthy). [change doc 14816](docs/changes/14816-anonymous-proxy-signs-as-instance-actor.md)
- **⑤ Mastodon un-follow fan-out fix (2026-09-20):** after an un-follow, the un-follower's home feed (`GET /u/{handle}/feed`) served the **stale** per-actor feed cache (30 s TTL) and kept showing the unfollowed user's posts. Added `IFollowFeedService.InvalidateActorFeedCache` (no-op default) + `FeedService` impl (`_feedCache.TryRemove`), called in `OutboxPublishHandler` on a `Follow` (gained a target) and an `Undo` of a `Follow` (lost a target). The delivery side was already correct (QA 993); this closes the client-facing feed-cache gap. New regression test drops only the named actor's entry. Full fast suite green: 1402 passed, 0 failed. Deployed (container healthy). [change doc 14815](docs/changes/14815-unfollow-feed-cache-invalidation.md)
- **Lemmy Undo (unfollow) 400 fix (2026-09-20):** the outbound Undo of a Follow now **embeds the original Follow** (Lemmy's untagged-enum parser rejects a bare IRI link → 400 → dead-letter). Person + community outbox paths fixed; stored activity keeps the bare link (only the outbound delivery embeds — backward-compatible). New regression test asserts the embedded shape. 1401 passed, 0 failed. **Live-verified:** Follow→200 (1→2), Undo→200 (2→1, no dead-letter). [change doc 14814](docs/changes/14814-lemmy-undo-follow-embedded.md)
- **Shared-inbox route (2026-09-20):** `POST /ap/v1/shared-inbox` implemented (was advertised but unhandled — a shared-inbox-preferring sender like Mastodon had deliveries silently dropped). Fans content (Create/Announce) out to the author's local followers; routes object-addressed (Follow/Accept/Undo) to the object. 4 new integration tests. 1891 passed, 0 failed. [change doc 1598](docs/changes/1598-shared-inbox-route.md)


  ## Keeping the docs lean

- **This file is the *only* one an agent must read and update every turn. Keep it short: bounded lists, not narrative.**
- **Clean up finished items roll them up - it's ok to keep some recently completed, but keep it short and point to change docs**
- Detail belongs in [docs/plans/](docs/plans/) (forward-looking scope), [docs/changes/](docs/changes/README.md) (what was built), or [docs/decisions/](docs/decisions/README.md) (why). Link, don't copy.
- [docs/ROADMAP.md](docs/ROADMAP.md) is append-only and low-churn — add a line when a phase closes, don't rewrite it.

Full operating rules — including the Inbox and Paused-Questions protocols — live in [docs/reference/DEV_LOOP.md](docs/reference/DEV_LOOP.md) (dev) and [docs/reference/QA_LOOP.md](docs/reference/QA_LOOP.md) (QA).
