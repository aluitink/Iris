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

- **Deployed commit:** `65ccfa0` (HEAD; rebuilt + redeployed 2026-09-20, container `irisweb-iris-web-1` recreated, healthy)
- **Container:** `irisweb-iris-web-1` — current
- **Note:** the S7 fix (Directory external lookup via `Ui.GetActorAsync`) is live. S5 + S7 both verified fixed. S2/S14 proxy seam still 401s unsigned GETs.

## Active Slice

- **③④ Unified home feed — Phase 5: community management page (dev, 2026-09-20).** Server `DELETE /local/v1/c/{name}` (last-owner-only) + `DeleteCommunityAsync`. `/communities` repurposed as management page (my-communities + create + delete/leave). Top menu "Communities" → management page. See [docs/plans/unified-home-feed.md](docs/plans/unified-home-feed.md).

## Dev Queue

**Work order (dev, per [DEV_LOOP.md step 2](docs/reference/DEV_LOOP.md#the-loop)):** Inbox → Re-verify debt → this queue (blockers → S2-sev QA fixes → feature scope). Keep it sorted; cap ~7 items, link the rest to plan docs.

**Inbox (user/loop injections — action oldest first):**

- *(empty)*

**Re-verify debt (committed fixes QA must confirm on a current build):**

- *(cleared Pass 27, 2026-09-20 — build now current (`a45f3d4` deployed); QA live-verified **S6** remote Join, the **S4** local-community following facet, the **S8** "All on this instance" list, and **S16** poll-vote persistence. S4's remote-community display caveat remains open in [s04](docs/qa/s04-communities-following-remote.md).)*

**QA fixes (by severity — one doc each in [docs/qa/](docs/qa/README.md)):**




**Feature scope:**

- **②④⑤ Community simplification — unify members with followers (Lemmy-shape). ② Phases 1-6 DONE; ④ control surfaces + ⑤ live-interop re-verify remain.** Members = followers, Join/Leave → Follow/Undo, `manuallyApprovesMembers` gates the Follow, single **Follow** button (labeled **Join**/**Leave** for communities), dead `ICommunityStore` member methods retired (kept `EdgeKind.CommunityMember` for the startup migration). **Peering** is the single owner-only extra. Remaining: **④** the `/communities` **management** page + Profile **Communities** tab (with unified-home-feed P5/P6); **⑤** live Lemmy interop re-verify (gated by `IRIS_LIVE_INTEROP`). See [docs/plans/community-simplification.md](docs/plans/community-simplification.md) + [change doc](docs/changes/1001-inbox-2-community-simplification.md).
- **③④ Unified home feed — two tabs + bottom strip + community IA rework. Phase 1 DONE (Page drop fix, 2026-09-20); Phases 2-7 remain.** (1) `/home` gets two feed tabs (**Posts**/**Communities**) via a server-side `?source=people|communities` filter. ~~also fixes the `Page` drop in `IsContentItem`~~ **Done** — `Page` added to `IsContentItem` in `HomeTimeline.razor` + `Home.razor`; live-verified (Lemmy `Page` post renders in `/home`). (2) A **bottom control strip** (feed tabs left on `/home`, notifications badge right, global) — no compose button. (3) **Community IA rework:** `/communities` becomes a **management** page (owned communities: create / delete-when-last-owner / leave-when-co-owner / manage + a **Peers** section — needs a new last-owner-only `DELETE /local/v1/c/{name}`); **Profile** gains a **Communities** tab. See [docs/plans/unified-home-feed.md](docs/plans/unified-home-feed.md) + [change doc](docs/changes/1002-inbox-3-phase1-page-drop-fix.md). **Depends on** community-simplification Phases 1-4.
- **① Minor UI fixes — feed load feel.** (1)(2)(3) Done (moderation icons off feed cards, full-width header strip, boost hint dropped). (4) **Remaining:** server-side follow-feed caching/streaming (a significant server change, deferred to a dedicated slice). See [change doc](docs/changes/1000-inbox-1-card-header-polish.md).

## QA Queue

*(QA-owned — see [QA_LOOP.md](docs/reference/QA_LOOP.md). Findings live in [docs/qa/](docs/qa/README.md); this is a count + pointer only.)*

- **6 open. No blockers (all S2/S3-sev).** (Pass 40: S7 confirmed FIXED; S3, S4 remote, S16-UX, S17, S19 re-confirmed open.)
- **Top priority:** S19 (follow request acceptance UI missing — no Accept/Decline buttons, /requests 404), S3 (Create-IRI 404), S4 (remote communities missing from Following tab), S17 (profile tabs over-fetch), S16-UX (poll badge re-hydration). S2/S14 still blocked on dev (proxy 401).
- **Last pass:** 40 (2026-09-20). **Resume checkpoint:** see [docs/qa/passes.md](docs/qa/passes.md) — S13, S15, S7 fixed; S18 partially fixed. Remaining open: S3, S4 (remote), S16-UX, S17, S19, S2/S14 (blocked on dev).

## Paused Questions

> **The loops never block on a question.** When either loop hits something it can't decide (a product fork, a conflict, a destructive action), it logs a short entry here and **moves on to another item** (stashing in-flight work first). A human clears this list when convenient; cleared entries fold their answer into the relevant slice/change doc. See [DEV_LOOP.md — Blocking without stopping](docs/reference/DEV_LOOP.md#blocking-without-stopping).

- *(empty)*

## Recently Completed

- **③ Phase 4 — Wire home-feed tabs to `?source=` filter (2026-09-20):** `HomeTabState` scoped service bridges FeedBar ↔ HomeTimeline. Tab switch triggers `?source=people|communities` feed request; per-tab empty states. Live-verified: 0 console errors. [change doc](docs/changes/1584-home-feed-tab-source-wiring.md)
- **③ Phase 3 — FeedBar bottom control strip (2026-09-20):** `FeedBar.razor` in `MainLayout`: Posts/Communities tabs + global `🔔` bell. [change doc](docs/changes/1583-feedbar-bottom-control-strip.md)
- **③ Phase 2 — Server `?source=` filter (2026-09-20):** `GET /u/{handle}/feed?source=people|communities` filters by attributedTo Group. All suites green (Server 1380, Web 106).
- **S13 — Remote Lemmy object-detail 404 noise (2026-09-20):** Collection walks skipped for non-local objects. [change doc](docs/changes/1582-remote-lemmy-object-detail-404-noise.md)
- **S3 — Object-detail 404s local post collections (2026-09-20):** `ContentIri` resolves to Note IRI. [change doc](docs/changes/1581-object-detail-create-iri-404-collections.md)
- **S15 — Compose visibility hint misleading (2026-09-20):** `ComposeHint` visibility + type aware. [change doc](docs/changes/1580-compose-visibility-hint-misleading.md)
- **Inbox ① card header polish (2026-09-20):** moderation icons removed from feed cards (reserved for actor detail), header color strip extended to full width, boost "replying to" hint dropped. Feed load feel investigation deferred (server-side caching needed). [change doc](docs/changes/1000-inbox-1-card-header-polish.md)



  ## Keeping the docs lean

- **This file is the *only* one an agent must read and update every turn. Keep it short: bounded lists, not narrative.**
- **Clean up finished items roll them up - it's ok to keep some recently completed, but keep it short and point to change docs**
- Detail belongs in [docs/plans/](docs/plans/) (forward-looking scope), [docs/changes/](docs/changes/README.md) (what was built), or [docs/decisions/](docs/decisions/README.md) (why). Link, don't copy.
- [docs/ROADMAP.md](docs/ROADMAP.md) is append-only and low-churn — add a line when a phase closes, don't rewrite it.

Full operating rules — including the Inbox and Paused-Questions protocols — live in [docs/reference/DEV_LOOP.md](docs/reference/DEV_LOOP.md) (dev) and [docs/reference/QA_LOOP.md](docs/reference/QA_LOOP.md) (QA).
