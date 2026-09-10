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

## Now

- **Phase 62 — Bug hunt (in progress).** Full-system manual pass. App was modified without focus for several hours and has odd bugs. 62.1 documents defects only (no fixes), 62.2 clears blockers + finishes the review, 62.3 fixes, 62.4 re-passes until clean.
- **Phase 63 — UI/UX review (next).** Usability + presentation: is the data well organized, readable, functional. Detailed visual review + brainstorming on what makes a good interface.
- **Phase 64 — Request spam & network efficiency (next-next).** Distilled from 62's network notes. **Priority: reduce the *number* of calls the client makes** (duplicate/redundant fetches on load, refetch-on-re-render, N+1 fan-outs) — raw latency is secondary. Topics added during 62.1.
- **Phase 65 — (to be distilled from 63's findings at 63's closeout).**
- **Phases 62–69 — exploratory buffer.** 66–69 are reserved slack: if any phase overruns (more blockers than expected, new issue classes found, re-passes needed), work rolls forward into the buffer instead of compressing later phases. Use them as needed.
- **Phase 70 — Content & media improvement (the start of "improving").** After the exploratory buffer: ensure pictures/media render properly; ensure we can browse remote users and view their content; ensure we can post content and view it within our instance.

## Active Slice

- *(none — 63 is complete; next slice is 64, see Up Next).*

**Phase 63 — COMPLETE** (63.1 visual/usability pass + 63.2 design/IA review).
Change docs: [630 tracker](docs/changes/630-ux-review-tracker.md) (63.1: 3 findings
U-01/U-02/U-03, all fixed in-slice + verified live) and [632](docs/changes/632-ux-ia-design-review.md)
(63.2: 3 IA findings; IA-01 root-redirect + IA-02 hide-admin-links-from-non-admins
implemented in-slice + verified live on `:8090`; IA-03 nav grouping deferred as
acceptable-as-is). **Build 0 warn/0 err; full suite green** (one known-flaky
delivery test fails in the full run, passes in isolation).

**Note for the loop (stale-WASM, re-confirmed this turn):** the server
`Iris.Web.csproj` `BuildAndCopyClient` target only republishes the client when
`Iris.Web.Client/publish/` is missing — a razor change requires deleting that
dir first (`python3 -c "import shutil;shutil.rmtree('apps/Iris.Web.Client/publish',ignore_errors=True)"`)
or the stale WASM is served. **Additionally, the Playwright MCP browser profile
caches the Blazor WASM by content hash across origins/sessions** — after a
razor republish the browser may still load a prior build's WASM even on the same
origin (a hard reload is not enough). The reliable workaround is to **launch the
server on a fresh origin (new port)** so the browser has no cached WASM for it.

### Phase cadence (rolling 3-phase pattern)

Work for **the phase after the next** is distilled from **the current phase**, while **the next phase** fixes problems found in the current. New work stays 1 phase ahead of fixing.

