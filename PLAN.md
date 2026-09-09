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

**Phase 59 — Remaining spec gaps & final interop (IN PROGRESS).** 4 slices from the last open F-items: 59.1 (`Move` re-resolution + key rotation, F-25), 59.2 (OAuth2 bearer path, F-20), 59.3 (`Article`-specific fields, F-11 remainder), 59.4 (`ld+json` production, F-31). **Phase 58 — Federation completeness & remaining spec gaps (COMPLETE).** 4 slices: 58.1 (custom emoji, F-27), 58.2 (Question/poll, F-26), 58.3 (rich attachments, F-11), 58.4 (final conformance sweep) — all COMPLETE. **Phase 57 COMPLETE** (57.1–57.4). **54.15 (Markdown content rendering)**: the production `ObjectView` was treating non-pre-rendered-HTML `content` as inert text (`WebUtility.HtmlEncode`), so Markdown-sourced notes displayed their literal source. Reused the app's existing dependency-free Markdown renderer by moving it to the shared `Iris.Core.Rendering.Markdown` (both the sample and the production client already reference `Iris.Core` transitively), and switched the three production render sites (`ObjectView.RenderedContent`/`ActivityContent`, `ObjectViewActivityRenderer.SafeContent`) from `HtmlEncode` to `Markdown.ToHtml` for non-HTML content (pre-rendered HTML still emitted verbatim). Live-verified: a Markdown note renders to `<h1>/<strong>/<em>/<ul>/<ol>/<a>/<code>/<pre>/<br>`; an XSS note's raw `<script>` is escaped inert and a `javascript:` link is dropped while a `https` link is kept; a pre-rendered-HTML note renders verbatim. Core 293/0, Server 952/0, Web 63/0, SampleBlazorClient 17/17. **54.14 (mention/hashtag autocomplete popover + `tag` round-tripping)**: the composer's `tag` field now round-trips `Hashtag` entries end to end — `IriExtensions.GetHashtagTags` reads them back, `ComposeNote.Build`/`PostReplyAsync` write them (a generic `Object` of `Type=["Hashtag"]`, `Name`, optional `href`), all four compose post paths detect `#hashtag` and pass them through, and `ObjectView` renders them as links to the local hashtag search; plus a live-verified autocomplete popover (debounced `@onkeyup` token detection → actor search for `@`, typed-token offer for `#`, splice-on-accept). A subtle store-round-trip quirk (a `tag` item whose `type` is an array lacking the `"Object"` base deserializes to null and is dropped; the write path's bare-string `"Hashtag"` form round-trips cleanly) is guarded by `HashtagStoreRoundTripTests`. Core 293/0, Server 952/0, Web 63/0. **54.6 (search stale-results cosmetic)** — the Search page's results branch was guarded only by `Results is not null`; a CDP-throttled re-search + MutationObserver showed the stale results were already hidden in practice (because `RunSearchAsync` nulls `Results` in the same synchronous chunk as `Busy = true`), but the correctness was timing-dependent. Made it structural: the results branch is now gated on `!Busy` too, so a re-search never renders the prior result set while in flight. Live-verified: the observer never saw the spinner with results present. **54.13 (media upload 400)** fixed: the 400 was Kestrel's global 1 MiB `MaxRequestBodySize` cap (not nginx) — `LocalMediaUploadHandler` now sets `IHttpMaxRequestBodySizeFeature.MaxRequestBodySize = MaxMediaUploadBytes` (10 MiB) before reading the form (exempting the authenticated media endpoint from the 1 MiB federation-inbox DoS bound) and propagates the `BadHttpRequestException` status (413 for oversized, 400 for malformed) instead of a blanket 400. Live-verified: 1 MiB / 1.3 MiB / 2 MB / 9 MiB → 201 (were 400), 11 MiB → 413. **54.5 (responsive at 768px/1024px)** was a clean verification pass (no overflow/clipping, no code change). **Active slice: 54.14 (mention/hashtag autocomplete popover + `tag` round-tripping)**. 54.12 (No way to set actor icon/image via edit profile) added an avatar control to `EditProfileForm` (preview + `InputFile` upload-on-select + "Remove avatar"), wired `Profile.razor` to upload via `Session.UploadMediaAsync` and set/clear `updated.Icon` (a `Link`, or `[]` to remove) on the embedded `Person`, and fixed two server gaps in `UpdateActivityHandler`: icon-merge semantics (an empty icon array clears the icon; a missing one leaves it unchanged) and — the key fix — `LocalActorDocumentCache` invalidation after `PutActorAsync` (without it a profile edit persisted to the store but the public actor document kept serving a stale cached copy, so the new icon/name were invisible to federation, other clients, and a fresh load). 54.11 (Settings → Account → Edit profile two clicks) deep-linked the Settings Account-tab "Edit your profile" link to `/profile?edit=true`; `Profile.razor` reads the `?edit` query param and a one-shot flag auto-enters edit mode on load, so the user skips the extra "Edit profile" click. 54.10 (Settings tab bar overcrowded) dropped the Password tab and folded its change-password form into the Account tab as a native `<details>` collapsible (7→6 tabs). 54.9 (duplicate home-feed fetch) fixed the client's `GetCollectionAsync` double-fetch: a self-`first` collection (an `OrderedCollection` served as its own first page) was fetched twice (once to read `first`, once for the first page) — now the fetched collection document is reused as the first page, so the first page costs one `GET`. 54.8 (redundant per-object likes/shares fetch) made the server stamp `iris:likedCount`/`sharedCount`/`repliedCount` (+ per-requester `isLiked`/`isShared`) onto feed/outbox/object-doc objects and gave `EngagementBar` a fast path that skips both collection walks when the counts are present — also fixed a `KristofferStrube.ActivityStreams` `OneOrMultipleConverter` bug that clones + drops `ExtensionData` on nested objects. Next slice 54.13 (Media upload 400s on a 1.3 MiB image — cause unconfirmed) is Active.

