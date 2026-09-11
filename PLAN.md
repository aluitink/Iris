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

- **Scope correction (this turn) — the WASM client is back in the active build.** Commit `01f4fcf` ("chore(web): remove WASM client from active build", authored by the Iris Loop Agent) had moved `apps/Iris.Web.Client` (the production Blazor WASM UI) to `.scratch/`, dropped it from `Iris.slnx`, and stripped the `BuildAndCopyClient`/`PatchWasmConfig` targets from `Iris.Web.csproj` on the (wrong) premise that "the WASM app is not the project under development." That premise was an agent scoping error, not operator direction — the operator confirmed `Iris.Web.Client` is the product UI and should be active. **Reverted this turn:** `apps/Iris.Web.Client` restored (from `f6750f0`), re-added to `Iris.slnx`, the two MSBuild targets restored in `Iris.Web.csproj`, the `.scratch/` gitignore entry removed, and the Dockerfile (which had not been updated by `01f4fcf` and was left referencing the client) is now correct again. Build 0 warn/0 err; fast suite green (974/975, one known-flaky delivery test passes in isolation). **Phases 71 and 72 are valid again** — Phase 72 (72.1–72.4) is the active work.
- **Phase 71 — Note card + compose polish (COMPLETE).** All six slices done (71.1–71.6). Change doc: [711](docs/changes/711-note-card-compose-polish.md).
- **Phase 72 — Efficiency + UX residuals (COMPLETE).** Four items, all carrying forward residuals from Phases 63/64/70/71: (1) shared per-object engagement-count cache on `UiContext` (the `EngagementBar`'s `/likes`+`/shares` walk coalesced to 1× per object); (2) minted-id extension (server renders `iris:likeActivityIri`/`announceActivityIri` so engaged posts fire **0** collection walks); (3) richer inline document rendering (`.doc-gallery` grid — image `Preview` fills the card, PDF via `<iframe>`, other docs a large-icon placeholder); (4) IA-03 nav grouping (**deferred/accepted-as-is** — the nav is a flat 9-link row, below the ~11-link threshold; mobile hamburger already groups). No new coded tests (WASM manual-test policy); full suite 975 passed / 17 skipped / 0 failed.
- **Phase 73 — Compose feature completion (NEXT).** The server already supports all of these (Update/Delete handlers, poll round-trip, media, audience `to` arrays, WebFinger) but the UI doesn't expose them. Six slices, in priority order: (1) poll creation (compose UI + `PostQuestionAsync` — polls render but can't be created); (2) multiple media attachments (multi-file compose, broaden file types beyond image); (3) audience/visibility selector (Public / Followers / Direct — `to` is currently hardcoded `as:Public`); (4) community posts carry media + CW (unhide the attachment/CW gates for community posts); (5) cross-instance @mention resolution (WebFinger lookup for `@user@domain` — currently same-instance only, the explicit "deferred" comment in `Compose.razor`); (6) poll voting (interactive poll options + `VoteAsync` — the server-side vote-handling half is new). Poll-voting is the only slice needing a new server endpoint; the other five are client/UI-only.

## Active Slice

