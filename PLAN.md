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

**Phase 54 — Post-1.0 polish & hardening (IN PROGRESS).** 54.1–54.6, 54.7, 54.8, 54.9, 54.10, 54.11, 54.12, 54.13 COMPLETE. **54.6 (search stale-results cosmetic)** — the Search page's results branch was guarded only by `Results is not null`; a CDP-throttled re-search + MutationObserver showed the stale results were already hidden in practice (because `RunSearchAsync` nulls `Results` in the same synchronous chunk as `Busy = true`), but the correctness was timing-dependent. Made it structural: the results branch is now gated on `!Busy` too, so a re-search never renders the prior result set while in flight. Live-verified: the observer never saw the spinner with results present. **54.13 (media upload 400)** fixed: the 400 was Kestrel's global 1 MiB `MaxRequestBodySize` cap (not nginx) — `LocalMediaUploadHandler` now sets `IHttpMaxRequestBodySizeFeature.MaxRequestBodySize = MaxMediaUploadBytes` (10 MiB) before reading the form (exempting the authenticated media endpoint from the 1 MiB federation-inbox DoS bound) and propagates the `BadHttpRequestException` status (413 for oversized, 400 for malformed) instead of a blanket 400. Live-verified: 1 MiB / 1.3 MiB / 2 MB / 9 MiB → 201 (were 400), 11 MiB → 413. **54.5 (responsive at 768px/1024px)** was a clean verification pass (no overflow/clipping, no code change). **Active slice: 54.14 (mention/hashtag autocomplete popover + `tag` round-tripping)**. 54.12 (No way to set actor icon/image via edit profile) added an avatar control to `EditProfileForm` (preview + `InputFile` upload-on-select + "Remove avatar"), wired `Profile.razor` to upload via `Session.UploadMediaAsync` and set/clear `updated.Icon` (a `Link`, or `[]` to remove) on the embedded `Person`, and fixed two server gaps in `UpdateActivityHandler`: icon-merge semantics (an empty icon array clears the icon; a missing one leaves it unchanged) and — the key fix — `LocalActorDocumentCache` invalidation after `PutActorAsync` (without it a profile edit persisted to the store but the public actor document kept serving a stale cached copy, so the new icon/name were invisible to federation, other clients, and a fresh load). 54.11 (Settings → Account → Edit profile two clicks) deep-linked the Settings Account-tab "Edit your profile" link to `/profile?edit=true`; `Profile.razor` reads the `?edit` query param and a one-shot flag auto-enters edit mode on load, so the user skips the extra "Edit profile" click. 54.10 (Settings tab bar overcrowded) dropped the Password tab and folded its change-password form into the Account tab as a native `<details>` collapsible (7→6 tabs). 54.9 (duplicate home-feed fetch) fixed the client's `GetCollectionAsync` double-fetch: a self-`first` collection (an `OrderedCollection` served as its own first page) was fetched twice (once to read `first`, once for the first page) — now the fetched collection document is reused as the first page, so the first page costs one `GET`. 54.8 (redundant per-object likes/shares fetch) made the server stamp `iris:likedCount`/`sharedCount`/`repliedCount` (+ per-requester `isLiked`/`isShared`) onto feed/outbox/object-doc objects and gave `EngagementBar` a fast path that skips both collection walks when the counts are present — also fixed a `KristofferStrube.ActivityStreams` `OneOrMultipleConverter` bug that clones + drops `ExtensionData` on nested objects. Next slice 54.13 (Media upload 400s on a 1.3 MiB image — cause unconfirmed) is Active.

*(Phases 32–53 complete — one-line ledger per phase in [docs/ROADMAP.md](docs/ROADMAP.md).)*

**Test policy for these phases (user-directed, binding):**

