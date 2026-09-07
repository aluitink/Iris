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

**Phase 37 — Profile editing & remaining UI gaps (COMPLETE).** 37.1–37.5 all COMPLETE. Phase 36 COMPLETE. Phase 35 COMPLETE.

**Phase 38 — Communities & settings (COMPLETE).** 38.1–38.3 all COMPLETE.

**Phase 39 — Community management & settings (COMPLETE).** 39.1–39.3 all COMPLETE.

**Phase 40 — Community management polish (COMPLETE).** 40.1–40.3 all COMPLETE.

**Phase 41 — Notifications & engagement polish (COMPLETE).** 41.1–41.3 all COMPLETE.

**Phase 42 — Community settings & instance admin (COMPLETE).** 42.1–42.3 all COMPLETE.

**Phase 43 — Per-user moderation & follow-request queue (COMPLETE).** 43.1–43.3 all COMPLETE.

**Phase 44 — Post content completeness (ACTIVE):**

1. ~~**44.1: Content warning / sensitive flag on compose (F-28)**~~ **COMPLETE** — "Content warning" checkbox + summary input on Compose (Note posts); the Note path builds the note via `ComposeNote.Build` (sensitive + summary + to) and posts it via the `PostNoteAsync(Note)` overload; the feed's existing reveal toggle renders it. 4 integration tests. → [docs/changes/336](docs/changes/336-44.1-content-warning-compose.md)
2. **44.2: Edit own post (F-02)** — an "Edit" action on own notes (object detail + compose pre-fill) that updates the note's content; client `UpdateNoteAsync` (an `Update` activity) if not present, or reuse the existing update path; server handles `Update` on a note object. 5+ integration tests.
3. **44.3: Media attachment (image) on compose** — file upload via `IMediaClient` (Phase 20.4a) → same-origin media IRI; `ComposeNote.Build`/note `attachment` carries an `Image`; feed renders the image. Depends on media storage being wired in the app.

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
15. ~~34.28: **Object detail — show like count + boost count**~~ **COMPLETE** — `ObjectDetail.razor` fetches `/likes` and `/shares` collections via `GetLikesAsync`/`GetSharesAsync`; shows "N likes · M boosts" under the note when counts > 0. Verified live (no likes/boosts yet, so text correctly hidden).
16. ~~34.29: **Favicon + meta tags**~~ **COMPLETE** — added `favicon.svg` (stylized iris flower in app accent color) + `<link rel="icon">` + `<meta name="description">` in `App.razor` head. Verified live: favicon served at `/favicon.svg` (200), meta present in HTML.
17. ~~34.30: **Responsive nav**~~ **COMPLETE** — `@media (max-width: 640px)`: `.main-nav` becomes `flex-wrap: nowrap; overflow-x: auto` (horizontal scroll, no scrollbar); `.main-header` allows wrapping (brand on its own line). Verified at 375px and 1024px.
18. ~~34.31: **Color & typography refinement**~~ **COMPLETE** — warmer bg (`#111318`), explicit `h1`/`h2`/`h3` hierarchy (1.6/1.35/1.1rem, 700/700/600 weight), `--accent-warm` for links/nav-hover/brand, line-height 1.6, brand 1.3rem with tighter tracking. Verified live.
19. ~~34.32: **Object detail — show the full thread**~~ **COMPLETE** — `ObjectDetail.razor` fetches parent via `GetParentIri()` + `GetObjectAsync`; shows "In reply to [author]" + truncated parent content in a styled blockquote card above the note. Verified live with a real reply.
20. ~~34.33: **Re-evaluate and generate new work**~~ **COMPLETE** — systematic UI review of all pages (home, profile, notifications, directory, compose, search, object detail, actor detail, landing). Identified key gaps: no timestamps on cards, no engagement actions on timeline cards, Follow activities polluting home feed, no post deletion UI, no character count on compose. Generated Phase 35 below.

**Phase 35 — Timeline actions & content completeness:**

21. ~~35.1: **Server — set `published` timestamp on outbox-published activities**~~ **COMPLETE** — `MintActivityIds` sets `activity.Published = DateTime.UtcNow` when absent + sets `Published` on embedded Create objects. `InboxProcessor` sets fallback on inbound activities. `CreateActivityHandler.StoreEmbeddedObjectAsync` sets object `Published` from activity's time. New posts show "just now" / relative time. Verified live.
22. **35.2: Timeline cards — show relative timestamps** — verified as part of 35.1: new posts show "just now" with ISO 8601 in `<time title>`. Existing posts (pre-35.1) lack timestamps — expected, no migration. Marked COMPLETE.
23. ~~35.3: **Home timeline — filter out social activities (Follow, Accept, Reject)**~~ **COMPLETE** — `PagedCollection` gains an optional `ItemFilter` predicate; `HomeTimeline` passes `IsContentItem` (only Create(Note/Article) + Announce render). Server feed API unchanged. Verified live: Follow activity no longer appears in home feed.
24. ~~35.4: **Timeline cards — inline engagement actions (like, boost, reply)**~~ **COMPLETE** — new `EngagementBar` component (like/boost/reply buttons) integrated into `ObjectView` for Create(Note/Article), Announce, and bare Note/Article. Like/boost use `LikeAsync`/`AnnounceAsync` with optimistic count updates; reply links to `/compose?replyTo=`. Verified live: like toggles 0→1→0.
25. ~~35.5: **Object detail — delete own posts**~~ **COMPLETE** — Delete button on object detail (own posts only, via `attributedTo` check). Two-step confirm → `DeleteAsync` → redirect to `/home`. Verified live: appears on own post, not others'; full delete flow works.
26. ~~35.6: **Compose — character count + content type**~~ **COMPLETE** — character counter ("N/500", red when over) + Note/Article dropdown (hidden for replies). Article posts via `DeliverAsync` with an `Article` object. Verified live.
### Recently completed this session (34.1–34.14)