- **75 — External media serving (in progress).** **75.1 DONE** ([change doc 751](docs/changes/751-external-media-proxy-rewrite.md)): client render boundary routes cross-origin media through the media proxy (was: stripped host → 404). Live-verified: external wasabi S3 image loads via proxy. Build 0 warn/0 err; full suite: 1665 passed, 0 failed, 17 skipped. **Next: 75.2 — warmer media-type gap (Document/Audio/Video not pre-downloaded), proxy-fetch sync gap (AP proxy-fallback doesn't store/warm), Update handler warm gap.**

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
| 71 | Note card + compose polish — card built from all content, inline attachments, interaction bar at bottom, sensitive-blur, mention dropdown | **COMPLETE** — all 6 slices done |
| 72 | Efficiency + UX residuals — engagement-count cache, minted-id extension, inline document rendering, nav grouping (deferred) | **COMPLETE** — 72.1–72.3 done, 72.4 accepted-as-is |
| 73 | Compose feature completion — poll creation, multi-media, audience selector, community media+CW, cross-instance mentions, poll voting | **COMPLETE** — all 6 slices done |
| 74 | Better Mastodon compatibility — standardize activity minting, sensitive content blur fix | **COMPLETE** — all 4 slices done |

- Each phase's closeout writes its change doc AND distills the next-next phase's topics into this file.
- **Operator latitude:** I am authorized to do whatever keeps testing moving — recreate containers, create new test users, seed content, restart services, adjust test data. Log significant interventions in the slice's tracker notes.

### Loop protocol

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
     - **Network watch — count calls, not bytes (always on)**: the point is to catch **request spam** — duplicate/redundant calls fired when a page or control loads (same fetch twice on mount, a refetch on re-render, an N+1 fan-out). For each distinct request pattern record: method+path, status, **how many times it fired (the count is the spam signal)**, and *what triggered it* (which load/control).
    - **MCP Runs in docker** - If the MCP server seems down or stuck you can restart the container
5. **Triage**: log every finding (page, repro, expected vs actual, **class** + **severity**) in the **shared tracker**. Class is the routing axis (blocker → current phase's blocker slice, bug → current phase's fix slice, UX → a later UX phase, perf → a later efficiency phase); severity (S1/S2/S3) sets priority *within* the class. Set both on every row.
6. **Fix in scope**: implement fixes for this slice's assigned defects; **re-verify each fix from a clean entry** (step 3) and record the evidence (`console-clean + <control/state> works`, optionally + the auto-saved screenshot path) before flipping Status to `fixed`. No evidence, no `fixed`.
7. **Web tests**: `cd /workspace && dotnet test --no-build -c Release` — keep passing tests; **delete** any test broken by the change; **skip/comment out** any single test >15 s (find offenders via `dotnet test tests/Iris.Web.Tests -v n --logger "console;verbosity=detailed"` per-test timings). No new coded tests. **Every deleted or skipped test is logged** (test name, action, reason, restore-by) — no silent deletions; the phase's closeout reviews the ledger.
8. **Update PLAN.md**: move the finished slice to Recently Completed; keep Up Next sorted by priority (blockers first). At phase closeout: distill the next-next phase's topics into this file.

### Slices (Phase 73 — Compose feature completion)

| Slice | Scope |
|---|---|
| **73.1** | **Poll creation (outbound) — DONE** ([change doc 731](docs/changes/731-poll-creation.md)). `PostQuestionAsync` added to `IActivityPubClient` (builds a `Question` object with a Mastodon `poll` extension in `ExtensionData` — the single reliable round-trip form through the library's deserializer); "Poll" option in the compose type selector + poll editor (question, 2–4 option rows, duration picker, multiple-choice toggle) wired into `PostAsync`. Server's `CreateActivityHandler` already stores any object type — no server change. |
| **73.2** | **Multiple media attachments — DONE** ([change doc 732](docs/changes/732-multiple-media-attachments.md)). `Compose.razor` now holds `List<IBrowserFile> Attachments` with `accept="image/*,video/*,audio/*,.pdf"`; `OnAttachmentsChosen` uses `e.GetMultipleFiles(e.FileCount)`; `PostAsync` loops uploads and passes `List<MediaAttachment>` to `ComposeNote.Build` (new `IEnumerable<MediaAttachment>?` parameter — builds `Image` for `image/*`, `Document` for other types). New `MediaAttachment` record in `Iris.Core.Compose`. Live-verified: two PNGs attached, posted, both render in the AP `Create` object with correct `mediaType`/`name`/`url`. |
| **73.3** | **Audience / visibility selector — DONE** ([change doc 733](docs/changes/733-audience-selector.md)). Added a Public/Followers/Direct `<select>` in compose meta (non-reply, non-community posts only). `PostAudience` record + `BuildAudience` map visibility to the correct `to`/`cc` IRI set per AP §5.1.2: Public = `to`[#Public] `cc`[followers]; Followers = `to`[followers] `cc`[followers]; Direct = `to`[followers+mentions] `cc`[followers]. `ComposeNote.Build` gained `cc` param (ExtensionData); `PostQuestionAsync` gained `cc` param. Threaded through all post paths (note/article/poll). Live-verified: all three audiences produce correct `to`/`cc` on the wire (202 Accepted). |
| **73.4** | **Community posts carry media + CW — DONE** ([change doc 734](docs/changes/734-community-media-cw.md)). Relaxed the CW gate from `ReplyToIri is null && CommunityIri is null` to `ReplyToIri is null`; `PostToCommunityAsync` now accepts `media`, `sensitive`, `summary` — `sensitive` written to ExtensionData, `summary` to `note.Summary`, each media file becomes an `Image`/`Document` in `note.Attachment`. Live-verified: community compose shows attachment + CW controls; posted note carries `sensitive: true`, `summary`, `attributedTo=[actor, community]`, `to=[community/followers, #Public]` (202). |
| **73.5** | **Cross-instance @mention resolution — DONE** ([change doc 735](docs/changes/735-cross-instance-mention-resolution.md)). `DetectMentionsAsync` regex extended to match `@user@domain`; cross-instance mentions resolved via WebFinger (RFC 8410) through the home instance's proxy endpoint; unresolvable mentions silently dropped. |
| **73.6** | **Poll voting (outbound) — DONE** ([change doc 736](docs/changes/736-poll-voting.md)). Server: `POST /local/v1/u/{handle}/votes/{**pollIri}` records a vote on a stored Question object (Basic/cookie auth, idempotent, 409 on expired). Client: `VoteAsync` on `ILocalModerationClient` (POST JSON `{option: index}`). Core: `GetPollDataFromJson` parses the vote response. UI: poll options are clickable when signed in + not expired + not already voted; clicking shows a check mark, updates bars/counts, shows "You voted". Live-verified: posted a poll, voted on "C#", UI updated to 1 vote, server state confirmed (voters array, votesCount=1, totalVotes=1), re-vote is a no-op. |

**Phase 73 is COMPLETE.** All 6 slices done. Next: Phase 74 (Mastodon compatibility).

## Up Next

- 75 — Investigate external media serving: content from external actors does not serve properly; sync attachment content when we see an item and when we proxy-fetch an item.
- 76 — Other AP servers: find other AP servers/actors to browse content, document differences, follow Mastodon's example.
- 77 — Replied-to items could be fetched and content shown under the reply.
- 78 — Investigate Lemmyverse conventions (communities via `!community@domain.tld`, ranked posts, batched streams).


## Inbox

- *(empty)*

## Paused Questions

- *(empty)*

**Resolved (this turn) — Login 400 was stale Playwright context state, not a code/proxy bug.** Investigating the recurring MCP Playwright sign-in 400 step-by-step proved it was **not** a code bug and **not** a reverse-proxy misconfiguration. The persistent Playwright browser context had accumulated **stale/desynced anti-forgery state** (from repeated testing: clearing cookies, re-fetching `/local/v1/antiforgery` tokens, posting multiple times in rapid succession); a desynced (field-token vs `.AspNetCore.Antiforgery.*` cookie) pair is rejected with a silent **400**. In a clean state (fresh context / cookies cleared) the **normal form flow works every time**: navigate `/login` → fill `handle`+`password` → click "Sign in" → **302** to `/home` (verified 3/3 in fresh contexts + with the standard Playwright MCP tools, no `run_code_unsafe`). The proxy correctly passes `Set-Cookie` + forwarded headers + TLS; **no proxy reconfiguration needed**. **Repeatable login:** if a login 400s, log out first (or use a fresh context) to clear stale anti-forgery state, then re-login normally. **71.6 is now unblocked.**

## Recently Completed

- **75.1 — External media proxy rewrite (Phase 75, slice 1) — DONE** ([change doc 751](docs/changes/751-external-media-proxy-rewrite.md)): client render boundary routes cross-origin media through the media proxy (was: stripped host → 404). Live-verified: external S3 image loads via proxy. Full suite: 1665 passed, 0 failed, 17 skipped.
- **74.4 — likes/shares Collections + inReplyToAtomUri (Phase 74, slice 4) — DONE** ([change doc 744](docs/changes/744-likes-shares-inreplytoatomuri.md)): minted Notes carry `likes`/`shares` Collections (totalItems=0) + `inReplyToAtomUri` (parent IRI for replies). **Phase 74 COMPLETE.** Full suite: 1665 passed, 0 failed, 17 skipped.
- **74.3 — atomUri, context/conversation, contentMap (Phase 74, slice 3) — DONE** ([change doc 743](docs/changes/743-atomuri-context-contentmap.md)): minted Notes carry `atomUri`, `context`/`conversation`, `contentMap`. Full suite: 1665 passed, 0 failed, 17 skipped.
- **74.2 — Mastodon wire-format fields (Phase 74, slice 2) — DONE** ([change doc 742](docs/changes/742-mastodon-wire-format-fields.md)): minted Notes carry `url`, explicit `sensitive`, `replies` `OrderedCollection`. Full suite: 1665 passed, 0 failed, 17 skipped.
- **74.1 — Sensitive blur includes image (Phase 74, slice 1) — DONE** ([change doc 741](docs/changes/741-sensitive-blur-includes-image.md)): `MediaGallery` `Blurred` param; media blurred when sensitive + not revealed. Full suite: 1665 passed, 0 failed, 17 skipped.
## Keeping the docs lean

- This file is the *only* one an agent must read and update every turn. Keep it short: bounded lists, not narrative.
- Detail belongs in [docs/plans/](docs/plans/) (forward-looking scope), [docs/changes/](docs/changes/README.md) (what was built), or [docs/decisions/](docs/decisions/README.md) (why). Link, don't copy.
- [docs/ROADMAP.md](docs/ROADMAP.md) is append-only and low-churn — add a line when a phase closes, don't rewrite it.

Full operating rules, including the Inbox and Paused-Questions protocols, live in [docs/reference/AUTONOMOUS_LOOP.md](docs/reference/AUTONOMOUS_LOOP.md).