- **No new coded tests** — all verification is manual via MCP Playwright (live app, real browser).
- **Existing web tests (`tests/Iris.Web.Tests`) are expendable:** keep what passes; if a change breaks one, **delete that test** (log it in the change doc) — never fix the app to satisfy it, never write a replacement.
- **15-second rule:** any single test taking longer than 15 s is **skipped**; if the suite stalls on timing-out tests, use blame (detailed per-test timings) to find the offenders and **comment them out**.
- **Done = live-verified:** build clean + Docker rebuild + Playwright pass over the slice's scope + screenshots; broken/slow tests handled per the rules above.

## Active Slice

**54.14: Mention/hashtag autocomplete popover + `tag` field round-tripping** — two related investigations for compose:
- **Autocomplete popover**: [Compose.razor](apps/Iris.Web.Client/Components/Pages/Compose.razor) only does plain-text regex detection of `@handle`/`#hashtag` on submit (`DetectMentionsAsync`) with no in-editor picker. Add a selection dropdown/popover that triggers while typing `@`/`#`, queries matching actors/tags, and lets the user confirm a choice before it's inserted (reduces mis-mentions and confirms the resolved actor IRI up front).
- **`tag` field notation**: ActivityPub `Note.tag` carries structured `Mention` and `Hashtag` objects (not just inline text). [IriExtensions.cs](src/Iris.Core/Identity/IriExtensions.cs)'s `GetMentionIris` only extracts `Mention` entries today — `Hashtag` tag entries aren't parsed anywhere. Read/write `Hashtag` tag objects (name + href) alongside `Mention` so incoming posts from other servers that rely on `tag` rather than inline `#text` still surface as recognized hashtags, and so outgoing posts populate `tag` for both mentions and hashtags per the AP convention.

This is a two-part slice. Start with the `tag` round-tripping (server/Core, testable) since the autocomplete popover depends on a reliable search/lookup path; then build the client popover. Verify both live (compose a post with `@mention` and `#hashtag`, confirm the popover offers candidates and the outgoing note carries `Mention`/`Hashtag` tag objects; verify an incoming note with `tag`-only hashtags renders them).

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

1. **54.14: Mention/hashtag autocomplete popover + `tag` field round-tripping** — two related investigations for compose:
    - **Autocomplete popover**: [Compose.razor](apps/Iris.Web.Client/Components/Pages/Compose.razor) only does plain-text regex detection of `@handle`/`#hashtag` on submit (`DetectMentionsAsync`) with no in-editor picker; investigate adding a selection dropdown/popover that triggers while typing `@`/`#`, queries matching actors/tags, and lets the user confirm a choice before it's inserted (reduces mis-mentions and confirms the resolved actor IRI up front).
    - **`tag` field notation**: ActivityPub `Note.tag` carries structured `Mention` and `Hashtag` objects (not just inline text). [Iris.Core/Identity/IriExtensions.cs](src/Iris.Core/Identity/IriExtensions.cs)'s `GetMentionIris` only extracts `Mention` entries today — `Hashtag` tag entries aren't parsed anywhere. Investigate reading/writing `Hashtag` tag objects (name + href) alongside `Mention` so incoming posts from other servers that rely on `tag` rather than inline `#text` still surface as recognized hashtags, and so outgoing posts populate `tag` for both mentions and hashtags per the AP convention.