*(Phases 32–53 complete — one-line ledger per phase in [docs/ROADMAP.md](docs/ROADMAP.md).)*

**Test policy for these phases (user-directed, binding):**

- **No new coded tests** — all verification is manual via MCP Playwright (live app, real browser).
- **Existing web tests (`tests/Iris.Web.Tests`) are expendable:** keep what passes; if a change breaks one, **delete that test** (log it in the change doc) — never fix the app to satisfy it, never write a replacement.
- **15-second rule:** any single test taking longer than 15 s is **skipped**; if the suite stalls on timing-out tests, use blame (detailed per-test timings) to find the offenders and **comment them out**.
- **Done = live-verified:** build clean + Docker rebuild + Playwright pass over the slice's scope + screenshots; broken/slow tests handled per the rules above.

## Active Slice

*(none — 59.2 COMPLETE; next: 59.3 `Article`-specific fields.)*

### Loop protocol (WASM manual-test phase)

Each slice is a **Playwright-driven pass**, not a code-first slice. Per slice:

1. **Build**: `cd /workspace && dotnet build apps/Iris.Web/Iris.Web.csproj -c Release`
2. **Docker**: `cd /workspace/apps/Iris.Web && docker compose build iris-web && docker compose up -d --force-recreate iris-web` — **avoid `--no-cache`** (repeated no-cache fills the host disk; if the build fails with `No space left on device`, run `docker builder prune -af` first).
3. **Manual test (MCP Playwright)**: create/use test accounts (`alice`/`alice-password` seeded; register more as the slice needs — `bob`, `carol`, `dave`), create test content (posts, replies, follows, communities, media, CW), exercise the slice's scope (see [docs/plans/wasm-stabilization.md](docs/plans/wasm-stabilization.md)). Capture **console errors** (`browser_console_messages`) and **screenshots of every screen** (inline screenshot no files) visited - use public fqdn address "https://iris.luit.ink".
4. **Triage**: log every defect (page, repro, expected vs actual, severity) in the slice's change doc; any defect not fixed this slice becomes a numbered **Up Next** item.
5. **Fix in scope**: implement fixes for the defects assigned to this slice; re-verify each fix live.
6. **Web tests**: `cd /workspace && dotnet test --no-build -c Release` — keep passing tests; **delete** any test broken by the change; **skip/comment out** any single test >15 s (find offenders via `dotnet test tests/Iris.Web.Tests -v n --logger "console;verbosity=detailed"` per-test timings). No new coded tests.
7. **Update PLAN.md**: move the finished slice to Recently Completed; keep Up Next sorted by priority (defects first).

## Up Next

Short, bounded list — only the next few items, not the whole roadmap. Defects triaged from test passes are prepended here (highest severity first).

**Phase 60 - UI/UX Review (PENDING):**

- 60.1: **Review what the user is seeing on every page - login as 'andrew' - password 'Password1' and visually inspect all views - build additional items to address under phase 60.

**Phase 59 — Remaining spec gaps & final interop (IN PROGRESS):**

- 59.1: **`Move` re-resolution + key rotation (F-25)** (COMPLETE) — [changes/423](docs/changes/423-59.1-move-re-resolution-key-rotation.md)
- 59.2: **OAuth2 bearer path (F-20)** (COMPLETE) — [changes/424](docs/changes/424-59.2-oauth2-bearer-path.md)
- 59.3: **`Article`-specific fields (F-11 remainder)** (Medium/S) — surface `publishedTime`, `duration`, `inLanguage` from `Article` objects in `ObjectView` rendering.
- 59.4: **`ld+json` production (F-31)** (Low/S) — content-type negotiation: serve `application/ld+json` when the client accepts it (currently always `application/activity+json`, which is spec-valid).

**Phase 58 — Federation completeness & remaining spec gaps (COMPLETE):**

- 58.1: **Custom emoji / `Emoji` tag support (F-27)** (COMPLETE) — [changes/419](docs/changes/419-58.1-custom-emoji-emoji-tag-support.md)
- 58.2: **Question / poll support (F-26)** (COMPLETE) — [changes/420](docs/changes/420-58.2-question-poll-support.md)
- 58.3: **Rich attachment rendering (F-11)** (COMPLETE) — [changes/421](docs/changes/421-58.3-rich-attachment-rendering.md)
- 58.4: **Final conformance sweep** (COMPLETE) — [changes/422](docs/changes/422-58.4-final-conformance-sweep.md)