See the **Recently Completed** section below for the rolling window.

## Up Next

Short, bounded list — only the next few items, not the whole roadmap.

**Phase 42 — Community settings & instance admin (COMPLETE):**

1. ~~**42.1: Community "manuallyApprovesMembers" toggle**~~ **COMPLETE** — community edit form checkbox ("Require approval for join requests"); on save calls `SetManuallyApprovesMembersAsync` when the flag changed; reads state via `GetManuallyApprovesMembers`; 5 integration tests. → [docs/changes/330](docs/changes/330-42.1-community-approve-members-toggle.md)
2. ~~**42.2: Person "manuallyApprovesFollowers" toggle**~~ **COMPLETE** — profile edit form checkbox ("Require approval for follow requests"); on save calls `SetManuallyApprovesFollowersAsync` when the flag changed; reads state via `GetManuallyApprovesFollowers`; 5 integration tests. → [docs/changes/331](docs/changes/331-42.2-profile-approve-followers-toggle.md)
3. ~~**42.3: Instance admin — user list**~~ **COMPLETE** — `/admin/users` page (admin-only, "Admin" policy); lists all local accounts (handle, role badge, created date); reads `IUserAccountStore.GetAllAsync()` in-process; 5 integration tests. → [docs/changes/332](docs/changes/332-42.3-admin-user-list.md)

**Phase 43 — Per-user moderation & follow-request queue (COMPLETE):**

1. ~~**43.1: Moderation actions on posts (Block/Mute/Flag)**~~ **COMPLETE** — "⋯" dropdown on `EngagementBar` (author ≠ self); Block/Flag via `IActivityPubClient`, Mute via `ILocalModerationClient`; 6 integration tests. → [docs/changes/333](docs/changes/333-43.1-post-moderation-actions.md)
2. ~~**43.2: Follow-request queue (person actor)**~~ **COMPLETE** — "Requests" tab on `/profile` (visible when `manuallyApprovesFollowers` is on) lists pending Follows from the outbox; Accept/Reject via `IActivityPubClient`; 5 integration tests. → [docs/changes/334](docs/changes/334-43.2-follow-request-queue.md)
3. ~~**43.3: Moderation actions on actor detail (Block/Mute/Report)**~~ **COMPLETE** — `ModerationActions` on `ActorDetail` (other actors): Block/Report via `IActivityPubClient`, Mute via `ILocalModerationClient`, with Undo state from the blocks/mutes collections; plus a community "Requests" join-request tab (accept/reject). 8 integration tests. → [docs/changes/335](docs/changes/335-43.3-actor-detail-moderation.md)

*(Phases 32–42 are complete — see docs/changes/ for details.)*



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

  - 41.3: **Community membership requests (admin UI)** (Phase 41) — "Requests" tab (creator only): lists pending join requests; Accept/Reject buttons; `GET /local/v1/c/{name}/requests` + `POST .../accept/{**actorIri}` + `POST .../reject/{**actorIri}`; `ILocalModerationClient` join-request methods; 10 integration tests.
  - 41.2: **Profile engagement tabs** (Phase 41) — `/profile` tab bar (Your posts / Replies / Likes); inbox-filter via `PagedCollection.ItemFilter`; 5 integration tests.
  - 41.1: **Notification read-state + unread badge** (Phase 41) — "Mark all as read" button + nav unread badge (60s poll); `POST /local/v1/notifications/read` + `GET /local/v1/notifications/unread-count`; in-process `NotificationService`; 7 integration tests.
  - 44.1: **Content warning / sensitive flag on compose** (Phase 44) — CW checkbox + summary on Compose (Note posts); note built via `ComposeNote.Build` (sensitive + summary); feed reveal toggle already renders it; 4 integration tests.
  - 43.3: **Moderation on actor detail** (Phase 43) — `ModerationActions` (Block/Report via `IActivityPubClient`, Mute via `ILocalModerationClient`, Undo state from blocks/mutes collections) + community join-request "Requests" tab; 8 integration tests.
  - 43.2: **Follow-request queue (person)** (Phase 43) — "Requests" tab on `/profile` (when `manuallyApprovesFollowers` on); pending Follows from outbox; Accept/Reject; 5 integration tests.
  - 43.1: **Moderation actions on posts** (Phase 43) — "⋯" dropdown on `EngagementBar` (author ≠ self); Block/Flag via signed outbox, Mute via local Basic-auth; 6 integration tests.
  - 42.3: **Instance admin — user list** (Phase 42) — `/admin/users` page (admin-only); lists local accounts; 5 integration tests.
Rolling window of the last ~5 slices. When a new entry pushes this over 5, move the oldest entry's one-liner into [docs/ROADMAP.md](docs/ROADMAP.md)'s ledger and drop it here.

## Keeping the docs lean

- This file is the *only* one an agent must read and update every turn. Keep it short: bounded lists, not narrative.
- Detail belongs in [docs/plans/](docs/plans/) (forward-looking scope), [docs/changes/](docs/changes/README.md) (what was built), or [docs/decisions/](docs/decisions/README.md) (why). Link, don't copy.
- [docs/ROADMAP.md](docs/ROADMAP.md) is append-only and low-churn — add a line when a phase closes, don't rewrite it.

Full operating rules, including the Inbox and Paused-Questions protocols, live in [docs/reference/AUTONOMOUS_LOOP.md](docs/reference/AUTONOMOUS_LOOP.md).
