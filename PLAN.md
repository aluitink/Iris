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

- **73 — Compose feature completion (in progress).** **73.1 DONE** ([change doc 731](docs/changes/731-poll-creation.md)). **73.2 DONE** ([change doc 732](docs/changes/732-multiple-media-attachments.md)). **73.3 DONE** ([change doc 733](docs/changes/733-audience-selector.md)). Three slices remaining (details under Slices table). Server already supports most of this; the work is client/UI exposure + one new server method for poll voting (73.6). **No new coded tests** (WASM manual-test policy) except the poll-voting server slice (73.6) which adds integration tests for the new vote endpoint. Build 0 warn/0 err; full suite green before close. **Next: 73.4 — Community posts carry media + CW.**

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
| 73 | Compose feature completion — poll creation, multi-media, audience selector, community media+CW, cross-instance mentions, poll voting | **in progress** — see Slices table |

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
| **73.4** | **Community posts carry media + CW (quick win).** The attachment section is gated `@if (ReplyToIri is null)` and the CW/sensitive toggle `@if (ReplyToIri is null && CommunityIri is null)` in `Compose.razor`, so community posts (and replies) can't carry media or a content warning. Relax the gates and extend `PostToCommunityAsync` (which currently builds a bare `Note` with no `attachment`/`sensitive`/`summary`) to accept + attach media + set `sensitive`/`summary`. Low effort, closes two small structural gaps at once. |
| **73.5** | **Cross-instance @mention resolution.** `Compose.razor`'s `DetectMentionsAsync` resolves `@handle` to the signed-in actor's origin only — the code comment (lines 373–374) explicitly defers cross-instance mentions: "would require a WebFinger lookup, which is deferred." Extend the mention-autocomplete path to detect an `@user@domain` token and resolve it via the client's `WebFingerClient` (RFC 8615 handle→IRI), so a remote mention is recorded with the correct actor IRI. The `WebFingerClient`/`WebFingerDiscoveryService`/`WebFingerCache` already exist in `Iris.Client`. |
| **73.6** | **Poll voting (outbound).** Completes the poll feature (create + view + vote). The poll rendering in `ObjectView` is display-only (non-interactive bars). Add: (a) a **server-side** vote handler (record the voter on the `Question` object's option — the only new server code in this phase, an `Update`-based or dedicated `POST` endpoint), (b) a `VoteAsync` on `IActivityPubClient`, (c) interactive poll options in `ObjectView` with vote-state tracking (the viewer's choice + the post-vote counts). This is the largest slice — scope it so the server half is testable (integration tests) and the UI half is Playwright-verified. |

**Priority / sequencing note:** 73.1 → 73.2 → 73.3 → 73.4 are the user-facing headline features (polls, multi-media, visibility, community media) and are all client/UI-only. 73.5 (cross-instance mentions) is a federation-polish item. 73.6 (poll voting) is the only slice needing a new server endpoint and is the most effort — it can land last, or be deferred to Phase 74 if 73.1–73.5 consume the phase.

## Up Next

- 73.4 — Community posts carry media + CW (unhide the attachment/CW gates)
- 73.5 — Cross-instance @mention resolution (WebFinger for `@user@domain`)
- 73.6 — Poll voting (interactive options + `VoteAsync` + new server vote endpoint)

- 74 - Better mastodon compatiblity - The way we mint our activities vs. Mastodon is slightly different, we should stanardize on how they do it for maximum compatiblity. See the example in docs/references/ExampleWithTagAndMentionAndAttachedImage.json. Perform deeper compatiblity investigation by walking random outboxes to find more content, explore RayvenMX@mastodon.world followings and find other mastodon actors to browse their outboxes, use the UI to drive this effort - Build additional items into the plan to fix/implement to improve support.
- 74.1 - The sensitive content blur should also include the image - it's likely the image that is sensitive more than the text - ok to blur the text too.
- 75 - Investigate external media serving - Content from external actors do not serve properly, we should be syncing the attachment content when we see an item and if we proxy fetch an item.
- 76 - Other AP Servers - Try to find other AP servers and actors to browse their content to find more content to try and enhance compatiblity. Document any differences that cannot be overcome, Mastodon is the largest, we should probably follow their example.
- 77 - Replied to items could be fetched and content shown under the reply.
- 78 - Investigate Lemmyverse and their conventions, I think communities are handled via !community@domain.tdl - we could follow similar conventions, we should investigate the proper way to interact with communities. Communities show posts ranked by Likes and maybe Dislikes - unsure what the over the wire looks like for these items, maybe we can explore following one to see, from what I remember the posts come in a large batched stream on a schedule. Review this doc: https://join-lemmy.org/docs/contributors/05-federation.html or try to browse [for raw source](https://github.com/LemmyNet/lemmy-docs)
## Inbox

