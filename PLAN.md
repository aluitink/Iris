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

**Phase 54 — Post-1.0 polish & hardening (IN PROGRESS).** 54.1–54.4, 54.7, 54.8, 54.9, 54.10, 54.11, 54.12 COMPLETE. **54.13 (media upload 400) root-cause confirmed (Kestrel 1 MiB `MaxRequestBodySize` global cap, not nginx) and a fix was built + live-verified, but the user directed to skip it for now (2026-09-09) — the code change was reverted; the diagnosis is recorded in Up Next.** Active slice: **54.5 (responsive regression at tablet/laptop breakpoints)**. 54.12 (No way to set actor icon/image via edit profile) added an avatar control to `EditProfileForm` (preview + `InputFile` upload-on-select + "Remove avatar"), wired `Profile.razor` to upload via `Session.UploadMediaAsync` and set/clear `updated.Icon` (a `Link`, or `[]` to remove) on the embedded `Person`, and fixed two server gaps in `UpdateActivityHandler`: icon-merge semantics (an empty icon array clears the icon; a missing one leaves it unchanged) and — the key fix — `LocalActorDocumentCache` invalidation after `PutActorAsync` (without it a profile edit persisted to the store but the public actor document kept serving a stale cached copy, so the new icon/name were invisible to federation, other clients, and a fresh load). 54.11 (Settings → Account → Edit profile two clicks) deep-linked the Settings Account-tab "Edit your profile" link to `/profile?edit=true`; `Profile.razor` reads the `?edit` query param and a one-shot flag auto-enters edit mode on load, so the user skips the extra "Edit profile" click. 54.10 (Settings tab bar overcrowded) dropped the Password tab and folded its change-password form into the Account tab as a native `<details>` collapsible (7→6 tabs). 54.9 (duplicate home-feed fetch) fixed the client's `GetCollectionAsync` double-fetch: a self-`first` collection (an `OrderedCollection` served as its own first page) was fetched twice (once to read `first`, once for the first page) — now the fetched collection document is reused as the first page, so the first page costs one `GET`. 54.8 (redundant per-object likes/shares fetch) made the server stamp `iris:likedCount`/`sharedCount`/`repliedCount` (+ per-requester `isLiked`/`isShared`) onto feed/outbox/object-doc objects and gave `EngagementBar` a fast path that skips both collection walks when the counts are present — also fixed a `KristofferStrube.ActivityStreams` `OneOrMultipleConverter` bug that clones + drops `ExtensionData` on nested objects. Next slice 54.13 (Media upload 400s on a 1.3 MiB image — cause unconfirmed) is Active.

*(Phases 32–53 complete — one-line ledger per phase in [docs/ROADMAP.md](docs/ROADMAP.md).)*

**Test policy for these phases (user-directed, binding):**

- **No new coded tests** — all verification is manual via MCP Playwright (live app, real browser).
- **Existing web tests (`tests/Iris.Web.Tests`) are expendable:** keep what passes; if a change breaks one, **delete that test** (log it in the change doc) — never fix the app to satisfy it, never write a replacement.
- **15-second rule:** any single test taking longer than 15 s is **skipped**; if the suite stalls on timing-out tests, use blame (detailed per-test timings) to find the offenders and **comment them out**.
- **Done = live-verified:** build clean + Docker rebuild + Playwright pass over the slice's scope + screenshots; broken/slow tests handled per the rules above.

## Active Slice

**54.5: Responsive regression at tablet/laptop breakpoints** — extend the 54.1 mobile pass (375px) to 768px and 1024px: verify no overflow/clipping on the dense pages (Settings cards, admin users/moderation tables, actor detail tabs).

