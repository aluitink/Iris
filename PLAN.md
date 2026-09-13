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

- **135.1 - Communities and the Lemmyverse** - We need to be able to interact with Lemmy communities. We need to better understanding how the Lemmy servers expect us to interact. We should deploy a local lemmy server container and interact with it, we likely need to use public fqdns so let's utilize the iris-dev2.luit.ink fqdn for this. The lemmy container definitons/compose should live in it's own folder separate from our app. **135.1a DONE:** `RemoteCommunityPersister` persists remote community `Group` documents to the durable `ICommunityStore`; the `/ap/v1/actor?iri=...` endpoint now also serves a cached remote community (proven by tests). **135.1b DONE:** real Lemmy 0.19.20 deployed in its own `lemmy/` folder (own compose + Dockerfile + hjson config), advertising `https://iris-dev2.luit.ink`, on the shared Docker network; it serves a real community `test` (a proper ActivityStreams `Group` at `https://iris-dev2.luit.ink/c/test` with publicKey/inbox/followers/outbox) in exactly the shape the persister expects. **BLOCKED on the full live follow-interop** by external infra: the host's nginx reverse proxy 400s the required POST federation paths (`/local/v1/c/*/follow/*`, `/ap/v1/proxy/*`, Lemmy `/api/v3/follow`), and the running Iris container's actor Basic-auth creds don't match the PLAN notes (401/403 on direct container access). Unblocked when the operator whitelists those nginx POST paths + confirms/re-seeds live Iris actor creds. **135.1b(2) DONE (test):** `IrisActorDocumentFetcherTests.GetActor_RealLemmyCommunity_PersistsAndServesIt` feeds the verbatim real Lemmy `Group` document (from `iris-dev2.luit.ink/c/test`, with the full Lemmy shape: `@context` array, nested `source`, `sensitive`, `postingRestrictedToMods`, `endpoints.sharedInbox`, `featured`, `language`, `published`, `attributedTo`→`/moderators`) through deserialize → fetcher → persist → cached-actor-endpoint, asserting the Lemmy fields round-trip (the `Group` type in KristofferStrube.ActivityStreams 0.2.4 preserves extension fields). This is the strongest in-process proof Iris handles a real Lemmy community while the live follow is infra-blocked. **Remaining:** drive the real follow (Iris community `technology` → Lemmy `test`), confirm the `Group` lands in `ICommunityStore` + serves via `/ap/v1/actor?iri=https://iris-dev2.luit.ink/c/test`, then post content in both directions. [changes/13501b](docs/changes/13501b-phase135-lemmy-deployment.md) [changes/13501b2](docs/changes/13501b2-phase135-real-lemmy-group-roundtrip-test.md)

- **135.2 - Lemmy Test Planning** - Investigate how to configure and manage the Lemmy server instance, build a test plan and populate it as item 136; The goal will be to post content to Lemmy
 and see it replicated to Iris and vice versa. This is a big one and we will need to iterate on it spending a lot of time to carefully
.
**137 - Run the Lemmy intergration test plan** - Focus on driving the lemmy server and confirming interactions with Iris.


## Inbox

- *(empty)*

## Paused Questions

- *(empty)*

## Recently Completed

