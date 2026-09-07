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

**Phase 34 — UI polish & engagement (ACTIVE, autonomous loop).** Phases 32 (production social platform app) and 33 (server production-readiness & hardening) are **COMPLETE**. Phase 34 is a **growing, iterative UI workstream** driven by visual inspection of the running app. The loop continues indefinitely: each iteration refines/improves the UI, then a visual review adds new items. **Primary goal: all UI work. No new automated UI tests — everything verified manually via MCP Playwright against the Docker stack.** Existing test suite must stay green (fast run `dotnet test --no-build -c Release` as the loop check). Priorities: **functionality first, then design & looks.**

## Active Slice

**Phase 34 — UI polish & engagement (autonomous loop).** This is an open-ended, growing workstream. Each iteration: (1) pick the next item below, (2) implement it, (3) build + Docker rebuild + verify via Playwright, (4) run existing tests (must be green), (5) visual-review all pages, (6) add new items discovered during the review, (7) update this file. The item list below is the **living backlog** — it grows with each iteration.

### Loop protocol

1. **Build**: `cd /workspace && dotnet build apps/Iris.Web/Iris.Web.csproj -c Release`
2. **Docker**: `cd /workspace/apps/Iris.Web && docker compose build --no-cache iris-web && docker compose up -d iris-web`
3. **Verify**: MCP Playwright — log in as `alice` / `alice-password`, navigate pages, test interactions
4. **Tests**: `cd /workspace && dotnet test --no-build -c Release` (0 failures required)
5. **Visual review**: screenshot all pages (home, compose, profile, directory, notifications, search, actor detail, object detail, landing). Compare against what a polished social platform should look like.
6. **Add items**: append new findings to the backlog below. Mark completed items with ~~strikethrough~~ + **COMPLETE**.
7. **Update PLAN.md**: move completed items to Recently Completed; keep the backlog sorted by priority.

### Backlog (sorted by priority: functionality → design)

**Functionality:**

