# Iris — ActivityPub .NET Libraries

A set of .NET libraries that facilitate ActivityPub communications, designed to be embedded in existing
applications (Blazor clients, ASP.NET Core servers, or any .NET app).

This file is the **index** — it must stay small. [docs/ROADMAP.md](docs/ROADMAP.md) tracks where
we've been and what's left.

> **About this file (compacted 2026-09-03):** rebuilt index. The long running change-by-change status
> narrative (changes 161a…179, Phases 19.0b–20.7, 21.x) has been removed — that detail lives in
> [docs/changes/](docs/changes/README.md) and [docs/decisions/](docs/decisions/README.md). Only the
> active phase (Phase 22) is summarized here, with a pointer to the roadmap + the Phase 22 plan.

## Documentation

| File | Contents |
|---|---|
| [docs/reference/ARCHITECTURE.md](docs/reference/ARCHITECTURE.md) | Design principles, solution layout, cross-cutting concerns (caching, HTTP signatures, key model, proxy fallback), spec research |
| [docs/reference/PROJECTS.md](docs/reference/PROJECTS.md) | Per-project details: `Iris.Core`, `Iris.Client`, `Iris.Server`, `Iris.Server.InMemory`, `Iris.Client.Extensions`, `Iris.WebCrypto` |
| [docs/reference/TESTING.md](docs/reference/TESTING.md) | Integration-first testing strategy, multi-instance `TestServer` harness, deferred Mastodon live test |
| [docs/ROADMAP.md](docs/ROADMAP.md) | Working roadmap — where we've been, where we are, what's left (active phase = 22, the functional sample explorer) |
| [docs/changes/](docs/changes/README.md) | **Per-change docs**: one file per slice/change (build notes, key types, test counts, lightweight decisions) |
| [docs/decisions/](docs/decisions/README.md) | Design-decision documents (one file per substantial decision) |
| [docs/phase-notes/](docs/phase-notes/README.md) | Per-phase rationale + test-count history |
| [docs/plans/](docs/plans/) | **Phase 22 deep-dive plans** — the user-stories/component map + one document per component we build |
| [docs/reference/CODING_STYLE.md](docs/reference/CODING_STYLE.md) | **Binding** coding conventions, incl. 3rd-party `KristofferStrube.ActivityStreams` rules |
| [docs/reference/AUTONOMOUS_LOOP.md](docs/reference/AUTONOMOUS_LOOP.md) | Operating instructions for the autonomous dev loop + doc-maintenance rules |

## The Short Version