**Phase 56 — Cross-implementation federation compatibility (COMPLETE):**

- 56.1: **Mastodon wire-compatibility gap analysis** (COMPLETE) — [changes/412](docs/changes/412-56.1-mastodon-wire-compatibility-gap-analysis.md)
- 56.2: **Pleroma/Akko wire-compatibility check** (COMPLETE) — [changes/413](docs/changes/413-56.2-pleroma-akko-wire-compatibility.md)
- 56.3: **Fix top-3 wire compatibility gaps** (COMPLETE) — [changes/414](docs/changes/414-56.3-fix-top3-wire-compatibility-gaps.md)

**Phase 56 COMPLETE.**

*(Phase 55 COMPLETE — all three slices done. See ROADMAP.md.)*

**Phase 45–52** (all COMPLETE — see [docs/ROADMAP.md](docs/ROADMAP.md)).

## Inbox

User-injected requests that arrived mid-workstream. Actioned in order at the top of the *next* turn's "select the next work item" step, ahead of **Up Next** (unless a slice is already in progress — finish that first). Cleared once actioned; the resulting slice gets its own **Recently Completed** entry.

*(empty — Phase 31's 10 user-review items (2026-09-05) are all COMPLETE; see docs/changes/274–283.)*

## Paused Questions

Questions the agent asked and is waiting on a real answer for — the loop should not silently proceed past these. *(none currently)*

## Recently Completed

  - 59.2: **OAuth2 bearer path (F-20)** (Phase 59) — actor document `endpoints` now advertises `oauthAuthorizationEndpoint` + `oauthTokenEndpoint`; inbox handler falls back to `Authorization: Bearer` token resolution when no valid HTTP signature is present (via `IOAuthTokenStore`); body-reading fixed to use `EnableBuffering` + `ReadAsBufferedStringAsync` (works for both signed and unsigned paths). 6 new integration tests. Server 966/0. [changes/424](docs/changes/424-59.2-oauth2-bearer-path.md)
  - 59.1: **`Move` re-resolution + key rotation (F-25)** (Phase 59) — `GetPublicKeyIri` helper extracts `publicKey.id` from actor ExtensionData; `MoveActivityHandler` resolves the old actor's actual key IRI (not `#key-1`) + warms the new actor doc into `RemoteActorCache`; `RemoteInboundKeyResolver` invalidates the old key's cache entry when a fetched document's `publicKey` declares `replaces`. 9 new unit tests. Core 334/0, Server 977/0. [changes/423](docs/changes/423-59.1-move-re-resolution-key-rotation.md)
  - 58.4: **Final conformance sweep** (Phase 58) — verified all F-01–F-31; updated `MISSING_FEATURES.md` (F-06/F-12/F-26/F-27/F-28 → resolved; F-11 partial; F-20/F-25/F-31 still open); all C-01–C-08 verified. Doc-only. [changes/422](docs/changes/422-58.4-final-conformance-sweep.md)
  - 58.3: **Rich attachment rendering (F-11)** (Phase 58) — `GetRichAttachments` reads all attachment types (Image/Document/Audio/Video/Link) with type, name, URL, preview; `ObjectView` renders type-appropriate cards with icons + preview thumbnails + type labels. 8 new unit tests. Core 328/0, Server 957/0, Web 62/0. [changes/421](docs/changes/421-58.3-rich-attachment-rendering.md)
  - 58.2: **Question / poll support (F-26)** (Phase 58) — `GetPollData` parses Mastodon `poll` and AS2.0 `options`/`endTime`/`closed` shapes; `ObjectView` renders proportional option bars + vote counts + ended state. 12 new unit tests. Core 320/0, Server 957/0, Web 62/0. [changes/420](docs/changes/420-58.2-question-poll-support.md)
  - 58.1: **Custom emoji / `Emoji` tag support (F-27)** (Phase 58) — `IriExtensions.GetCustomEmojis` reads the `emoji` array from `ExtensionData`; `ObjectView` renders emoji images (or text fallback) below content. 12 new unit tests. Core 308/0, Server 957/0, Web 62/0. [changes/419](docs/changes/419-58.1-custom-emoji-emoji-tag-support.md)
Rolling window of the last ~5 slices. When a new entry pushes this over 5, move the oldest entry's one-liner into [docs/ROADMAP.md](docs/ROADMAP.md)'s ledger and drop it here.

## Keeping the docs lean

- This file is the *only* one an agent must read and update every turn. Keep it short: bounded lists, not narrative.
- Detail belongs in [docs/plans/](docs/plans/) (forward-looking scope), [docs/changes/](docs/changes/README.md) (what was built), or [docs/decisions/](docs/decisions/README.md) (why). Link, don't copy.
- [docs/ROADMAP.md](docs/ROADMAP.md) is append-only and low-churn — add a line when a phase closes, don't rewrite it.

Full operating rules, including the Inbox and Paused-Questions protocols, live in [docs/reference/AUTONOMOUS_LOOP.md](docs/reference/AUTONOMOUS_LOOP.md).