1. ~~34.13: **Reply flow**~~ **COMPLETE** — `Compose.razor` accepts `?replyTo=` query param; shows "Reply" heading + "Replying to …" context; calls `PostReplyAsync` (sets `inReplyTo`). `ObjectDetail.razor` has a "Reply" button → `/compose?replyTo={noteIri}`. Verified live: posted note → Reply → composed → posted (HTTP 202).
2. ~~34.14: **Object detail cleanup**~~ **COMPLETE** — removed redundant "NOTE" type label from IObject branch; added `.object-detail` CSS for hero-sized content. Verified live.
3. ~~34.15: **Directory — hide raw IRI**~~ **COMPLETE** — `ActorProfile` handle is now a clickable link to `/actor?iri=…` with the IRI in a `title` tooltip; the visible IRI `<code>` line removed. `ObjectView` Actor branch: removed the redundant IRI link. Verified live: directory, actor detail, profile all show clean handles with no raw IRIs.
4. ~~34.16: **Remove duplicate card headings**~~ **COMPLETE** — removed `Title` + `Description` from `PagedCollection` on Home (`Home timeline`), Notifications (`Your notifications`), and Profile (`Your posts`). Each page now has a single `<h2>` heading. Verified live.
5. ~~34.18: **Pagination / "Load more"**~~ **COMPLETE (already implemented)** — `PagedCollection` already has a "Load more" button (line 70) that fetches the next page via `LoadMoreAsync` and appends items. No change needed.
6. ~~34.19: **Show replies on object detail**~~ **COMPLETE** — `ObjectDetail.razor` fetches the replies collection via `GetRepliesAsync` after loading the object, resolves each reply IRI to a full object via `GetObjectAsync`, and renders them in a dedicated "Replies" card using `ObjectView`. Actor objects skip the section. Unresolvable replies are silently skipped. Verified live: parent note shows its reply; a note with no replies shows "No replies yet." Full fast suite green (1,599 passed).
7. ~~34.20: **Follow/unfollow from actor detail + directory**~~ **COMPLETE** — fixed cross-page unfollow bug: when the follow was made from a different component instance, `_followActivityIri` was null so the unfollow was silently skipped. Added `UiContext.GetFollowActivityIriAsync` (scans the actor's outbox for the Follow activity targeting the given actor). `FollowButton` now falls back to this lookup when `_followActivityIri` is null. Verified live: follow on actor detail → navigate to directory → unfollow from directory (cross-page round-trip works). Full fast suite green (1,599 passed).
8. ~~34.21: **Home timeline shows followed actors' posts**~~ **COMPLETE (verification; no code change)** — followed andrew from the directory; the home timeline (`/home`) immediately shows andrew's posts ("Heeellllo", "Hello") + the Follow activity. The `FeedService.BuildFeedAsync` correctly merges followed actors' outboxes (F-14). The actor's own posts appear on `/profile`, not `/home` (standard ActivityPub behavior). Verified live.
9. ~~34.22: **Compose — reply context preview**~~ **COMPLETE** — `Compose.razor` now fetches the parent note on load and shows the author handle + a truncated content preview (200 chars) in a styled blockquote above the textarea. Verified live: replying to andrew's "Heeellllo" shows "Replying to andrew" + blockquote "Heeellllo". Full fast suite green.

**Design & polish:**

10. ~~34.23: **Landing page — remove the ugly text input**~~ **COMPLETE** — removed the card wrapper and redundant "Welcome to Iris" heading. The signed-out landing now shows a centered hero: accent-colored "Iris" title + tagline + sign-in/register buttons. Verified live.
11. ~~34.24: **Avatar consistency**~~ **COMPLETE** — `.actor-profile-avatar` + `.actor-profile-avatar-img` now use `border-radius: 50%` (circular). Both profile and actor detail show 64px circular avatars. Verified live.
12. ~~34.25: **Empty states**~~ **COMPLETE** — added `PagedCollection.EmptyContent` RenderFragment parameter. Home timeline, notifications, and search now show inline SVG icons + friendly messages. Home timeline empty state includes a "Browse the directory →" link. Verified live.
13. ~~34.26: **Loading states**~~ **COMPLETE (verification; no code change)** — all pages already have loading spinners: object detail (`card-loading` + spinner), actor detail (`profile-loading` + spinner when `ActorDoc is null`), directory (`card-loading` + spinner when `Busy`), search (`card-loading` + spinner when `Busy`), and `PagedCollection` (spinner during initial fetch + "Load more" button). Verified in code.
14. ~~34.27: **Post card spacing & separators**~~ **COMPLETE** — `ul.object-list li` padding 0.75rem 1rem, margin-bottom 0.75rem, border-radius 8px; `.object-item` gap 0.25rem. More breathing room between cards. Verified live.
15. **34.28: Object detail — show like count + boost count** — the object detail page shows the note but no engagement metrics. Fetch the `/likes` and `/announcements` collections (or use the `Replies`/`Likes` properties on the object) and show "N likes · M boosts" under the note.
16. **34.29: Favicon + meta tags** — the app has no favicon (browser tab shows a generic icon). Add a simple SVG favicon (e.g. a stylized "I" or an iris flower). Add `<meta name="description">` for the landing page.
17. **34.30: Responsive nav** — on narrow viewports the nav bar wraps awkwardly. Add a simple mobile-friendly layout: the nav links collapse into a horizontal scroll or a hamburger menu.
18. **34.31: Color & typography refinement** — the current palette (dark bg, blue accent) works but feels flat. Consider: slightly warmer background, better font weight hierarchy (the page `<h2>` and card text are similar weight), and a subtle accent color for interactive elements beyond just blue.
19. **34.32: Object detail — show the full thread** — when viewing a reply, show the parent note above it (a "In reply to …" blockquote with the parent's content), so the user has context without navigating away.
20. **34.33: Re-evealuate and generate new work** — when finished - review the entire project, create multiple identities and generate content to get an understanding of the look and feel of everything. Visually inspect and generate new phases of improvements/refinements to continue to work on. Review for user experience and what someone would expect to see. Reflect on some of the implementation in the sample UI exporler we built to test our server features if you are running out of ideas on what to implement (That project was quick and dirty, we want to take our time with this one).
### Recently completed this session (34.1–34.14)

See the **Recently Completed** section below for the rolling window.

## Up Next

Short, bounded list — only the next few items, not the whole roadmap. The Phase 34 backlog above is the authoritative list. When it drops below ~3 uncompleted items, do a fresh visual review and add more.

*(Phases 32 & 33 are complete — see the Recently Completed section and docs/changes/ for details. The Phase 34 backlog above is the active work.)*



## Inbox

User-injected requests that arrived mid-workstream. Actioned in order at the top of the *next* turn's "select the next work item" step, ahead of **Up Next** (unless a slice is already in progress — finish that first). Cleared once actioned; the resulting slice gets its own **Recently Completed** entry.

**Phase 31 — explorer UI & server namespace polish** (user review, 2026-09-05). Visual-confirmed via MCP Playwright against the running sample (UI :8090, server :8081) before each slice starts.

1. ~~31.1: **WebFinger — non-matching resources + client base-URI parsing (RFC 7033)**~~ **COMPLETE** — server returns 404 for a non-matching `host` (RFC 7033); client retries a failed dial-base resolution against the account's own advertised host (RFC 8410) when the hosts differ. 6 new tests. → [docs/changes/274](docs/changes/274-31.1-webfinger-rfc7033-host-and-retry.md)
2. ~~31.2: **`PagedCollection` initial load never re-renders — "Loading…" stuck until Refresh is clicked (feed + actor detail).**~~ **COMPLETE** — `LoadInitialAsync` (fired fire-and-forget from `OnParametersSet`) now ends with `StateHasChanged()`, mirroring the auto-re-render the `@onclick` handlers rely on; the spinner branch is also guarded on `LoadError` so a failed first fetch shows the error line, not an eternal spinner. Repairs every `PagedCollection` (feed + all actor-detail collections). New bUnit project, 4 tests. → [docs/changes/275](docs/changes/275-31.2-pagedcollection-initial-load-rerender.md)
3. ~~31.3: **Sample should not seed fake `remote.example` activities.**~~ **COMPLETE** — carla (the `remote.example` in-process stand-in) is now gated behind `Iris:Seed:RemoteStandIn` (default off); the default sample seeds one honest instance (alice, bob, community) with no fake cross-instance graph. Tests that exercise carla opt in; 2 new tests assert the default excludes carla. Also fixed `CreateWebHostBuilder` to pass the host's resolved config into `ConfigureServices` (per-host `UseConfiguration` was invisible to the seed). → [docs/changes/276](docs/changes/276-31.3-sample-no-fake-remote-seed.md)
4. ~~31.4: **Actors page — directory search should return only actors; rename to "Directory".**~~ **COMPLETE** — the search surface gained an optional `?type` filter (client `SearchOptions.Type`); `type=Actor` runs only the actor pass (no content), a non-actor type filters the content pass by `@type`, and no type keeps the full actor+content search. The `/actors` page passes `Type=Actor` and is renamed "Directory" (nav + `<PageTitle>`); route unchanged. 5 new tests. → [docs/changes/277](docs/changes/277-31.4-directory-actors-only-and-rename.md)
5. ~~31.5: **Actor detail — break up into tabs + profile-style header.**~~ **COMPLETE** — new `ActorProfile` component (avatar + handle + name + summary + IRI; initial fallback when no icon; reuses for 31.6) + the sections are now tabs (Outbox/Liked/Followers/Following/Raw, + a Moderation tab with a count badge once counts load). Header + tab bar always visible; tab switches preserve state; `ActiveTab` resets to Outbox on a new actor. 6 new bUnit tests. → [docs/changes/278](docs/changes/278-31.5-actor-detail-profile-header-and-tabs.md)
6. ~~31.6: **Communities — same treatment as actor detail.**~~ **COMPLETE** — `/community` reuses the 31.5 `ActorProfile` header verbatim (a Group is an Actor) + its one-long page is now tabs (Feed / Members / Followers / Following / Manage / Requests / Moderation / Raw). Header + tab bar always visible; tab switches preserve state; `ActiveTab` resets to Feed on a new community load. Pure client-side reorganization. Full fast suite green (1439/1439). → [docs/changes/279](docs/changes/279-31.6-community-profile-header-and-tabs.md)
7. ~~31.7: **Shared collection browser.**~~ **COMPLETE** — new `CollectionBrowser` component: walks the pages of an ordered collection and renders each item via a registered `ItemTemplate`, or — when none is registered — a built-in basic fallback (actor → detail link + type, object → `/object` link, bare link → `<code>`), plus an `ItemActions` slot for per-row controls (the follower Block button). ActorDetail's hand-rolled paged followers/following lists (~200 lines) are replaced by two `<CollectionBrowser>` calls; outbox/liked/inbox/mutes/blocks/flags + community followers/following/moderation move from `PagedCollection` to it. `PagedCollection` is retained for the non-collection data paths (the followed feed, community feed/members, replies). 7 new bUnit tests. Full fast suite green (1446/1446). → [docs/changes/280](docs/changes/280-31.7-shared-collection-browser.md)
8. ~~31.8: **Server — use the public URI in the namespace + host the namespace document.**~~ **COMPLETE** — the `iris:` extension namespace is now derived from the public base URI as `{BaseUri}/ns#` when `NamespaceIri` is unset (canonical default when `BaseUri` is also unset; an explicit `NamespaceIri` is honored verbatim), and the server hosts the JSON-LD namespace document at `{BaseUri}/ns` (root `GET /ns`, `application/ld+json`, long `Cache-Control`) so the advertised `@vocab` is resolvable. The SampleServer derives its namespace + advertised hostname from the same `Iris:*` base. 5 new integration tests; test host pins the canonical namespace by default (`PinDefaultNamespace`) so the existing known-namespace tests stay stable. Full fast suite green (1451/1451). → [docs/changes/281](docs/changes/281-31.8-server-namespace-derivation-and-document.md)
9. ~~31.9: **Followed-feed (`/feed`) is missing items — only 6 of 15 activities returned.**~~ **COMPLETE (verification; no server change)** — 6 integration tests confirm the followed feed returns the followed actors' data, **every activity type** (Create, Announce, Like, Accept, Note), for **local + remote** follows, under **in-memory + file-backed** persistence. The reported drop could not be reproduced in-process: the server's `FeedService.BuildFeedAsync` merges every followed actor's outbox with no per-type filter (de-duplicated by IRI, capped by `FeedOptions.MaxItems`), and the remote-walk path (`FetchRemoteOutboxAsync`) returns a remote followed's full outbox over the wire. Per the user's direction ("we just need to confirm that it does return data and that we can see that data in the sample"), this slice locks the contract with tests rather than changing the (correct) server path; the UI faithfully renders what the server returns. If the live feed still shows fewer items, the residual cause is what the followed actors' outboxes hold live (upstream delivery), not the feed-merge. 6 new tests; full fast suite green (1459/1459). → [docs/changes/282](docs/changes/282-31.9-followed-feed-returns-followed-data-verified.md)
10. ~~31.10: **Remote Likes are not recorded — a remote actor starring a local post leaves the local `liked` collection empty.**~~ **COMPLETE** — confirmed live (Mastodon → Iris, 2026-09-05): RayvenMX (mastodon.world) starred both of alice's posts; Iris accepted each `Like` (202) and stored it in the inbox, but the post's like count stayed at zero. `LikeActivityHandler` recorded the like edge only when the **liker** was a local actor; a **remote** liker's like on a local post was accepted + stored but never surfaced. The fix flips the guard: the edge is now recorded when the **liked object is local** (stored in this instance's object store), regardless of the liker's locality — so a remote actor's like on a local post is surfaced on the object's `/likes` collection + like count (the existing per-object reverse index + endpoint, decision 056 (d) — no new store or endpoint). A like of a *remote* object is still not recorded locally (the edge lives on the object's author's home instance; recording it here would duplicate). The symmetric `Undo(Like)` already removes the edge unconditionally; a two-instance integration test confirms the edge is recorded on the object's home instance, the `/likes` collection carries it over the wire, and the Undo removes it. 3 new unit tests (remote-liker-of-local-object records; remote-liker-of-remote-object doesn't; the local-liker tests now seed a local object); the `UndoLike` integration test now asserts the B-side edge + `/likes` count + Undo removal. Full fast suite green (1459/1459). → [docs/changes/283](docs/changes/283-31.10-remote-likes-recorded-on-local-objects.md)

## Paused Questions

Questions the agent asked and is waiting on a real answer for — the loop should not silently proceed past these. *(none currently)*

## Recently Completed

 - 34.27: **Post card spacing & separators** (Phase 34) — `ul.object-list li` padding 0.75rem 1rem, margin-bottom 0.75rem, border-radius 8px; `.object-item` gap 0.25rem. More breathing room between cards. Verified live.
 - 34.26: **Loading states** (Phase 34) — verified: all pages already have loading spinners (object detail, actor detail, directory, search, PagedCollection). No code change needed.
 - 34.25: **Empty states with icons** (Phase 34) — `PagedCollection.EmptyContent` RenderFragment; home/notifications/search show SVG icons + friendly messages; home has "Browse the directory →" link. Verified live.
 - 34.24: **Avatar consistency** (Phase 34) — `.actor-profile-avatar` + `.actor-profile-avatar-img` now circular (border-radius: 50%). Both profile and actor detail show 64px circular avatars. Verified live.
 - 34.23: **Landing page hero** (Phase 34) — removed card + redundant heading; signed-out landing now shows a centered hero with accent-colored title + tagline + buttons. Verified live.
Rolling window of the last ~5 slices. When a new entry pushes this over 5, move the oldest entry's one-liner into [docs/ROADMAP.md](docs/ROADMAP.md)'s ledger and drop it here.

## Keeping the docs lean

- This file is the *only* one an agent must read and update every turn. Keep it short: bounded lists, not narrative.
- Detail belongs in [docs/plans/](docs/plans/) (forward-looking scope), [docs/changes/](docs/changes/README.md) (what was built), or [docs/decisions/](docs/decisions/README.md) (why). Link, don't copy.
- [docs/ROADMAP.md](docs/ROADMAP.md) is append-only and low-churn — add a line when a phase closes, don't rewrite it.

Full operating rules, including the Inbox and Paused-Questions protocols, live in [docs/reference/AUTONOMOUS_LOOP.md](docs/reference/AUTONOMOUS_LOOP.md).