2. **54.15: Markdown content rendering** — many federated servers send `content` as server-rendered HTML but source it from markdown (or send markdown-flavored text as-is); investigate adding a common markdown-aware content-rendering control (shared across `ObjectView`/timeline/detail views) so markdown-sourced content displays correctly instead of relying solely on raw HTML passthrough.
3. **54.16: Images don't render for notes** — attached images on notes/posts don't render in the timeline/detail views; investigate the attachment rendering path (likely `ObjectView`/timeline card markup and/or the `attachment`/`Image` model mapping) and fix so image attachments actually display.
4. **54.17: Home timeline excludes the user's own posts** — the home feed only shows followed actors' posts, not the signed-in user's own; investigate the feed-composition logic (fan-out/aggregation on the server, or the client's feed query) and include the user's own posts in their home timeline.
5. **54.18: Notification display is too bare ("liked your post {handle}")** — the notification list currently renders minimal text like "liked your post {handle}"; build out a richer notification item (actor avatar, actor display name, a snippet/preview of the referenced post, relative timestamp, and a link to the post) instead of the current bare-text row.
6. **54.19: Directory should show actor cards, split into Communities/People tabs** — the actor directory currently lists actors plainly in one list; redesign it to show a card per actor using their icon/image, display name/handle, and a short bio/summary, with a "+"/expand affordance to reveal that actor's most recent posts inline. Also split the directory into two tabs — **Communities** and **People** — so Group-like actors and individual actors are browsed separately instead of mixed together.
7. **54.20: Review AP client proxy fallback for remote object browsing** — browsing remote objects still fails with CORS errors instead of falling back to the server-side proxy; review the WASM cross-instance proxy fallback (from 54.7) to find why remote-object fetches aren't routing through it and fix so remote browsing works without CORS failures. (Seeing 401 on the proxy call - it's signed with a cookie not sure why it gets rejected). **Update (latest build):** the 401 appears resolved and the proxy call now succeeds, but the remote actor page renders no posts — the outbox/feed call returns successfully (per network inspection) yet the list stays empty; investigate the proxied response's shape/deserialization (e.g. the client's collection-page parsing expecting a direct AP response vs. a wrapped/proxied envelope) vs. the rendering component's empty-state logic. Same symptom also seen on the **followers** and **following** collections — both return data over the proxy but render empty lists, so the root cause is likely shared across all proxied collection views, not just the outbox. (follow RayvenMX@mastodon.world for an example)
8. **54.21: No search for external/remote users** — search currently only finds local actors; investigate adding remote-user search (e.g. WebFinger-style handle lookup or federated search against the actor's home instance) so users can find and follow actors that aren't already known locally.
9. **54.22: Actor icons on notes show initials instead of the real icon — extract shared `ActorBar`/`ActorCard` controls** — timeline/detail note cards render an actor's initial-letter avatar even when the actor has a real icon set, suggesting the icon isn't being passed/rendered consistently across the several places actor identity is shown (timeline cards, detail view, notifications, directory). Create common reusable controls — a compact **`ActorBar`** (icon + display name + handle, for inline/list contexts like note headers and notification rows) and a fuller **`ActorCard`** (icon + display name + handle + bio, for directory/profile-preview contexts) — and migrate the existing ad-hoc avatar/name markup to use them, fixing the icon-vs-initials bug as part of the consolidation. This overlaps with 54.18 (notifications) and 54.19 (directory cards) — build the shared controls here and have those slices consume them.
10. **54.23: Audit page authorization — some pages are reachable while logged out** — at least some pages that should require auth are still visible/reachable without being logged in; audit every route/page (Settings, Compose, Profile-edit, admin/moderation pages, notifications, etc.) for missing `[Authorize]`/auth-guard checks (both route-level and any client-side redirect logic), and lock down every page that should require a signed-in session.
11. **54.24: Profile "Replies" and "Likes" tabs don't populate** — on the own-profile page, the Replies and Likes tabs render empty instead of the actor's actual replies/liked posts; investigate the collection query/mapping for each tab (likely wrong collection IRI, missing federation/local-store lookup, or a filter that excludes everything) and fix so both tabs show real data.
12. **54.25: No reply count shown on replied-to items** — a post with replies doesn't show a reply count anywhere in the UI. Note 54.8 already has the server stamping `iris:repliedCount` onto feed/outbox/object-doc objects — check whether `EngagementBar` (or wherever the like/share counts render) is missing the reply-count display entirely, or whether `repliedCount` isn't reaching this call site, and add/fix the reply-count UI.
13. **54.26: Settings → Notifications tab says "cannot load"** — the Notifications tab in Settings fails to load its content instead of showing the notification preferences; investigate the underlying data/API call (endpoint error, missing/mismatched DTO, or an unhandled exception surfaced as a generic "cannot load" message) and fix so the tab renders correctly.
14. **54.27: Investigate a "system" identity for a logged-out public feed** — there's currently nothing to browse when no one is logged in. Investigate introducing a system/instance actor (default handle `sys@{domain}`) whose outbox/feed surfaces all public activities on the instance, so a logged-out visitor has something to browse. Its actor detail could be resolved/returned as part of session bootstrap (e.g. an anonymous "session" carries the system actor's IRI so the client knows what feed to render). Needs design thought on: is this a real actor row seeded per-instance, or a synthetic/virtual actor computed on the fly; how it interacts with existing auth-page-audit work (54.23); and what "public activities" means (local-only vs. federated-in-too).
15. **54.28: Community profile page — owner icon/image + owners list + promote-to-owner endpoint** — the community (Group actor) profile page needs work: (1) the owner should be able to set an icon/image for the community the same way a user sets their avatar (likely reusing the 54.12 upload-and-set-icon flow); (2) it's unclear how community ownership is currently tracked — investigate whether an `iris:`-namespaced extension property (an ordered list of owner actor IRIs, allowing multiple/promoted owners rather than a single creator) needs to be added to the Group actor document; (3) owners should be able to promote a member to owner by posting to an owners endpoint (new `iris:` capability, e.g. `POST` to an `owners` collection/endpoint on the community) — needs auth checks so only existing owners can promote.
16. **54.29: General UI/UX consistency review across all pages** — do a full visual/UX pass across every page (spacing, typography, button/card styles, color usage, empty/loading states, iconography) to find and fix inconsistencies that have accumulated slice-by-slice; this overlaps with 54.22's shared `ActorBar`/`ActorCard` consolidation — treat that as one input to this broader pass rather than duplicating it.