- *(empty)*

## Paused Questions

- *(empty)*

**Resolved (this turn) — Login 400 was stale Playwright context state, not a code/proxy bug.** Investigating the recurring MCP Playwright sign-in 400 step-by-step proved it was **not** a code bug and **not** a reverse-proxy misconfiguration. The persistent Playwright browser context had accumulated **stale/desynced anti-forgery state** (from repeated testing: clearing cookies, re-fetching `/local/v1/antiforgery` tokens, posting multiple times in rapid succession); a desynced (field-token vs `.AspNetCore.Antiforgery.*` cookie) pair is rejected with a silent **400**. In a clean state (fresh context / cookies cleared) the **normal form flow works every time**: navigate `/login` → fill `handle`+`password` → click "Sign in" → **302** to `/home` (verified 3/3 in fresh contexts + with the standard Playwright MCP tools, no `run_code_unsafe`). The proxy correctly passes `Set-Cookie` + forwarded headers + TLS; **no proxy reconfiguration needed**. **Repeatable login:** if a login 400s, log out first (or use a fresh context) to clear stale anti-forgery state, then re-login normally. **71.6 is now unblocked.**

## Recently Completed

- **73.3 — Audience/visibility selector (Phase 73, slice 3) — DONE** ([change doc 733](docs/changes/733-audience-selector.md)): added a Public/Followers/Direct `<select>` in compose meta (non-reply, non-community only). `PostAudience` record + `BuildAudience` map visibility to `to`/`cc` per AP §5.1.2: Public = `to`[#Public] `cc`[followers]; Followers = `to`[followers] `cc`[followers]; Direct = `to`[followers+mentions] `cc`[followers]. `ComposeNote.Build` gained `IEnumerable<Iri>? cc` (ExtensionData); `PostQuestionAsync` gained `cc` param. Threaded through note/article/poll paths. 5 stub clients updated. Dockerfile fixed (remove stale Client obj/bin/publish before build). Live-verified: all three audiences produce correct `to`/`cc` on the wire (202 Accepted). Full suite: 1665 passed, 0 failed, 17 skipped.
- **73.2 — Multiple media attachments (Phase 73, slice 2) — DONE** ([change doc 732](docs/changes/732-multiple-media-attachments.md)): `Compose.razor` now accepts multiple files (`List<IBrowserFile>`, `accept="image/*,video/*,audio/*,.pdf"`, `multiple` on the `InputFile`); `OnAttachmentsChosen` uses `e.GetMultipleFiles(e.FileCount)`; `PostAsync` loops uploads and passes `List<MediaAttachment>` to `ComposeNote.Build` (new `IEnumerable<MediaAttachment>?` parameter — `Image` for `image/*`, `Document` for video/audio/PDF). New `MediaAttachment` record in `Iris.Core.Compose` (respects the Core↛Client dependency rule). `UploadAttachmentAsync` returns null on failure (skip, don't abort). Live-verified: two PNGs attached simultaneously, posted (HTTP 202), both render in the AP `Create` object with correct `mediaType`/`name`/`url`; both media URLs resolve (200, `image/png`). Full suite: 975 passed, 17 skipped, 0 failed.
- **73.1 — Poll creation (outbound) (Phase 73, slice 1) — DONE** ([change doc 731](docs/changes/731-poll-creation.md)): added `PostQuestionAsync` to `IActivityPubClient` (builds a generic `Question` object with a Mastodon `poll` extension in `ExtensionData` — the single reliable round-trip form through the library's deserializer, since individual scalar keys like `endTime` are dropped from `ExtensionData` on `IObject` but a nested `JsonElement` property survives); "Poll" option in the compose type selector + poll editor (question input, 2–4 option rows with add/remove, duration picker in minutes, multiple-choice toggle) wired into `PostAsync` via a `PostPollAsync` helper. Five test stubs implementing `IActivityPubClient` updated with the new method. Two new client tests: `PostQuestionAsync_BuildsAs2Question_ThatRoundTripsThroughGetPollData` (wire carries `endsAt`; `GetPollData` re-read returns the 2-option poll with the right `endsAt` + `multiple` flag) + `PostQuestionAsync_FewerThanTwoOptions_Throws`. **No server change** — `CreateActivityHandler` already stores any object type. Full suite: 975 passed, 17 skipped, 0 failed. No new coded UI tests (WASM manual-test policy).
- **Phase 72 — Efficiency + UX residuals — COMPLETE** (72.1 engagement-count cache + 72.2 minted-id extension + 72.3 richer document rendering done & verified; 72.4 IA-03 nav grouping **accepted-as-is** — the nav is a flat 9-link row, below the ~11-link threshold where grouping was deemed worthwhile, and the mobile hamburger already groups). Full suite: 975 passed, 17 skipped, 0 failed. Change docs: [721](docs/changes/721-engagement-count-cache.md) · [722](docs/changes/722-minted-id-extension.md) · [723](docs/changes/723-rich-document-rendering.md).
- **72.3 — Richer inline document rendering (Phase 72, slice 3) — DONE** ([change doc 723](docs/changes/723-rich-document-rendering.md)): replaced the `.object-attachment` link-card with a `.doc-gallery` grid (mirrors the image `.media-gallery` single/two/three/four layout). Each document card: an image `Preview` fills the card; a PDF renders inline via `<iframe>`; other documents get a large-icon placeholder. A name + type label is overlaid on a gradient scrim; the name link adds a `download` attribute. **Verified live on `:8088`** (fresh `docker compose build --no-cache` + `--force-recreate`, browser cache cleared): the image path is unaffected (a note with an `Image` attachment renders 1 `.media-gallery` image, 0 `.doc-gallery`, 0 `.object-attachment`, 0 console errors). Document/PDF paths verified by build + code inspection only (the live DB has no document/PDF attachments). Full suite: 975 passed, 17 skipped, 0 failed. No new coded tests (WASM manual-test policy).
- **72.2 — Minted-id extension: server-rendered Like/Announce activity IRI (Phase 72, slice 2) — DONE** ([change doc 722](docs/changes/722-minted-id-extension.md)): eliminated the residual `/likes`+`/shares` walk the 72.1 cache left (the single id-recovery walk an engaged post still paid). Added `IrisExtensionTerms.LikeActivityIri`/`AnnounceActivityIri` + namespace `@context` declaration; `GetRequesterActivityIrisAsync` (a single `GetAllActivitiesAsync` sweep filtered to the requester's Like/Announce activities on the page's objects, using `ResolveObjectIri` for robust actor/object matching); wired into `EnrichCollectionItemsAsync` (batch fetch in Phase 2, annotation in Phase 3) and `ObjectDocumentHandler`/`ServeObjectDocument`; client `GetLikeActivityIri()`/`GetAnnounceActivityIri()` + `EngagementBar` fast path seeds minted ids directly from the extensions (no walk). The 72.1 shared cache remains as a residual fallback for non-Iris / un-enriched reads. **Verified live on `:8088`** (fresh `docker compose build --no-cache` + `--force-recreate`, cookie-auth `/home` as `andrew`, browser cache cleared): the engaged post's feed response carries `likeActivityIri` + `announceActivityIri`; **0** `/likes` + **0** `/shares` requests fire (down from 1+1 after 72.1). Full suite: 975 passed, 17 skipped, 0 failed. No new coded tests (WASM manual-test policy).
## Keeping the docs lean

- This file is the *only* one an agent must read and update every turn. Keep it short: bounded lists, not narrative.
- Detail belongs in [docs/plans/](docs/plans/) (forward-looking scope), [docs/changes/](docs/changes/README.md) (what was built), or [docs/decisions/](docs/decisions/README.md) (why). Link, don't copy.
- [docs/ROADMAP.md](docs/ROADMAP.md) is append-only and low-churn — add a line when a phase closes, don't rewrite it.

Full operating rules, including the Inbox and Paused-Questions protocols, live in [docs/reference/AUTONOMOUS_LOOP.md](docs/reference/AUTONOMOUS_LOOP.md).