*(54.13 — media upload 400 — was the prior active slice; its root cause is now **confirmed** (Kestrel's global 1 MiB `MaxRequestBodySize` cap, not nginx) and a fix was built + live-verified, but the user directed to **skip it for now** (2026-09-09), so the code change was reverted and the full diagnosis is recorded in the Up Next entry. It remains the top Up Next item for when it is actioned.)*

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

**Phase 54 — Post-1.0 polish & hardening (in progress):**

1. **54.13: Media upload 400s on a 1.3 MiB image — ROOT CAUSE CONFIRMED (deferred by user, 2026-09-09)** — a compose-post attachment upload of a 1.3 MiB PNG returns `400`. **Diagnosis (done):** the request *does* reach the app (an unauthenticated `curl` gets a 401 from the app's cookie/Basic auth, and the nginx access log shows the POST reaching the upstream). The 400 is **size-dependent**: live-verified on the FQDN that 10 KB / 500 KB uploads return 201 but 1.3 MiB / 2 MB return 400 — the threshold is **1 MiB**, which is exactly [WebAppFactory.cs](apps/Iris.Web/WebAppFactory.cs)'s `DefaultMaxRequestBodySize` (1 MiB, a deliberate DoS bound on the unauthenticated federation inbox, applied globally to *all* request bodies). The media endpoint's own cap (`MaxMediaUploadBytes` = 10 MiB in [ActivityPubServerExtensions.cs](src/Iris.Server/ActivityPubServerExtensions.cs)) is never reached because Kestrel rejects the body at 1 MiB first; `LocalMediaUploadHandler`'s `catch (BadHttpRequestException)` then maps it to a `400` (not the `413` the handler documents for oversized). **The nginx `Server` header is a red herring** — nginx proxies the response (the 400 originates in Kestrel, behind nginx). **The fix (built + live-verified, then reverted at user request — re-apply when this slice is actioned):** in `LocalMediaUploadHandler`, set `IHttpMaxRequestBodySizeFeature.MaxRequestBodySize = MaxMediaUploadBytes` before `ReadFormAsync` (exempts the authenticated media endpoint from the 1 MiB global cap while keeping the cap on federation endpoints), and change the `catch (BadHttpRequestException)` to propagate the exception's status code (413 for oversized, 400 otherwise). Verified live: 10 KB / 500 KB / 1.3 MiB / 2 MB / 9 MiB all → 201; 11 MiB → 413. `Iris.Server.Tests` 951/0, `Iris.Web.Tests` 63/63. *Deferred by the user ("skip this item for now") — the code change was reverted to keep the tree clean; the diagnosis above is recorded so it need not be re-run.*
2. **54.5: Responsive regression at tablet/laptop breakpoints** — extend the 54.1 mobile pass (375px) to 768px and 1024px: verify no overflow/clipping on the dense pages (Settings cards, admin users/moderation tables, actor detail tabs).
3. **54.6: Search stale-results cosmetic** — during a re-search, the "Searching…" spinner renders while stale previous results remain visible below; hide the stale results (or show them dimmed) while a new search is in flight (noted in 54.2, deferred).
4. **54.14: Mention/hashtag autocomplete popover + `tag` field round-tripping** — two related investigations for compose:
    - **Autocomplete popover**: [Compose.razor](apps/Iris.Web.Client/Components/Pages/Compose.razor) only does plain-text regex detection of `@handle`/`#hashtag` on submit (`DetectMentionsAsync`) with no in-editor picker; investigate adding a selection dropdown/popover that triggers while typing `@`/`#`, queries matching actors/tags, and lets the user confirm a choice before it's inserted (reduces mis-mentions and confirms the resolved actor IRI up front).
    - **`tag` field notation**: ActivityPub `Note.tag` carries structured `Mention` and `Hashtag` objects (not just inline text). [IriExtensions.GetMentionIris](src/Iris.Core/Identity/IriExtensions.cs) only extracts `Mention` entries today — `Hashtag` tag entries aren't parsed anywhere. Investigate reading/writing `Hashtag` tag objects (name + href) alongside `Mention` so incoming posts from other servers that rely on `tag` rather than inline `#text` still surface as recognized hashtags, and so outgoing posts populate `tag` for both mentions and hashtags per the AP convention.
5. **54.15: Markdown content rendering** — many federated servers send `content` as server-rendered HTML but source it from markdown (or send markdown-flavored text as-is); investigate adding a common markdown-aware content-rendering control (shared across `ObjectView`/timeline/detail views) so markdown-sourced content displays correctly instead of relying solely on raw HTML passthrough.

**Phase 45–52** (all COMPLETE — see [docs/ROADMAP.md](docs/ROADMAP.md)).

## Inbox

User-injected requests that arrived mid-workstream. Actioned in order at the top of the *next* turn's "select the next work item" step, ahead of **Up Next** (unless a slice is already in progress — finish that first). Cleared once actioned; the resulting slice gets its own **Recently Completed** entry.

*(empty — Phase 31's 10 user-review items (2026-09-05) are all COMPLETE; see docs/changes/274–283.)*

## Paused Questions

Questions the agent asked and is waiting on a real answer for — the loop should not silently proceed past these. *(none currently)*

## Recently Completed

  - 54.12: **No way to set actor icon/image via edit profile** (Phase 54) — `EditProfileForm` had no avatar control. Added an avatar section (circular preview + hidden `<InputFile>` + "Choose/Change avatar" + "Remove avatar"); `Profile.razor` uploads via `Session.UploadMediaAsync` (10 MiB cap) and sets `updated.Icon` on the embedded `Person` (a `Link` to the media IRI, or `[]` to remove). Two server fixes in `UpdateActivityHandler`: (1) icon-merge semantics — an empty icon array clears the icon, a missing one leaves it unchanged (previously only non-empty arrays merged, so there was no way to clear); (2) **`LocalActorDocumentCache` invalidation** after `PutActorAsync` — without it a profile edit persisted to the store but the public `GET /ap/v1/u/{handle}` kept serving a stale cached copy (60s/300s TTL), so the new icon/name were invisible to federation, other clients, and a fresh page load (mirrors the existing `Add`/`Remove` invalidation; the `Update` path was the gap). Live-verified on the FQDN (fresh context, cache disabled): set-avatar → preview + save → DB icon + plain `curl` shows the icon (no `?refresh=true`); current avatar shown on reload; "Remove avatar" + save sends `"icon":[]` → DB + `curl` show no icon. 0 console errors. [changes/385](docs/changes/385-54.12-actor-icon-image.md)
  - 54.11: **Settings → Account → Edit profile takes two clicks** (Phase 54) — the Settings Account tab "Edit your profile" link went to `/profile` (read-only view), forcing a second "Edit profile" click to reach the form. The link now points to `/profile?edit=true`; `Profile.razor` reads the `?edit` query param via `[SupplyParameterFromQuery]` and a one-shot `_pendingEdit` flag auto-enters edit mode once the actor document loads (consumed so a post-save re-render doesn't bounce back into edit mode). Read-only `/profile` path unchanged. Live-verified on the FQDN: deep-link lands directly on the edit form (no "Edit profile" button); plain `/profile` still shows read-only + button (regression held). [changes/384](docs/changes/384-54.11-edit-profile-two-clicks.md)
  - 54.10: **Settings tab bar overcrowded** (Phase 54) — the 7-tab Settings bar (Account/Password/Notifications/Communities/Relays/Moderation/Danger) was squished. Dropped the **Password** tab and moved its change-password form into the **Account** tab as a native `<details class="settings-subsection">` collapsible (a CSS chevron `▸` rotates when open; no JS, keyboard/AT accessible). Password functionality preserved 1:1 — only its location moved. Build clean, `Iris.Web.Tests` 63/63 (no test referenced the removed tab). Live-verified on the FQDN: tab bar is now 6 tabs, the collapsible reveals the 3-field form, and a full change-password → re-login cycle worked (then reverted to the canonical `alice-password`). [changes/383](docs/changes/383-54.10-settings-tab-bar-overcrowded.md)
  - 54.9: **Duplicate home-feed fetch** (Phase 54) — first `/home` load fired **two** identical `GET /ap/v1/u/{actor}/feed` requests. Root cause was **not** the initially-suspected `PagedCollection` re-fire (a live diagnostic proved the `_loadedFor` tracker fired the load exactly once) nor the "prerender + circuit double-mount" (the app is pure Blazor WASM, no SSR). It was in the client's collection walker `ActivityPubClient.GetCollectionAsync`: it fetched the collection document (to read its `first` link) and then fetched the first page — but for a self-`first` collection (an `OrderedCollection` served as its own first page, `first` == its own IRI) those are the **same** document, so it was fetched twice. **Fix:** extracted the fetched-object → `CollectionPage` conversion into a reusable `ConvertToCollectionPage` helper and added a fast path in `GetCollectionAsync` — when the resolved first-page IRI equals the collection's own IRI, the already-fetched collection document is reused as the first page (no second `GET`); page 2+ and external-`first` collections are unaffected. The earlier `_loadingInitial` guard in `PagedCollection.razor` (wrong root cause) was reverted. Live-verified on the FQDN: first `/home` load fires **exactly one** `GET /feed` (was two) and renders the feed. [changes/382](docs/changes/382-54.9-duplicate-home-feed-fetch.md)
  - 54.8: **Redundant per-object likes/shares collection fetch** (Phase 54) — the server now stamps `iris:likedCount`/`sharedCount`/`repliedCount` (+ per-requester `isLiked`/`isShared`, only when true) onto feed/outbox collection objects **and** the standalone object doc; `EngagementBar` takes the embedded `Object` as a parameter and uses a **fast path** that seeds counts + engagement state from those fields and skips both `/likes`+`/shares` walks (at most ONE walk to recover the minted Like/Announce id for Undo), falling back to the full walk only when the counters are absent (non-Iris/cached-outbox/unsigned). Also fixed a `KristofferStrube.ActivityStreams` **library bug**: the `OneOrMultipleConverter` re-materializes (clones) the embedded object on every `Activity.Object` enumeration **and** drops the embedded object's `ExtensionData` when the enclosing activity serializes — worked around by replacing `activityCopy.Object` with a single-element array so the enriched instance survives to the wire. Readers moved to `IObject` receivers; `IActorSessionAccessor` exposes `IrisNamespaceBase` (`{base}/ns#`). Live-verified: home feed render fires **ZERO** `/likes`+`/shares` requests (was 2 per object). [changes/381](docs/changes/381-54.8-redundant-likes-shares-fetch.md)
  - 54.7 / 54.4 / 54.3 / 54.2 (Phase 54) — moved to the [ROADMAP.md](docs/ROADMAP.md) ledger (WASM cross-instance proxy fallback, global error-boundary, accessibility/keyboard regression, empty/loading-state audit).
Rolling window of the last ~5 slices. When a new entry pushes this over 5, move the oldest entry's one-liner into [docs/ROADMAP.md](docs/ROADMAP.md)'s ledger and drop it here.

## Keeping the docs lean

- This file is the *only* one an agent must read and update every turn. Keep it short: bounded lists, not narrative.
- Detail belongs in [docs/plans/](docs/plans/) (forward-looking scope), [docs/changes/](docs/changes/README.md) (what was built), or [docs/decisions/](docs/decisions/README.md) (why). Link, don't copy.
- [docs/ROADMAP.md](docs/ROADMAP.md) is append-only and low-churn — add a line when a phase closes, don't rewrite it.

Full operating rules, including the Inbox and Paused-Questions protocols, live in [docs/reference/AUTONOMOUS_LOOP.md](docs/reference/AUTONOMOUS_LOOP.md).
