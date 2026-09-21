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
- **Environment split:** the QA loop uses the QA cluster (`qa-iris-a`, `qa-iris-b`, `qa-lemmy`, `qa-mastodon`); the dev loops use the dev1/dev2 clusters (`dev1-*` / `dev2-*`); `iris.luit.ink` is the production/public FQDN and is not used for dev or QA testing.
- **Ownership:** each loop writes only its own PLAN.md sections and files (the [ownership map](docs/reference/DEV_LOOP.md#ownership-map-planmd) is binding).

## Live state

- **Topology:** per-agent environment stacks (see [DUAL_DEV_PROTOCOL.md](docs/reference/DUAL_DEV_PROTOCOL.md)). dev1 → `dev1-*` (10xxx), dev2 → `dev2-*` (20xxx), qa → `qa-*` (30xxx), prod → `iris.luit.ink` (8088). Each agent builds from its own worktree.
- **Verified 2026-09-21:** all 13 FQDNs serve HTTP 200 over TLS via the gateway proxies; WebFinger confirms each Iris instance advertises its own FQDN (e.g. `alice@dev1-iris-a.luit.ink` → `acct:alice@dev1-iris-a.luit.ink`). Gateway `stack.sh` updated to bring up the `proxies/iris-environments` compose (4th stack).
- **Dev1 stack:** `dev1-iris-a` + `dev1-iris-b` healthy (fresh build from `a915d9c4`, 2026-09-21).
- **Dev2 stack:** `dev2-iris-a` + `dev2-iris-b` healthy (fresh build from `a915d9c4`, 2026-09-21).
- **QA stack:** `qa-iris-a` + `qa-iris-b` healthy (fresh build from `a915d9c4`, 2026-09-21). QA must re-verify S40/S2/S14 (fixed) + directory fix (awaiting re-verify) on the new stack.
- **Prod:** `iris-web` on `e8988af6` (S17 fix + QA status updates), port 8088.

    ## Active Slice

- **Directory "All known" omits remote actors (FIXED `401c08b5`, awaiting QA re-verify).** QA Pass 202: the directory's "All known" tab omits remote actors bidirectionally (A does not show ii-b1, B does not show ii-a1), while search finds them. Root cause: `GlobalSearchService.IsSameInstanceActor` dropped any actor with a `preferredUsername` whose IRI was not on the local instance base — which incorrectly excluded remote Iris actors (they carry a handle too). Fix (refined `401c08b5`): a non-canonical actor whose handle matches a LOCAL actor's handle is a stale local row (S5) and is dropped; a non-canonical actor whose handle is NOT local is a remote peer and is kept. This preserves S5 (stale local alice is dropped) while keeping remote Iris actors. **Dev cluster rebuilt 2026-09-21 on `401c08b5`.** QA must rebuild the QA cluster to re-verify.
- **S36 home-feed (DIAGNOSIS COMPLETE — in-process root cause NOT reproducible; regression-net added; live evidence requested).** The server path is provably correct in-process: (a) new regression-net test `S36_OwnPostsSurvive_CapSaturationByLocalFollowPlusBrokenRemotePlusDeliveredContent` (own posts + a local follow saturating the MaxItems=200 cap + a broken remote follow whose delivered content lives in the object store) PASSES — own posts survive; (b) the existing S36 live-wire-shape tests (actor-doc noise + Group Create + own Create + follow Create) all pass; (c) the live dev container's `Iris.Server.dll` was byte-verified to contain the S25 delivered-content union (`GetDeliveredContentAsync`) — the deployed build is current. The live drop is therefore **data/environment-specific** (the live actors have accumulated S25/S32 noise the fresh reproductions don't have). **QA request:** on the next re-verify, capture the live actor's outbox IRIs + activity types (`SELECT "Id", "Type" FROM "Activities"` joined to the outbox box-items) and the exact signed `GET /ap/v1/u/{me}/feed` body — the in-process repros cannot see the live data shape.
## Dev Queue

**Work order (dev, per [DEV_LOOP.md step 2](docs/reference/DEV_LOOP.md#the-loop)):** Inbox → Re-verify debt → this queue (blockers → S2-sev QA fixes → feature scope). Keep it sorted; cap ~7 items, link the rest to plan docs.

**Inbox (user/loop injections — action oldest first):**

**Investigate Home feed** - Home feed seems to be missing a lot of content that shows in notifications, we should see content from people we follow as well as our own posts in the feed. → **S36** (CRITICAL: feed empty even for own posts; investigating feed query path).

**Investigate Directory** - The directory is no longer listing all accounts, we are only seeing local on both tabs. → **Fix `401c08b5` deployed** (remote actors with a preferredUsername are now kept in the "All known" scope; S5 stale-local-row drop preserved). Awaiting QA re-verify.

**Directory Paging** - We are showing only the first 100, we should continue to page out all of the records with infinity scroll.

**Re-verify debt (committed fixes QA must confirm on a current build):**

- *(cleared Pass 27, 2026-09-20 — build now current (`a45f3d4` deployed); QA live-verified **S6** remote Join, the **S4** local-community following facet, the **S8** "All on this instance" list, and **S16** poll-vote persistence. S4's remote-community display caveat remains open in [s04](docs/qa/s04-communities-following-remote.md).)*

**QA fixes (by severity — one doc each in [docs/qa/](docs/qa/README.md)):**

- **S2-sev:** **S36** [home-feed omits posts](docs/qa/s36-home-feed-omits-posts-and-is-polluted-with-actor-document-activity.md) — **OPEN (66th consecutive pass; home feed is the ONLY broken surface of 8; awaiting dev fix)**. **S24-D2** [foreign activities in local outbox](docs/qa/s24-cross-instance-follow-state-inconsistent.md) — linked to S36. **S2, S3, S4, S14, S17, S19, S20, S21, S35** — open.
- **S3-sev:** **S38** — open.




**Feature scope:**

- *All prior feature-scope items are **COMPLETE** — rolled up into [ROADMAP.md](docs/ROADMAP.md). No open feature-scope slice remains.*
## QA Queue

*(QA-owned - see [QA_LOOP.md](docs/reference/QA_LOOP.md). Findings live in [docs/qa/](docs/qa/README.md); this is a count + pointer only.)*

- **Open:** S2, S3, S4, S14, S17, S19, S20, S21, S24-D2, S35 (S1), **S36 (S2, OPEN — 66th consecutive pass; home feed is the ONLY broken surface of 8; awaiting dev fix)**, S38 (S3). Count in [docs/qa/README.md](docs/qa/README.md).
- **CLOSED:** S26, S27, S28, S29, S30, S31, S32, S33, S34, S37, S39.
- **BLOCKERS (operator action):** (1) Mastodon M2-M12 - registrations: false + imuser password unknown + actor docs 404 (S35). (2) Lemmy L2-L12 - iluser pending approval.

## Paused Questions

> **The loops never block on a question.** When either loop hits something it can't decide (a product fork, a conflict, a destructive action), it logs a short entry here and **moves on to another item** (stashing in-flight work first). A human clears this list when convenient; cleared entries fold their answer into the relevant slice/change doc. See [DEV_LOOP.md - Blocking without stopping](docs/reference/DEV_LOOP.md#blocking-without-stopping).

- **`git pull --rebase` conflict (recurring, 2026-09-21):** conflict on `PLAN.md` + `docs/qa/passes.md` when applying QA commit `9a251708` (Pass 205). This is the 3rd+ occurrence of the same conflict. The QA commits appear to be duplicates of already-merged work (the pass content is already in the local `passes.md`). Aborted the rebase; the local branch is ahead of the remote by 15+ commits. A human needs to either force-push the local branch or resolve the duplicate commits on the remote. **Blocks:** clean `git pull --rebase` (step 1 of every turn).

## Recently Completed

- **Directory "All known" fix (401c08b5):** refine `IsSameInstanceActor` to distinguish stale local rows (S5) from remote peers. A non-canonical actor whose handle matches a LOCAL actor's handle is dropped (S5); a non-canonical actor whose handle is NOT local is kept (remote Iris actor). Preserves S5 while fixing the directory.
- **S36 (home feed):** **OPEN (66th consecutive pass).** Home feed is the ONLY broken surface of 8 (notifications, actor page, object-detail, profile, community feed, directory, search all work). Awaiting dev fix to the home feed query.
- **S32, S24-D1, S37, S30, S36 (in-process investigation) — see change docs + finding docs.**

  ## Keeping the docs lean

- **This file is the *only* one an agent must read and update every turn. Keep it short: bounded lists, not narrative.**
- **Clean up finished items roll them up - it's ok to keep some recently completed, but keep it short and point to change docs**
- Detail belongs in [docs/plans/](docs/plans/) (forward-looking scope), [docs/changes/](docs/changes/README.md) (what was built), or [docs/decisions/](docs/decisions/README.md) (why). Link, don't copy.
- [docs/ROADMAP.md](docs/ROADMAP.md) is append-only and low-churn — add a line when a phase closes, don't rewrite it.

Full operating rules — including the Inbox and Paused-Questions protocols — live in [docs/reference/DEV_LOOP.md](docs/reference/DEV_LOOP.md) (dev) and [docs/reference/QA_LOOP.md](docs/reference/QA_LOOP.md) (QA).