**Phase 45–52** (all COMPLETE — see [docs/ROADMAP.md](docs/ROADMAP.md)).

## Inbox

User-injected requests that arrived mid-workstream. Actioned in order at the top of the *next* turn's "select the next work item" step, ahead of **Up Next** (unless a slice is already in progress — finish that first). Cleared once actioned; the resulting slice gets its own **Recently Completed** entry.

*(empty — Phase 31's 10 user-review items (2026-09-05) are all COMPLETE; see docs/changes/274–283.)*

## Paused Questions

Questions the agent asked and is waiting on a real answer for — the loop should not silently proceed past these. *(none currently)*

## Recently Completed

  - 54.6: **Search stale-results cosmetic** (Phase 54) — during a re-search, the "Searching…" spinner could render while the previous result set stayed visible. A CDP-throttled re-search + MutationObserver showed the stale results were already hidden in practice (`RunSearchAsync` nulls `Results` in the same synchronous chunk as `Busy = true`), but the correctness was timing-dependent on that ordering, not on the render guard. Made it structural: the results branch is now gated on `!Busy` too (`else if (Results is not null && !Busy)`), so a re-search never renders the prior result set while in flight. Live-verified on the FQDN (CDP cache disabled + 3.5 s throttle): the observer never saw the spinner visible together with results; the stale result count stayed 0 throughout the in-flight window; new results rendered cleanly after. 0 console errors. [changes/388](docs/changes/388-54.6-search-stale-results-cosmetic.md)
  - 54.13: **Media upload 400s on a 1.3 MiB image** (Phase 54) — a compose attachment upload of a >1 MiB image returned `400`. Root cause: Kestrel's **global 1 MiB `MaxRequestBodySize`** cap (`DefaultMaxRequestBodySize` in `WebAppFactory.cs`, a DoS bound on the unauthenticated federation inbox, applied to every body) rejected the upload before the endpoint's own 10 MiB cap was reached, and `LocalMediaUploadHandler`'s `catch (BadHttpRequestException)` collapsed the 413 to a blanket 400 (the `Server: nginx` header was a red herring — nginx proxies the app-generated 400). **Fix:** in `LocalMediaUploadHandler`, set `IHttpMaxRequestBodySizeFeature.MaxRequestBodySize = MaxMediaUploadBytes` (10 MiB) before `ReadFormAsync` — exempting the authenticated, owner-only media endpoint from the 1 MiB bound while every other endpoint keeps it — and propagate the `BadHttpRequestException` status (413 oversized / 400 malformed) instead of a blanket 400. Live-verified on the FQDN (signed in as `alice`, CDP cache disabled): 1 MiB / 1.3 MiB / 2 MB / 9 MiB → **201** (were 400); 11 MiB → **413**. 0 unexpected console errors. [changes/387](docs/changes/387-54.13-media-upload-400.md)
  - 54.5: **Responsive regression at tablet/laptop breakpoints** (Phase 54) — extended the 54.1 mobile pass (375px) to 768px and 1024px. The app has a **single** breakpoint (`@media (max-width: 768px)` in app.css), so 768px renders the mobile layout (hamburger nav, stacked actor header) and 1024px renders the desktop layout (full horizontal nav, row actor header) — two distinct paths. Live-verified on the FQDN at **both** widths across the dense pages (Settings 6-tab bar + cards, admin users table, admin moderation table, actor detail header+tabs, home timeline feed): **no overflow or clipping found** at either width — the single 768px breakpoint already covers the tablet/laptop range correctly, so **no code change was made** (a clean verification pass). 0 console errors after a cache-disabled reload (the 34 `ERR_FAILED`/`.wasm Failed to fetch` errors seen on first post-redeploy visit were the known stale-WASM-bootstrap cache artifact, not an app regression). [changes/386](docs/changes/386-54.5-responsive-tablet-laptop.md)
  - 54.12: **No way to set actor icon/image via edit profile** (Phase 54) — `EditProfileForm` had no avatar control. Added an avatar section (circular preview + hidden `<InputFile>` + "Choose/Change avatar" + "Remove avatar"); `Profile.razor` uploads via `Session.UploadMediaAsync` (10 MiB cap) and sets `updated.Icon` on the embedded `Person` (a `Link` to the media IRI, or `[]` to remove). Two server fixes in `UpdateActivityHandler`: (1) icon-merge semantics — an empty icon array clears the icon, a missing one leaves it unchanged (previously only non-empty arrays merged, so there was no way to clear); (2) **`LocalActorDocumentCache` invalidation** after `PutActorAsync` — without it a profile edit persisted to the store but the public `GET /ap/v1/u/{handle}` kept serving a stale cached copy (60s/300s TTL), so the new icon/name were invisible to federation, other clients, and a fresh page load (mirrors the existing `Add`/`Remove` invalidation; the `Update` path was the gap). Live-verified on the FQDN (fresh context, cache disabled): set-avatar → preview + save → DB icon + plain `curl` shows the icon (no `?refresh=true`); current avatar shown on reload; "Remove avatar" + save sends `"icon":[]` → DB + `curl` show no icon. 0 console errors. [changes/385](docs/changes/385-54.12-actor-icon-image.md)
  - 54.7 / 54.4 / 54.3 / 54.2 / 54.8 / 54.9 / 54.10 / 54.11 (Phase 54) — moved to the [ROADMAP.md](docs/ROADMAP.md) ledger (WASM cross-instance proxy fallback, global error-boundary, accessibility/keyboard regression, empty/loading-state audit, redundant per-object likes/shares fetch, duplicate home-feed fetch, Settings tab bar overcrowded, edit-profile deep-link).
Rolling window of the last ~5 slices. When a new entry pushes this over 5, move the oldest entry's one-liner into [docs/ROADMAP.md](docs/ROADMAP.md)'s ledger and drop it here.

## Keeping the docs lean

- This file is the *only* one an agent must read and update every turn. Keep it short: bounded lists, not narrative.
- Detail belongs in [docs/plans/](docs/plans/) (forward-looking scope), [docs/changes/](docs/changes/README.md) (what was built), or [docs/decisions/](docs/decisions/README.md) (why). Link, don't copy.
- [docs/ROADMAP.md](docs/ROADMAP.md) is append-only and low-churn — add a line when a phase closes, don't rewrite it.

Full operating rules, including the Inbox and Paused-Questions protocols, live in [docs/reference/AUTONOMOUS_LOOP.md](docs/reference/AUTONOMOUS_LOOP.md).
