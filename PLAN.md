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

- **131.4 — Performance: WASM payload reduction (LOW)** — 66 .wasm files; raw 11.4 MB, **gzipped 5.83 MB** (the "12.8 MB" figure was raw + JS). Target: < 5 MB gzipped. **Investigated + tabled (2026-09-13):** the dominant cost is BouncyCastle.Cryptography.wasm (5.1 MB raw / 2.56 MB gz, 44% of the payload), pulled in because Iris.Core's `Ed25519Key` references it. The WASM client never actually signs with Ed25519 at runtime (it always uses `WebCryptoSigningKeyFactory` → browser WebCrypto RSA), so BouncyCastle is dead weight in the payload — but the trimmer can't prove `Ed25519Key` is unreferenced (Iris.Core is a shared, non-trimmable project). The fix is to remove BouncyCastle from Iris.Core by implementing Ed25519 in pure .NET. **Attempted a pure-.NET `NativeEd25519` (RFC 8032, BigInteger-based extended coordinates) — base point + curve constant verified correct, but the scalar-mult/compress path produced wrong points for large scalars; bug not isolated before time-boxing. Reverted.** Backup of the partial implementation: `.tmp-ed25519-backup.cs`. Next attempt should debug `ScalarMult`/`Double`/`Add` against the RFC 8032 test vectors (a Python reference confirms the expected outputs). Alternative lower-risk path: multi-target Iris.Core (`net10.0` server with BouncyCastle + a WASM-friendly path) or accept the 5.83 MB gzipped payload as "good enough."

- **131.5 - Disable anti-fogery tokens** - These AF tokens are causing login issues, they may work find in production but cause a lot of greif as we change the implementation and redeploy, we should disable for now.

- **131.6 - Content caching policies** - During active development it seems our browser gets stuck with a cached versions of the website often. Investigate a method to create a shorter cache lifetime and or make it configurable so while we develope we can disable browser content caching to ensure a refresh actually fetches the new content.

- **132.1 - Interaction tracking** - Objects have Likes, Shares, and Replies; We need to be able to track the counts of these for local and remote objects. We should be able to display the number of Likes/Shares/Replies on any object we can see. Ideally when we fetch an object via proxy or upon seeing an announce or reply, we attempt to sync up the like/share/reply counts by walking the object collections if available, if the collections cannot be walked or return nothing, we would serve the counts of any known Likes/Shares/Replies.

- **132.2 - Object details view** - When an object is selected in the feed, we navigate to an object centric view, this view should be able to render several kinds of objects in a meaningful format. This view could have tabs like an actor view to display the Likes/Shraes (Actor cards for the actors that have Liked or Shared). The Replies should show as a thread stream under the object's main body. The thread stream should allow users to expand each reply to see the replies to that message.

- **133.1 - Object Card styling** - Object cards have a user header moderation bar and a post date - this has become too busy for a mobile screen and causes overflow for long usernames. The date (relative time) should be a single top line left justified, the actor handle and moderation bar should be the next (moderation bar left justified).

- **133.2 - Actor page/card styling** - There is a description under an actors name that can contain html tags, we should render this markup correctly.

- **134.1 - Directory listings** - The directory is only showing local actors, we need to be able to see all actors we interact with. The backend should be caching copies of the actor records in order to serve them as known content. These records should live in the databse so we can display them in the directory of known actors.

- **135.1 - Communities and the Lemmyverse** - We need to be able to interact with Lemmy communities. We need to better understand how the Lemmy servers expect us to interact. We should deploy a local lemmy server container and interact with it, we likely need to use public fqdns so let's utilize the iris-dev2.luit.ink fqdn for this. The lemmy container definitons/compose should live in it's own folder separate from our app with it's.

- **135.2 - Lemmy Test Planning** - Investigate how to configure and manage the Lemmy server instance, build a test plan and populate it as item 136; The goal will be to post content to Lemmy
 and see it replicated to Iris and vice versa. This is a big one and we will need to iterate on it spending a lot of time to carefully
.
**137 - Run the Lemmy intergration test plan** - Focus on driving the lemmy server and confirming interactions with Iris.


## Inbox

- *(empty)*

## Paused Questions

- *(empty)*

## Recently Completed

- **131.5 — HSTS + cookie hardening (DONE)** — Added `Strict-Transport-Security: max-age=31536000; includeSubDomains; preload` (HTTPS-only, placed after `UseForwardedHeaders` so the proxy's scheme is seen). Cookie already hardened (HttpOnly, SameSite=Lax, Secure=SameAsRequest) — verified. 0 console errors. [changes/13105](docs/changes/13105-phase131-hsts-cookie-hardening.md)

- **131.2 — Security: dependency audit + CSP review (DONE)** — Added 5 security headers (CSP, X-Content-Type-Options, X-Frame-Options, Referrer-Policy, Permissions-Policy). CSP uses `'wasm-unsafe-eval'` (Blazor WASM requirement). Dependency audit: no known CVEs. 0 console errors. [changes/13102](docs/changes/13102-phase131-security-headers.md)

- **131.1 — Performance: home feed load time (DONE, measurement-only)** — Cold load: ~1s to first post (target <3s met). WASM bootstrap (12.8 MB / 66 files) is the dominant cost. No optimization needed at this scale. [changes/13101](docs/changes/13101-phase131-home-feed-performance.md)

- **130.3 — General UI/UX review (fifth pass) (DONE)** — Visual sweep of all 11 pages (desktop 1400px + mobile 375px). 1 issue found: `/admin` 404 → fixed (route alias). 0 console errors. Review cycle converged (5 passes). [changes/13003](docs/changes/13003-phase130-ui-ux-review-pass5.md)

## Keeping the docs lean

- **This file is the *only* one an agent must read and update every turn. Keep it short: bounded lists, not narrative.**
- **Clean up finished items roll them up - it's ok to keep some recently completed, but keep it short and point to change docs**
- Detail belongs in [docs/plans/](docs/plans/) (forward-looking scope), [docs/changes/](docs/changes/README.md) (what was built), or [docs/decisions/](docs/decisions/README.md) (why). Link, don't copy.
- [docs/ROADMAP.md](docs/ROADMAP.md) is append-only and low-churn — add a line when a phase closes, don't rewrite it.

Full operating rules, including the Inbox and Paused-Questions protocols, live in [docs/reference/AUTONOMOUS_LOOP.md](docs/reference/AUTONOMOUS_LOOP.md).