- **135.1b — Deploy a local Lemmy instance for real Iris ↔ Lemmy interop (DONE — deployment; live follow-interop blocked on external nginx + creds)** — Stood up a real, separate Lemmy 0.19.20 in its **own folder** (`lemmy/`) per the Phase 135.1 requirement, advertised under `https://iris-dev2.luit.ink` (replaces the iris2 test instance at that FQDN; the iris2 Postgres volume is preserved). `lemmy/docker-compose.yml` (postgres:16 + lemmy on the shared `irisweb_iris-web-net` network, host 8082→container 8536), `lemmy/Dockerfile` (`FROM dessalines/lemmy:0.19.20` + bakes the hjson config into the image — this host's Docker **file bind mounts are broken**, so config is baked not mounted), `lemmy/config/config.hjson` (hostname, `tls_enabled: true`, DB, pictrs `None`, first-run `setup` → `lemmyadmin` + "Iris Lemmy Interop" site). **Verified live:** Lemmy healthy (nodeinfo, node doc, `/api/v3/site` federation-enabled); a real community `test` created via the REST API serving a proper ActivityStreams `Group` at `https://iris-dev2.luit.ink/c/test` (publicKey/inbox/followers/outbox) — the exact document shape 135.1a's `RemoteCommunityPersister` persists; network path confirmed (shared Docker network). **Blocked on the full live follow-interop** by two external-infra issues: (1) the host nginx reverse proxy returns 400 on the required POST federation paths (Iris `/local/v1/c/*/follow/*`, `/ap/v1/proxy/*`, Lemmy `/api/v3/follow`) before they reach the app (direct container access bypasses the 400 and reaches the handler); (2) the running Iris container's actor Basic-auth creds don't match the PLAN notes (401/403 on direct access), so a community owner can't be authenticated to drive the follow. The core persistence logic remains proven by the passing 135.1a unit/integration tests. **Next:** once the operator whitelists the nginx POST paths + confirms/re-seeds live Iris actor creds, drive `POST /local/v1/c/technology/follow/https://iris-dev2.luit.ink/c/test` as the community owner, verify the `Group` lands in `ICommunityStore` + serves via `/ap/v1/actor?iri=…`, then post content in both directions. [changes/13501b](docs/changes/13501b-phase135-lemmy-deployment.md)

- **135.1a — Persist remote community (Group) documents for Lemmy interop (DONE, first slice)** — Iris cached remote **actor** docs (117.3) but had no equivalent for communities: a remote `Group` (a Lemmy community) was invisible after interaction. New `RemoteCommunityPersister` (Iris.Server.Security, mirrors `RemoteActorPersister`) persists a remote `Group` to the durable `ICommunityStore` (skips local IRIs, idempotent, best-effort). `IrisActorDocumentFetcher` now fetches the full object (`GetObjectAsync`) so a `Group` document is not dropped; a `Group` is persisted to the community store **and returned** (a Group IS an Actor) so inbound key resolution still validates community signatures (returning null broke 31 community federation tests → fixed by returning the Group). `/ap/v1/actor?iri=...` now also serves a cached remote community (200, stored Group as-is; 404 unknown). `RemoteCommunityPersisterTests` (6) + fetcher community test. Build clean; suite green (1141 Server tests, stable). [changes/13501](docs/changes/13501-phase135-remote-community-persister.md)

- **134.1 — Directory "All known": serve cached remote actor documents (DONE)** — The directory's "All known" scope + the actor profile view now render a **remote** actor's identity from the copy this instance cached in its database during federation (Phase 117.3's `RemoteActorPersister` already persisted remote actor docs; the gap was that the profile view did a *live* fetch via the proxy, so a deactivated/unreachable remote actor showed "Actor not found." even though its doc was in the DB). New server endpoint `GET /ap/v1/actor?iri={absolute-actor-iri}` (`ActorByIriHandler`) serves the stored actor document **as-is** (no live fetch, no Iris-local extensions — those are only valid for local actors); 404 on an uncached/malformed IRI (the client then falls back to a live fetch). Client `UiContext.FetchActorAsync` now consults this endpoint first for a **remote** (cross-origin) actor IRI (`IsRemoteActorIri` + `FetchCachedActorAsync`, same-origin via the `"iris"` client); local actors keep the direct `/u/{handle}` path (preserving the Iris-local extensions). `CachedActorEndpointTests` (5) cover local + remote cached actors served, unknown/malformed IRI 404. Live-verified (Playwright, `--no-cache` rebuild): a cached **deactivated** remote actor (fairy.id "Mase", previously "Actor not found.") now renders its profile; a live remote actor (Gargron) still loads fully; the directory "All known" lists all cached remote actors; the only console errors are the **pre-existing** CSP `connect-src` collection-fetch violations (identical for live actors). Build clean; suite green. [changes/13401](docs/changes/13401-phase134-cached-actor-documents.md)

- **133.2 — Actor summary (bio) rendered as sanitized HTML (DONE)** — An actor's `summary` (bio) is authored as HTML by Mastodon/Lemmy but was rendered as plain text (HTML-encoded), so markup showed as literal `<b>…</b>`. Now rendered as real markup, but **sanitized first** (the summary is untrusted remote HTML — a `<script>`/`onerror` in a remote actor's bio would be XSS). New dependency-free `Iris.Core.Rendering.HtmlSanitizer` (allow-lists safe tags/attrs, strips scripts/iframes/event handlers/style, restricts `href`/`src` to http(s)/mailto, neutralizes the `java\tscript:` control-char bypass) — matching the codebase's dependency-free rendering convention (no AngleSharp/sanitizer NuGet, to keep the trimmed WASM payload lean). `ActorIdentityHelper.RenderedSummary` wraps the sanitized output in a `MarkupString`; used in all three actor-summary render sites (ActorCard, ActorProfile, ObjectView actor branch). 21 new unit tests (`HtmlSanitizerTests`) cover the security contract. Live-verified (Playwright, `--no-cache` rebuild): an HTML bio renders bold/italic/link as markup, the embedded `<script>` + `<img onerror>` are stripped, and no XSS fires (`window.__xss_ran` never set); server stores the summary raw (confirming the untrusted case). Build clean; suite green. [changes/13302](docs/changes/13302-phase133-actor-summary-html-rendering.md)

- **133.1 — Object card styling: two-row header (DONE)** — The object card header was restructured from one busy row (avatar + handle + moderation bar + date all competing for one horizontal band, with the date pushed right by `margin-left:auto`) into two left-justified rows: the relative post date sits on its own top line (`.object-header-time`), and the actor handle + moderation bar sit on the next line (`.object-header-actor`), with the moderation group left-justified directly after the handle instead of pushed to the far right. Applied to the primary `Create`/Note card and the generic `IObject` fallback card in `ObjectView.razor`; `.object-header` becomes a flex column; `.object-time` drops `margin-left:auto`. CSS synced to both `app.css` copies. Live-verified (Playwright, fresh context + CDP cache-bypass, `--no-cache` container rebuild): two rows render, date on top + left-justified, moderation left-justified after the handle, no horizontal overflow at 375 px, no console errors. Build clean; suite green (client-only). [changes/13301](docs/changes/13301-phase133-object-card-styling.md)

## Keeping the docs lean

- **This file is the *only* one an agent must read and update every turn. Keep it short: bounded lists, not narrative.**
- **Clean up finished items roll them up - it's ok to keep some recently completed, but keep it short and point to change docs**
- Detail belongs in [docs/plans/](docs/plans/) (forward-looking scope), [docs/changes/](docs/changes/README.md) (what was built), or [docs/decisions/](docs/decisions/README.md) (why). Link, don't copy.
- [docs/ROADMAP.md](docs/ROADMAP.md) is append-only and low-churn — add a line when a phase closes, don't rewrite it.

Full operating rules, including the Inbox and Paused-Questions protocols, live in [docs/reference/AUTONOMOUS_LOOP.md](docs/reference/AUTONOMOUS_LOOP.md).