| Phase | Role | Status |
|---|---|---|
| 62 | Bug hunt — find + document + fix | **complete** (62.4 re-pass converged; [621](docs/changes/621-phase62-closeout-bug-hunt.md)) |
| 63 | UI/UX review — usability + presentation + design/IA | **complete** (63.1 visual pass [630](docs/changes/630-ux-review-tracker.md); 63.2 design/IA [632](docs/changes/632-ux-ia-design-review.md)) |
| 64 | Request spam & network efficiency — cut redundant/duplicate calls on load (7 topics drafted in [620 tracker](docs/changes/620-bug-hunt-tracker.md#phase-64--request-spam--network-efficiency-draft-topics)) | **next** |
| 65 | distill from 63 | planned |
| 66–69 | exploratory buffer — overrun/slack for 62–65 (or later) phases | reserved |
| 70 | Content & media improvement — media rendering, remote browsing, post-and-view-in-instance | queued |

- Each phase's closeout writes its change doc AND distills the next-next phase's topics into this file.
- Defects found in a phase are fixed in that phase's fix slices (62.3/62.4); UX-class findings route to 63; inefficiency findings route to 64.
- **Buffer (66–69):** phases 62–65 may overrun into 66–69 without renumbering — if 62's re-passes don't converge, 62.5/62.6 land in the buffer; if 63/64 find more than planned, their extra slices do the same. The buffer is consumed in order and logged in this file when used.
- **Operator latitude (binding for 62–69):** I am authorized to do whatever keeps testing moving — recreate containers, create new test users, seed content, restart services, adjust test data. Log significant interventions in the slice's tracker notes.

### Loop protocol (Phase 62 — bug hunt)

Each slice is a **Playwright-driven pass**, not a code-first slice.

1. **Build**: `cd /workspace && dotnet build apps/Iris.Web/Iris.Web.csproj -c Release`
2. **Docker**: `cd /workspace/apps/Iris.Web && docker compose build iris-web && docker compose up -d --force-recreate iris-web` — **avoid `--no-cache`** (repeated no-cache fills the host disk; if the build fails with `No space left on device`, run `docker builder prune -af` first).
3. **Clean entry (every slice, every re-verification)**: close the browser entirely, clear cookies + storage, reopen, enter the app fresh. Never carry state between slices or between a defect and its re-verification.
4. **Manual test (MCP Playwright)** at `https://iris.luit.ink`:
   - **Primary account: `andrew` / `Password1`** (has real content + external contacts — use it to evaluate every page).
   - **Secondary accounts**: `bob`, `carol`, `dave` (register as needed) for multi-account flows (follows, communities, moderation, notifications).
   - **Authless pass**: every page visited signed-out — verify gating (302 to login), no data leaks, no console errors, sensible signed-out UI.
    - **Work the page inventory, not "every page"**: the tracker's **Page coverage** table lists all 18 routes. Work it top-to-bottom; mark each route's signed-in + authless boxes as done, or `skipped(<reason>)`. Update the tracker's **Resume checkpoint** at the end of the slice so the next slice continues where this one stopped — never restart from scratch.
    - **Deep dive per page**: exercise every control, every state (empty/populated/error), deep links + hard refresh on each route.
    - Capture **console errors** (`browser_console_messages`). For screenshots, call the Playwright screenshot tool **with no `filename`** (it can't write to an arbitrary path); it auto-saves to `tmp/.playwright-mcp/page-<ts>.png` and returns that path — cite the returned path in the tracker rather than promising to attach a file.
    - **Network watch — count calls, not bytes (always on)**: the point is to catch **request spam** — duplicate/redundant calls fired when a page or control loads (same fetch twice on mount, a refetch on re-render, an N+1 fan-out). For each distinct request pattern record: method+path, status, **how many times it fired (the count is the spam signal)**, and *what triggered it* (which load/control). These notes are the raw material for **Phase 64** (request-spam reduction).
    - **MCP Runs in docker** - If the MCP server seems down or stuck you can restart the container
5. **Triage**: log every finding (page, repro, expected vs actual, **class** + **severity**) in the **shared tracker** ([docs/changes/620-bug-hunt-tracker.md](docs/changes/620-bug-hunt-tracker.md)). Class is the routing axis (blocker→62.2, bug→62.3, UX→63, perf→64); severity (S1/S2/S3) sets priority *within* the class. Set both on every row.
6. **Fix in scope** (62.2+ only — 62.1 documents only): implement fixes for this slice's assigned defects; **re-verify each fix from a clean entry** (step 3) and record the evidence in the finding's `Verify` cell (`console-clean + <control/state> works`, optionally + the auto-saved screenshot path) before flipping Status to `fixed`. No evidence, no `fixed`.
7. **Web tests**: `cd /workspace && dotnet test --no-build -c Release` — keep passing tests; **delete** any test broken by the change; **skip/comment out** any single test >15 s (find offenders via `dotnet test tests/Iris.Web.Tests -v n --logger "console;verbosity=detailed"` per-test timings). No new coded tests. **Every deleted or skipped test is logged in the tracker's Test-debt ledger** (test name, action, reason, restore-by) — no silent deletions; 62.4 closeout reviews the ledger.
8. **Update PLAN.md**: move the finished slice to Recently Completed; keep Up Next sorted by priority (blockers first). At phase closeout: distill the next-next phase's topics (62 → 64; 63 → 65) into this file.

### Slices (Phase 62)

| Slice | Scope |
|---|---|
| **62.1** | Full-page deep-dive — **documentation only, no fixes.** Work the tracker's Page-coverage inventory (all 18 routes), signed-in (as `andrew`) + authless, every control + state, deep links + refresh. Network watch throughout (count calls per distinct request pattern — the spam signal). All findings → shared tracker. Phase 64 topics drafted from network notes. **Timebox: ~5 routes or ~45 min per turn — stop, checkpoint, and continue next turn; roll overflow into the buffer, don't marathon.** |
| **62.2** | Fix blockers (anything holding up the review) + finish the review of pages 62.1 could not complete. |
| **62.3** | Fix remaining bugs (non-blocker defects from the tracker). |
| **62.4** | Re-pass (full deep-dive again from clean entries) until zero open blocker/bug findings; then 62 closeout (change doc + distill 64 topics + review Test-debt ledger). **Stop condition: two consecutive clean re-passes, or 3 re-passes max — whichever first. If still dirty at 3, stop and roll the remainder into the buffer (66–69) rather than looping.** |

## Up Next

- 63.1 — UI/UX visual/usability pass, route-by-route — **complete** (3 findings U-01/U-02/U-03 fixed in-slice + verified; [630 tracker](docs/changes/630-ux-review-tracker.md))
- 63.2 — UI/UX design/IA review — **complete** (3 IA findings; IA-01 root-redirect + IA-02 hide-admin-links implemented in-slice + verified live on `:8090`; IA-03 nav grouping deferred; [632 change doc](docs/changes/632-ux-ia-design-review.md))
- 64 — request spam & network efficiency: 6 topics already drafted in the [620 tracker](docs/changes/620-bug-hunt-tracker.md#phase-64--request-spam--network-efficiency-draft-topics) (actor N+1, proxy N+1, per-item likes/shares fan-out, public-feed double-fetch) **+ 1 new from 63.1:** deleted-account notifications fire avatar proxy fetches that 410-Gone (noisy; skip when no cached avatar)
- 65 — (distilled from 63 at 63's closeout)
- 66–69 — exploratory buffer (consumed in order if any of 62–65 overruns)
- 70 — content & media improvement: media/pictures render correctly · browse remote users + view their content · post content + view it within the instance

## Inbox

- *(empty)*

## Paused Questions

- None currently.

## Recently Completed

- **63.2 — UI/UX design & information-architecture review — Phase 63 COMPLETE** ([change doc 632](docs/changes/632-ux-ia-design-review.md)): the "what makes a good interface" half of 63. Reviewed IA across nav, footer, root `/`, and `/home` timeline (live on `:8090`, `andrew`+`alice`). 3 findings: **IA-01** (med) root `/` was a dead-end interstitial for signed-in users → **fixed in-slice** (`Home.razor` now redirects signed-in users to `/home` in `OnInitializedAsync`; authless landing unchanged) — verified: `andrew`+`alice` login → auto-redirect to `/home`, authless `/` still shows hero+Public timeline. **IA-02** (low) admin footer links shown to all users → **fixed in-slice** (`MainLayout.razor` wraps Admin/Moderation/Dashboard in `@if (IsAdmin)`) — verified: non-admin sees only NodeInfo/WebFinger/Status, admin sees all 6. **IA-03** (low) flat 9-link nav — **deferred** (fits all widths, mobile hamburger already groups; revisit if nav grows past ~11 links). Build 0 warn/0 err; full suite green (one known-flaky delivery test fails in the full run, passes in isolation). **Loop note logged:** the Playwright MCP browser profile caches the Blazor WASM by content hash — after a razor republish a fresh origin (new port) is required to bypass the stale cached WASM.
- **63.1 — UI/UX visual & usability pass, route-by-route** ([630 tracker](docs/changes/630-ux-review-tracker.md)): full route-by-route visual/usability pass (Home, Search, Communities, Profile, Settings×6, Directory, Compose, Notifications, Actor detail, Object detail, Register, + 390px mobile). 3 findings, all fixed in-slice + verified live: **U-01** (low) base `a` now underlined (WCAG 1.4.1); **U-02** (low) auth `.field label` now matches `.form-group label` (uppercase/600/letter-spaced); **U-03** (med) Notifications active filter tab was never highlighted (CSS `[aria-pressed="true"]` never matched a boolean binding → razor now binds string `"true"`/`"false"` + CSS selector widened). Routed to 64: deleted-account notifications fire avatar proxy fetches that 410-Gone. Contrast ≥ 6:1 everywhere, no horizontal overflow at any width.
- **62.4 — clean-entry re-pass CONVERGED + Phase 62 closeout** ([change doc 621](docs/changes/621-phase62-closeout-bug-hunt.md)): two consecutive clean re-passes from a fresh login on a DB-backed same-origin server (`localhost:8089`, `IRIS_ADVERTISE_BASE=https://iris.luit.ink`, Postgres) re-verified every 62.3 fix end-to-end — B-015 (`andrew`→302 `/`), B-016 (`/home` 0 console errors), B-001 (Directory → real IRIs), B-003 ("Show posts by <name>", no `RangeSelectIterator`), B-010 (14 self-delete captions), B-011 (0 raw numeric IDs), B-014 (compose: signed POST to outbox, "Posted (HTTP 202)", note on home timeline), B-005 (profile "Your posts" no foreign posts); `/communities`/`/search`/`/settings` 0 errors; authless gates to sign-in. **No open blocker/bug findings remain.** Remaining console noise is expected (410s on gone remote actors + one stale `localhost:8088` alice dev-DB row = data drift, not a code defect). Closeout: change doc 621 written; Test-debt ledger empty (no tests deleted/skipped in 62.x); 64 request-spam topics drafted in the 620 tracker; 63 (UX) now active. **Phase 62 COMPLETE.**
- **62.3 — fixed the remaining bug-class findings + two deeper S1 defects, live-verified** ([tracker](docs/changes/620-bug-hunt-tracker.md)): **B-003** (S2) Directory card `aria-label` leaked a `RangeSelectIterator` type name (`@Actor.Name` is a `List<string>`) → `DisplayName` property; **B-010** (S3) Notifications Delete rows linked to the actor IRI → `NotificationRow.razor` detects self-delete and renders a "deleted their account" caption; **B-011** (S3) raw numeric remote IDs shown as names → `DisplayNameFallback(iri)` (numeric last-segment → URI host); **B-014** (S2) compose "no POST" was the B-016 CORS block + MCP-input artifact (signed POST now same-origin). **B-015** (S1, new) login always "unknown username" — `WebAppFactory` bound the in-memory `IUserAccountStore`/`IInstanceMetadataStore` via `TryAddSingleton` *before* `AddEntityFrameworkPersistence`'s EF store, shadowing it (first-registration-wins); moved the in-memory defaults into the in-memory branch. **B-016** (S1, new) cross-origin CORS on every local AP read/write — the 55.2 override set `advertiseBase` to the browser origin so `SameOriginApHandler` never rewrote the FQDN; `Program.cs` now keeps the original FQDN as the rewrite base (`rewriteBase` → `SameOriginApHandler`) while the effective base still drives `DialBaseUri`/`ProxyBaseUrl`/`NamespaceIri` (55.2 preserved). Build clean (0 warn/0 err), full suite green (one flaky 16s delivery test passes in isolation). Verified on a DB-backed same-origin server (`localhost:8089`, `IRIS_ADVERTISE_BASE=https://iris.luit.ink`): 0 CORS errors, home timeline + Directory render, `andrew` login → 302 `/`.
- **62.2 — fixed the two S1 blockers, live-verified** ([tracker](docs/changes/620-bug-hunt-tracker.md)): **B-001** Directory actor links were the literal `Actor.Id` placeholder (`DirectoryCard.razor:8` `ActorId="Actor.Id"` — a Razor string literal, not a C# expression) → `@Actor.Id`; all 3 People cards now link to real IRIs. **B-005** profile "Your posts" showed 4 foreign RayvenMX posts (`OutboxFilter.IsContentItem` ignored author) → added `OutboxFilter.IsOwnContentItem(item, authorIri)`, Posts tab now filters to the signed-in user's own `Create`s (Replies/Likes/Requests unchanged). Also confirmed andrew's role is `User`, so the 4 `/admin/*` routes are correctly role-gated (closed the "admin render pending" question). Build clean + full suite green. **Build gotcha logged:** `apps/Iris.Web.Client/publish/` caches a stale WASM (the `BuildAndCopyClient` MSBuild target skips re-publish if `blazor.webassembly.js` exists) — clear that dir before a Docker rebuild or you test an old client.

## Keeping the docs lean

- This file is the *only* one an agent must read and update every turn. Keep it short: bounded lists, not narrative.
- Detail belongs in [docs/plans/](docs/plans/) (forward-looking scope), [docs/changes/](docs/changes/README.md) (what was built), or [docs/decisions/](docs/decisions/README.md) (why). Link, don't copy.
- [docs/ROADMAP.md](docs/ROADMAP.md) is append-only and low-churn — add a line when a phase closes, don't rewrite it.

Full operating rules, including the Inbox and Paused-Questions protocols, live in [docs/reference/AUTONOMOUS_LOOP.md](docs/reference/AUTONOMOUS_LOOP.md).