- **Clean and simple** — small, focused abstractions; no framework lock-in beyond .NET itself.
- **One client, two directions** — a single `net10.0` client library used by both client apps (Blazor) and servers (server-to-server).
- **Server as extension** — ActivityPub server capability added to an existing ASP.NET Core app via `IServiceCollection`/`IApplicationBuilder` extensions.
- **ActivityStreams via `KristofferStrube.ActivityStreams`** — the NuGet package provides the object model + JSON-LD; `Iris.Core` adds Iris-specific concerns (signing, identity, IRI helpers, validation) on top, never re-implements it.
- **Actor-keyed client auth** — client authenticates (Basic in v1; OAuth2 available), fetches the actor document with the private key, signs subsequent requests with it.
- **Layered caching** — short-TTL caches on client and server; every cached read has a `bypassCache` escape hatch.
- **Community-aware** — first-class `Group` actors (Lemmy-style communities) with unified feed/collection APIs.
- **Versioned API surface** — route-prefix versioning (`/ap/v1/...`); `Iris-Version` meta header; `iris:capabilities` for discovery.
- **Integration-first testing** — end-to-end tests against multiple in-process server instances, not a sprawl of unit tests.
- **AP-native flow (Phase 19.0b)** — the general flow is AP-native (every actor/community activity flows through the actor's **outbox** — client authors → outbox POST → server delivers); specialized capabilities (proxy relay, search, feeds) are `iris:`-extension-discovered but keep their own transport; mute/relay are local, non-AP, on `/local/v1`.
- **Server is the object-id authority (Decision 055)** — the server mints ULIDs for authored objects; the outbox handler returns the created object in the 2xx body; the client uses the learned id.
- **C2S inbox (Decision 056)** — the inbox is a private, owner-only, per-actor collection; inbound objects stored verbatim (no id rewrite).
- **Browser-loadable media (Decision 057)** — the client's render boundary rewrites cross-origin attachment URLs to a same-origin media proxy; the wire stays verbatim.

## Solution Layout

```
Iris.slnx
├── src/
│   ├── Iris.Core/                  net10.0 — identity, keys, signatures, IRI, caching abstractions
│   ├── Iris.Client/                net10.0 — HTTP client, signing, auth, proxy fallback, paged collections
│   ├── Iris.Client.Extensions/     net10.0 — DI/extensions for client app integration
│   ├── Iris.Server/                net10.0 — ASP.NET Core extensions, endpoints, middleware, community feeds
│   ├── Iris.Server.InMemory/       net10.0 — in-memory persistence implementation
│   └── Iris.WebCrypto/             net10.0 — WebCrypto browser signing support
├── tests/
│   ├── Iris.Testing/               shared multi-instance TestServer harness
│   ├── Iris.Core.Tests/            ├── Iris.Client.Tests/            ├── Iris.Client.Extensions.Tests/
│   ├── Iris.Server.Tests/          ├── Iris.LiveInterop.Tests/       ├── SampleServer.Tests/
│   └── SampleBlazorClient.Tests/
└── samples/
    ├── SampleServer/               minimal ASP.NET Core app hosting Iris.Server
    └── SampleBlazorClient/         Blazor WebAssembly server-explorer using Iris.Client
```

## Conventions (summary)

- **TFM: net10.0** everywhere. C# latest, nullable enabled, file-scoped namespaces.
- **`System.Text.Json` exclusively**; ActivityStream/ActivityPub types come from `KristofferStrube.ActivityStreams` — not re-implemented.
- **Central package management** (`Directory.Packages.props`).
- **Dependency direction**: `Iris.Core` → ActivityStreams + BCL; `Iris.Client` → `Iris.Core`; `Iris.Server` → `Iris.Core` + `Iris.Client` + ASP.NET Core; `Iris.Server.InMemory` → `Iris.Server`.
- **Caching**: every cached read exposes `bypassCache`. No cached path is opaque.
- **Versioning**: route prefix (`/ap/v1/...`) is authoritative; new capabilities via `iris:`-namespaced terms.
- **Testing**: integration-first (xUnit + multi-instance `TestServer` harness); unit tests reserved for pure logic. The sample explorer (the primary focus) is **tested manually** (Playwright MCP) until all wanted features exist — **no new framework tests (bUnit/`dotnet test`) for UI work**; the one exception is a **specific backend change** made while developing the UI, which ships with its normal integration test (see [docs/ROADMAP.md](docs/ROADMAP.md) Phase 22 method, rule 5).

> Full conventions, including the binding 3rd-party ActivityStreams rules: [docs/reference/CODING_STYLE.md](docs/reference/CODING_STYLE.md).

## Current Status

**Active focus: Phase 22 — the functional sample explorer.** The goal is not a beautiful UI but a more
functional explorer tool: better components, better support for viewing/reviewing activities and
objects, and better support for interacting with other servers. The work is driven by a set of **user
stories + a per-component usage map**
([docs/plans/22-sample-ui-user-stories.md](docs/plans/22-sample-ui-user-stories.md)); each item is first
**deepened into a component-level plan** under `docs/plans/22-*.md` (referenced from the roadmap's 22.0
umbrella item), then built, then **manually tested with the Playwright MCP tools** (gaps/errors resolved
before the item is closed). The high-level areas: better components (`ObjectView`, a reusable
`PagedCollection` card, a `RawInspector`, consistent card states); viewing/reviewing (object, actor,
community, instance pages + paged browsing); interacting with other servers (cross-instance navigation,
remote rendering, multi-server identity); and authoring (compose with media/markdown/sensitivity,
reply/threads). The remaining pre-Phase-22 items (the Phase 19 live-interop + raw-inspector UI halves
and the Phase 21 UI deltas) are listed in the roadmap and are largely subsumed by Phase 22.

**Latest slice (22.4, US-8 cross-instance reads):** a browser could not open a remote object or
actor — `GetObjectAsync`/`GetActorAsync` dial the target IRI directly, and a direct cross-origin GET
is CORS-blocked (a network failure with no status code, so the 401/403 `ProxyFallbackHandler` fallback
never engaged). The handler now has a **cross-instance-read** mode: a `GET` whose host differs from
the dial base is routed straight through the same-origin home proxy (no direct attempt), which relays
the remote document; a same-host `GET` dials directly. New opt-in options
(`ActivityPubClientOptions.DialBaseUri` / `RouteCrossInstanceReadsViaProxy`, surfaced on
`IrisClientOptions`) and the sample opts in (the home proxy relays both cross- and same-host reads, so
every AP read goes same-origin). 4 handler-level + 1 end-to-end pipeline test; 1,284 tests green. Live
FQDN verification is deferred to the 22.6 manual pass (the external proxy is unreachable in this env).
See [docs/changes/188-22.4-cross-instance-reads-via-proxy.md](docs/changes/188-22.4-cross-instance-reads-via-proxy.md).

**Latest slice (22.4, US-2/US-24 multi-server verification):** the multi-server identity/switching
surfaces (recent-instances one-click switch + current-instance marker, the identity bar, the
"Continue where you left off" cross-instance navigable) were **manually verified** on the real
two-instance compose stack (iris-a:8081 / iris-b:8082, internal names): log on to iris-b, load an
iris-a note through the home proxy (no CORS block, US-8), switch iris-b→iris-a one-click with the
navigable state preserved. No UI-feature code changed; the `InstanceBaseUrls` map gained the two local
compose FQDNs (`iris-dev1/dev2.luit.ink` → 8081/8082) so a logon by the advertised handle dials the
right host-published port. Build clean, format clean, suite green. See
[docs/changes/189-22.4-cross-server-manual-verification.md](docs/changes/189-22.4-cross-server-manual-verification.md).
**22.4 is done** (US-8 in change 188; US-2/US-24 in change 189).

**Latest slice (22.5, broad story review):** reviewed the full US-1…US-24 set against the sample UI and
confirmed the pages/components play together — shared `ObjectView`/`PagedCollection`/`RawInspector`/
`BackLink` in use, uniform log-on gating, consistent loading/empty/error states (no raw stack dumps),
and the cross-server surfaces (proxy reads, identity bar, recents/switch). One gap found and fixed: the
**Community page was missing the US-21 raw-JSON inspector** (it is named as serving US-21 in the plan's
component inventory but was the only detail page without it) — added `<RawInspector Document="CommunityDoc" />`
and verified it on the local compose stack. The hand-rolled read-only `following`/`followers`/`inbox`
collections are recorded as a 22.6 `PagedCollection` dedup candidate (cosmetic, not a contradiction).
Build clean, format clean, suite green (1,274). See
[docs/changes/190-22.5-broad-story-review.md](docs/changes/190-22.5-broad-story-review.md).
**22.5 is done** — next: 22.6 (implementation sweep + manual pass, incl. the recorded `PagedCollection`
dedup and the external-FQDN reverse-proxy pass).

**Latest slice (22.6, implementation sweep — first slice):** consolidated the Actor-detail **Inbox**
card (US-21) onto the shared [`PagedCollection`](samples/SampleBlazorClient/Components/PagedCollection.razor),
removing its hand-rolled load/empty/error/"Load more"/next-link state. Discovered + documented the
reliable way to give `PagedCollection` a custom item renderer: a **field-based `RenderFragment<T>`**
(built with `RenderTreeBuilder`) passed as `ItemTemplate="@InboxItemTemplate"` — an **inline lambda
attribute** with a multi-statement body is unreliable on net10.0 Razor (cascading `CS9348`), and the
`Description` string attribute cannot hold markup/interpolation (`RZ9986`). Behaviorally identical to
the old card (the owner-only inbox read yields an empty collection on a plain session, as before).
Build clean, format clean, suite green (1,274), verified on the local compose stack. See
[docs/changes/191-22.6-inbox-pagedcollection-consolidation.md](docs/changes/191-22.6-inbox-pagedcollection-consolidation.md).

**Latest slice (22.6, implementation sweep — second slice):** consolidated the Community **following**
and **followers** cards (19.8.4) onto two shared [`PagedCollection`](samples/SampleBlazorClient/Components/PagedCollection.razor)
components, driven by a single field-based `RenderFragment<IObjectOrLink>` `ItemTemplate`
(`ActorLinkTemplate`) that renders each entry as a link to its actor detail (handle + title) or a
relay-IRI fallback. Removed the following/followers state + six methods (−227 lines net); kept
`HandleOf`/`RelayIriOf` (still used by the Inbound follows / Members / Search cards). Behaviorally
identical; verified on the local compose stack (Following renders the `carla` link; Followers shows the
empty state; zero console errors). Build clean, format clean, suite green (1,274). See
[docs/changes/192-22.6-community-following-followers-pagedcollection.md](docs/changes/192-22.6-community-following-followers-pagedcollection.md).

**Latest slice (22.6, manual test pass — local compose stack):** ran the full Playwright-MCP manual pass
of the sample explorer on the two-instance local stack (iris-a:8081 / iris-b:8082 / iris-ui:8090), logged
on as `alice@localhost`, walking every page (Home, Instance, Actors, Actor detail, Object, Community,
Compose, Raw delivery, Feed). All render correctly; the WebFinger resolver resolves and navigates; the
**C2S write path returns 202 `IsSuccess=True`** (a signed `Create` minted by the server — the core
"post and have it federate" proof, local side); the four `PagedCollection` consolidations (changes 191–194)
render correctly in context. Console errors triaged: cosmetic favicon 404, the by-design owner-only inbox
403 (treated as an empty collection → "No activities delivered." empty state), and the unreachable
external-FQDN (`iris.luit.ink`) reverse-proxy route. No new gaps/regressions. **The 22.6 local manual pass
is complete**; only the external-FQDN reverse-proxy route (unreachable in this env) remains to close the
phase. See
[docs/changes/195-22.6-local-manual-test-pass.md](docs/changes/195-22.6-local-manual-test-pass.md).

**Latest slice (21.7.1, dial-base resolution — no silent localhost override):** the log-on's
  `InstanceBaseUrls` map no longer silently overrides the user's explicit base-URL input. The dial
  base is now resolved at log-on time by a shared `ResolveDialBase` helper: an entered base URL is
  used as-is (the user's explicit input always wins); an empty field derives the dial base from the
  address's host (a known local instance → its host-published port, e.g. `iris-dev1.luit.ink` →
  `http://localhost:8081`; an unknown host → the actor's home server over `https`, e.g.
  `alice@example.com` → `https://example.com`). The map override is removed from both the password
  and OAuth2 log-on paths. The README's "Logon & the base-URL / IRI-host rule" section is rewritten
  to document the new resolution behavior. Manually verified (Playwright MCP): log-on by
  `alice@iris-dev1.luit.ink` with an empty base URL dials `http://localhost:8081` (the map's value),
  the actor IRI carries the advertised FQDN, and the "Dialing" line confirms the resolved base. No
  new framework tests (UI work, Phase 22 rule 5). 1,288 tests green. See
  [docs/changes/205-21.7.1-dial-base-resolution-no-silent-localhost-override.md](docs/changes/205-21.7.1-dial-base-resolution-no-silent-localhost-override.md).

**Prior slice (21.3.1/21.3.2/21.3.3/21.4.1, object-detail interactions + feed pagination —
  verification slice):** performed a Playwright MCP manual pass on the local compose stack
  (logged on as `alice@localhost`) to confirm four already-implemented Phase 21 features work
  end-to-end, then ticked them in the ROADMAP. **21.3.1 Reply form:** typed a reply on alice's
  note, posted (202), Replies count went 2 → 3, the new reply's object page showed the content +
  "in reply to" link. **21.3.2 Like/Boost:** Like → 202 `Like` + "You liked this."; Unlike → 202
  `Undo`; Boost → 202 `Announce` + "You boosted this." **21.3.3 Delete (author only):** on a
  reply authored by the logged-on actor, Delete → the object re-loaded as a Tombstone, the Delete
  button was removed, 202 `Delete` activity. **21.4.1 Feed pagination (Load more):** the
  `PagedCollection` component (one server page per click, `HasMore = !page.IsLastPage`) loaded
  1100 items on the Community feed, confirming pagination at scale. The followed-feed (Feed page)
  showed "No followed items yet" (alice has no follows; the followed-feed endpoint 500s on the
  remote-follow fetch — the pre-existing FQDN blocker). No new code or tests (UI work, Phase 22
  rule 5; verification-only slice). 1,288 tests green. See
  [docs/changes/204-21.3.1-21.3.2-21.3.3-21.4.1-object-page-interactions-and-feed-pagination-verification.md](docs/changes/204-21.3.1-21.3.2-21.3.3-21.4.1-object-page-interactions-and-feed-pagination-verification.md).

**Prior slice (21.4.2, feed filter `?q`):** the followed-feed endpoint (`GET /u/{handle}/feed`) gains a
  `?q` content filter (case-insensitive content/name match, including nested objects — the same logic as
  the community feed's F-23 `?q`). `IFollowFeedService.GetFeedAsync` + `FeedService` gain a `query` param
  (the unfiltered build is extracted to `BuildFeedAsync`; a non-empty query delegates to a new
  `FilterFeed` static helper mirroring `CommunityFeedService.SearchCommunityAsync`); the
  `FollowFeedHandler` reads `?q` and passes it through. The Feed page (`Feed.razor`) gains a search box
  (input + Filter + Clear) that issues `?q=…`: the `FeedIri` is constructed with `?q=…` appended, and a
  `@key` on the `PagedCollection` (`feed-{query}`) forces a re-create on filter change. 4 integration
  tests verify the filter (case-insensitive match on nested Note content, no-match → empty, empty query →
  unfiltered). Manually verified (Playwright MCP): the search box renders, `?q=hello` is correctly
  appended to the feed IRI (network log), the banner/title/empty-message update on Filter and revert on
  Clear. End-to-end "filter returns matching items" is constrained by the pre-existing external-FQDN proxy
  blocker (the followed feed 500s on the remote-follow fetch before the filter is applied — as in
  19.6.1/19.6.2/21.2.2/21.2.3); the same filter logic is verified on the community feed's `?q` (22 → 1
  items for `?q=hello`). 1,288 tests green. See
  [docs/changes/203-21.4.2-feed-filter-q.md](docs/changes/203-21.4.2-feed-filter-q.md).

**Prior slice (21.2.3, member management from the list):** the Community page's **Members** list gains
  a **Remove** button per member (`RemoveMemberFromListAsync`), so the logged-on community owner removes
  a member directly rather than only via the "Manage membership" card's IRI input. The IRI-input-based
  `ManageMemberAsync` is refactored to delegate to a shared `ManageMemberAsync(bool isAdd, Iri member)`
  core that both entry points (the card's IRI input + the per-list-item button) use, so the write path is
  identical (the community-signed `Remove` posted to the community's own outbox — decision 055). A
  `MemberIriOf` helper extracts each member's actor IRI (an object's `Id` or a link's `Href`). No backend
  change. Manual "remove + confirm gone" is constrained by the pre-existing external-FQDN proxy blocker
  (as in 19.6.1/19.6.2/21.2.2): the community's collection `first` link carries the advertised FQDN
  (`iris-dev1.luit.ink`), and the page walk + membership write route through the browser proxy, which in
  this env is rate-limited (429) / CORS-blocked — so the Members list renders "No members." and the write
  429s, even though the API confirms the seeded members exist (`totalItems: 2`). On the public FQDN route
  the list populates and the button exercises the verified write path. No new framework tests (UI work,
  Phase 22 rule 5). 1,284 tests green. See
  [docs/changes/202-21.2.3-member-removal-from-list.md](docs/changes/202-21.2.3-member-removal-from-list.md).

**Prior slice (21.2.2, Feed refresh button + 19.5.5 / 19.6.6 UI half):** added a **Refresh** button to
  the Feed and Community feed cards that issues `?refresh=true` (the page-cache bypass). The
  `PagedCollection` component gains a `ShowRefreshButton` parameter + a one-shot `RefreshAsync`
  (re-fetches the first page with `BypassCache: true`, i.e. `?refresh=true`), enabled on the Feed page
  (21.2.2). The Community feed card (hand-rolled, not a `PagedCollection`) gains its own Refresh button +
  `RefreshFeedAsync` (re-fetches with `new CollectionQuery(BypassCache: true)`). No backend change (the
  `?refresh=true` wire parameter + server-side bypass handling already existed, changes 149/154). Manually
  verified (Playwright MCP): the Community feed went **1080 → 1200 items** after a Refresh click (a new
  note published from a second tab was visible only after the bypass), and every page fetch carried
  `?refresh=true`. The Feed page's followed-feed endpoint 500s in this local compose setup (FQDN
  resolution — the same documented external-FQDN blocker as 19.6.1/19.6.2); on the public FQDN route the
  button will re-fetch the followed feed. No new framework tests (UI work, Phase 22 rule 5). 1,284 tests
  green. See
  [docs/changes/201-21.2.2-feed-refresh-button.md](docs/changes/201-21.2.2-feed-refresh-button.md).


**Prior slice (19.6.2, broad signed-outbox-write enumeration — manual pass):** drove the remaining
 signed AP outbox write screens through the UI on the compose stack (logged on as `alice@localhost`
 dialing `http://localhost:8081`) and confirmed each enumerates the outbox **1:1 with no manual refresh**
 (the change-199 page-1 invalidation) and its `RawInspector` renders the signed AS document **1:1**
 (minted id, normalized advertised `object` IRI, correct `type`, `actor` = the acting local actor):
 **Block** (alice→bob, `.../blocks/06G6ERZNRD…`, 202), **Flag** (alice→bob, `.../flags/06G6ESEEZV…`, 202),
 **Like** (alice→bob note 1, `.../likes/06G6ESSGPV…`, 202). **Create** was already verified 1:1 in
 change 199. **Follow** is present (the Follow button is enabled); **Unfollow** is correctly disabled by
 the Decision 055 learned-id model (the seeded alice→bob follow has no learned activity id, so an Undo of
 that specific Follow is not offered) — by-design, not a defect. **Mute** is a local, non-AP decision
 (not an outbox candidate); **Accept/Reject/Undo** share the same `OutboxPublishHandler` path. Console
 errors reviewed — all by-design (owner-only inbox 403, non-routable `carla` placeholder CORS, 404 on
 `/replies`/`/likes`/`/shares` of non-Note activities); no 500s, no signature failures, no delivery
 errors. No code changed (a manual pass per the Phase 22 method, rule 5). 1,284 tests green. The
 external-FQDN reverse-proxy pass remains the documented blocker (as in 19.6.1). See
 [docs/changes/200-19.6.2-broad-outbox-enumeration-manual-pass.md](docs/changes/200-19.6.2-broad-outbox-enumeration-manual-pass.md).

**Prior slice (19.6.2, outbox page-cache invalidation on a local write):** fixed the server-side
 blocker behind the 19.6.2 "all activities flow through the outbox" UI half. The outbox collection page
 is served through the `LocalCollectionPageCache` (a 60s server→client response cache keyed by the page
 IRI), but a local outbox write (`Insert(0)`, newest-first) never dropped the cached page-1 — so the UI's
 plain (non-`?refresh`) outbox read lagged the activity it had just published (the outbox card showed the
 pre-write count; a `?refresh=true` read showed the new item at the head). `OutboxPublishHandler` (actor)
 and `CommunityOutboxPublishHandler` / `FinishCommunityOutboxPublishAsync` (community) now invalidate the
 owner's outbox page-1 (`{owner}/outbox`) after `AddToOutboxAsync`, via a small
 `InvalidateLocalOutboxPage` helper. Scoped to the handler path (a raw store write still relies on the
 19.6.6 `?refresh=true` escape hatch — the two are complementary). CI-pinned in
 `OutboxPublishCacheInvalidationIntegrationTests` (actor Create + community Follow), and confirmed to
 **fail** with the invalidation disabled (genuine regression guards). Verified on the compose stack:
 compose a post → 202, re-load the actor detail (plain read) → the new Create is at the head, no manual
 refresh. Create + Block write screens now enumerate 1:1. 1,284 tests green. See
 [docs/changes/199-19.6.2-outbox-page-cache-invalidation-on-write.md](docs/changes/199-19.6.2-outbox-page-cache-invalidation-on-write.md).

**Prior slice (19.6.5, audience metadata — on-the-wire `to`/`cc` enumeration):** implemented the
 production half that change 158 scoped out. The outbox publish delivered an outbound Create/Announce to
the right inboxes (the remote, non-blocked follower set) but recorded and federated the activity exactly
as the author composed it — a public post carried only `as:Public`, no per-follower enumeration on the
wire. `OutboxPublishHandler` now rewrites the activity's audience before recording (so the stored and
federated forms match): an `Announce` is addressed `to` each follower and `cc`'d to the announcer; a
`Create` appends the follower set to `cc`, keeps `as:Public` on `to`, and, for a reply (`inReplyTo`),
appends the reply target (the parent note's author) to `to`. The embedded Note is untouched. Design A
(activity-level single object, reusing `GetRemoteNonBlockedFollowersAsync` so audience and delivery stay
in lockstep). CI-pinned in `OutboxAudienceMetadataIntegrationTests` (public Create / boost / reply).
1,294 tests green. See
[docs/changes/198-19.6.5-audience-metadata-on-the-wire.md](docs/changes/198-19.6.5-audience-metadata-on-the-wire.md).

**Prior slice (19.6, minted-activity object read + client deep-link reload):** removed the
raw-inspector read blocker behind the 19.6.1 UI half. A minted activity id (e.g.
`/u/alice/blocks/{ulid}`) stored in the Activities store 404'd on the object-document endpoint, which
only consulted the Objects store — so the Object view / raw inspector could not fetch a minted
Follow/Block/Flag/Like by its id. `ObjectDocumentHandler` now falls back to the Activities store on an
Objects-store miss (a IRI in neither store still 404s). Separately, the actor/object detail pages loaded
their entity in `OnInitializedAsync` (once), so a deep-link `?iri=` param change on the already-loaded
page never re-loaded the new entity; both now use `OnParametersSetAsync` with a `_lastLoadedParamKey`
guard (reload only on a param change, not on a write-triggered re-render). Verified on the compose
stack: the minted Block fetched by its id returns the full signed AS document (200, was 404) and the
Raw inspector renders it 1:1 with the outbox; an Object-page param change re-loads the new note.
1,291 tests green. See
[docs/changes/197-19.6-minted-activity-object-read-and-deeplink.md](docs/changes/197-19.6-minted-activity-object-read-and-deeplink.md).

**Prior slice (19.6, dial-base IRI normalization):** fixed the server-side blocker behind the
19.6.1/19.6.2/19.6.3 write UI halves. A signed Follow of a *local* actor 500'd because the client
dials the instance on a host-published base (`http://localhost:8081`) and carries that base in the
activity's object reference, while the instance stores local actors under the advertised base
(`https://iris-dev1.luit.ink`) — the exact-IRI local-actor check missed, so the server treated its own
actor as remote and attempted an unroutable cross-instance delivery. The outbox publish path now
rewrites a dial-base local-actor/community object reference to the advertised base (a no-op for
already-canonical or remote targets) before recording the edge; the 500 catch block now logs the
exception. Verified on the compose stack (Follow/Unfollow → 202) and CI-pinned with three
`TestServer` integration tests. 1,289 tests green. See
[docs/changes/196-19.6-dial-base-iri-normalization.md](docs/changes/196-19.6-dial-base-iri-normalization.md).

Status per phase, with one-line summaries, lives in [docs/ROADMAP.md](docs/ROADMAP.md); per-slice build
notes in [docs/changes/](docs/changes/README.md); substantial design calls in
[docs/decisions/](docs/decisions/README.md).

## Keeping the docs lean

This file is the **index** — it must stay small. The rules for where information belongs:

- **PLAN.md** (this file): index, conventions summary, short status. Nothing else grows over time.
- **ROADMAP.md**: phases as one-line waypoints — where we've been, where we are, what's left. No build notes, no rationale.
- **docs/changes/**: one document per slice/change (build notes, key types, test counts, lightweight decisions).
- **docs/decisions/**: one document per substantial design decision; change docs link to it.
- **docs/plans/**: Phase 22 deep-dive plans (one per component/story elaborated), referenced from the roadmap's 22.0 item.
- **When in doubt, link instead of copy.** A pointer beats a duplicated paragraph.

Full rules, including the per-turn workflow: [docs/reference/AUTONOMOUS_LOOP.md — Keeping the docs lean](docs/reference/AUTONOMOUS_LOOP.md#keeping-the-docs-lean).
