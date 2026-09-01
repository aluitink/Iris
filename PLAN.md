# Iris — ActivityPub .NET Libraries

A set of .NET libraries that facilitate ActivityPub communications, designed to be embedded in existing applications (Blazor clients, ASP.NET Core servers, or any .NET app).

This file is the **index** — it must stay small. [docs/ROADMAP.md](docs/ROADMAP.md) tracks where we've been and what's left.

## Documentation

| File | Contents |
|---|---|
| [docs/reference/ARCHITECTURE.md](docs/reference/ARCHITECTURE.md) | Design principles, solution layout, cross-cutting concerns (caching, HTTP signatures, key model, proxy fallback), spec research |
| [docs/reference/PROJECTS.md](docs/reference/PROJECTS.md) | Per-project details: `Iris.Core`, `Iris.Client`, `Iris.Server`, `Iris.Server.InMemory`, `Iris.Client.Extensions`, `Iris.WebCrypto` |
| [docs/reference/TESTING.md](docs/reference/TESTING.md) | Integration-first testing strategy, multi-instance `TestServer` harness, deferred Mastodon live test |
| [docs/ROADMAP.md](docs/ROADMAP.md) | Working roadmap — where we've been, where we are, what's left (Phase 19 = live manual integration-testing program) |
| [docs/changes/](docs/changes/README.md) | **Per-change docs**: one file per slice/change (build notes, key types, test counts, lightweight decisions) |
| [docs/decisions/](docs/decisions/README.md) | Design-decision documents (one file per substantial decision) |
| [docs/phase-notes/](docs/phase-notes/README.md) | Per-phase rationale + test-count history |
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
- **Testing**: integration-first (xUnit + multi-instance `TestServer` harness); unit tests reserved for pure logic.

> Full conventions, including the binding 3rd-party ActivityStreams rules: [docs/reference/CODING_STYLE.md](docs/reference/CODING_STYLE.md).

## Current Status

Phases -1 through 12, 15, 16, 17, and 18 are complete, as are the Sample Explorer enhancement rounds (all rounds live-browser-verified). **Phase 19.0 (evaluation environment) is complete**. **Phase 19.1.1 (Iris↔Iris baseline) is complete**: F-1911-1 and F-1911-2 **fixed** (commit 262fd09); F-1911-3 root cause confirmed (community signing identity not registered). **Phase 19.1.2 (follow scenarios) in progress**: F2 (we follow RayvenMX) **PASS (signature)** — the F-1912-1 signature fix made Mastodon accept our Follow (202); F-1911-3 (community signing identity not registered) **fixed** (server + client) and verified (regression test + community-signed follow to Mastodon 202 via IrisSigner). RayvenMX's `Accept` still pending (their side to process). F1/F3/F4 not tested (require RayvenMX's action). **Phase 19.3.1/19.3.2 (two-instance loop/echo safety) is complete** (commit 262616e — inbox add-if-absent re-delivery guard); **Phase 19.3.3 (Announce propagation) is complete** (commit a0c86ad — propagated-boost IRI keyed to the announcer; local followers recorded in their outbox directly). **Phase 19.3.4 (Delete propagation, both directions) is complete** (direction 2 — a remote actor deletes a note we hold a copy of — now covered by a two-instance test proving the `DeleteActivityHandler` owner guard, correct scope, and no re-propagation; change 144). **Phase 19.3.5 (Follow-edge convergence) is complete** (a two-instance Follow/Undo/Follow cycle over the wire converges both sides' `following`/`followers` to the single edge — no orphan, no duplicate, stable public collections; the `UndoActivityHandler` already removed the edge on both sides, so no production change; change 145). **Phase 19.3.6 (Update propagation) is complete** (direction 2 — a remote author edits a note we hold a copy of — now covered by a two-instance test proving the `UpdateActivityHandler` owner guard, correct scope, and no re-propagation; direction 1 was already covered; no production change; change 146). **Phase 19.3.7 (Recreation stability) is complete** (a recreated host's un-truncated file-backed delivery journal replays the already-delivered Create over the wire — a genuine re-transmission, not a no-op that never left — and the peer's `InboxProcessor` add-if-absent-by-Id gate stores it as a no-op: the note is stored exactly once, the recipient's outbox is unchanged in length (no duplicate edge), and there is no re-fan-out storm; no production change — the guarantee rests on the at-least-once journal replay + the inbox-Id guard, pinned end-to-end by `Recreation_DeliveredCreateReplayed_StoredOnce_NoReFanOut_OutboxUnchanged`; change 147). **Phase 19.5.1 (community creation surface) — community READ surface complete** (change 148): the community document, `members`, `feed`, `following`/`followers`, and search collections were already served; the missing piece was the advertised **`outbox` link** — `GET /ap/v1/c/{name}/outbox` (the READ counterpart of the `POST` community outbox publish endpoint) is now a paged collection served through the local collection-page cache, so a remote client resolving the community's outbox link finds the community's authored activities instead of a 404; 5 new integration tests, full suite 1,204 green. Still open for full 19.5.1: the UI creation *write* path + WebFinger/`iris:capabilities` discovery (live/UI verification). **Phase 19.5.5 (community feed correctness) — newest-first merge fixed** (change 149): the community feed was documented "newest first" but actually concatenated the members' outboxes in member-IRI order (grouped by member), so a member's newest post did not rank above another member's older post; `ICommunityFeedService` now merges by (outbox position, then member IRI) — a stable, deterministic newest-first merge — keeping the IRI de-duplication and `?q` content filter; new `CommunityFeedCorrectnessIntegrationTests` + updated `CommunityFeedIntegrationTests`/`CommunitySearchIntegrationTests` order assertions, full suite 1,208 green. Still open for full 19.5.5: the remote-content half + `?refresh=true` bypass (live/UI verification). **Phase 19.5.2 (community membership management) — self-management gate added** (change 150): the `Add`/`Remove` membership mechanism existed (F-09) but had no authorization; `AddActivityHandler`/`RemoveActivityHandler` now apply a 19.5.2 self-management gate (an `Add`/`Remove` to a community inbox applies only when the activity's actor is that community itself — mirroring the community outbox publish endpoint's actor gate), a community-signed `Add`/`Remove` through its own inbox adds/removes the member with the community feed + `members` collection reflecting it on the wire, and a remote actor's signed `Add`/`Remove` is stored but no longer modifies membership; new `CommunityMembershipManagementIntegrationTests` + updated `AddRemoveFederationIntegrationTests`, full suite 1,211 green. Still open for full 19.5.2: the UI membership screens + remote-actor join-request/accept flow (live/UI verification). **Person inbound-follow accept/reject (the `manuallyApprovesFollowers` live half, J-10 / Resolved Decision #46) is complete** (change 151): a single operator follow-decision endpoint (`POST /ap/v1/u/{handle}/follows/{**followId}`, Basic-auth; a trailing `/accept` selects acceptance, otherwise reject) builds + records + server-delivers the deterministic `Accept` (ensures the edge) or `Reject` (removes the edge) — the remote finalizes/removes its edge on receipt — replacing the old body-based reject-only endpoint; the client (`AcceptFollowAsync`/`RejectFollowAsync`), the sample UI "Inbound follows" card, the opt-in `Iris__ManuallyApprovesFollowers` sample flag, and surfacing inbound follows in the followed actor's outbox are all in; verified end-to-end over the two-instance Docker env (signed inbound follow of a gated alice → operator Accept finalizes the edge on both sides; Reject removes it on both sides; unauthenticated → 401); full suite 1,217 green. **Community inbound-follow accept/reject (19.5.3) is complete** (change 152): the person decision logic is extracted into a shared follow-decision core and a community variant — `POST /ap/v1/c/{name}/follows/{**followId}` (Basic-auth, the community's IRI is the credential seam) — builds + records the deterministic `Accept`/`Reject` in the activity store + the community's outbox and ensures/removes the community's follower edge (`ICommunityStore` followers set); the community branch of `FollowActivityHandler` now surfaces the inbound follow in the community's outbox and applies the `manuallyApprovesFollowers` gate (a gated community records its edges but does not auto-accept); 12 new integration tests (incl. a local *community* following the community → 403) + 2 handler unit tests, full suite 1,231 green. **Community moderation surface (19.5.4) is complete** (change 153): the community's own block/flag/mute sets (`ICommunityStore`, keyed by `communityIri` — the community is the moderator, the actor is moderated, distinct from the person `IModerationStore`) are now recorded by both the in-memory and file-backed stores (round-tripped through a `blocks`/`flags`/`mutes` document section); the community feed (`CommunityFeedService`, now given the community store via the persistence provider) excludes a blocked member's content (hard) and a muted member's (soft — the membership is kept), while a *flagged* member is **not** excluded (a flag is a report, not a filter — mirroring the person feed); `GET /ap/v1/c/{name}/{blocks|flags|mutes}` serves the community's moderation collections (mirrors the person collections for a `Group`) and the community document advertises the three links; `POST /ap/v1/c/{name}/mutes/{target}` (Basic auth, the community's IRI is the credential seam) records/removes a community-scoped mute (`?unmute=true`) — block/flag are the federated `Block`/`Flag` activities, not a local POST; 11 new integration + 6 new unit tests, full suite 1,248 green. **Phase 19.5.5 (community feed correctness — `?refresh=true` cache bypass) is complete** (the last open item; the newest-first merge was change 149 and the remote-content half was `RemoteContent_ToCommunityInbox_PropagatesToMemberAndAppearsInFeed`): the community collections (feed, members, following/followers, blocks/flags/mutes) are now served through `LocalCollectionPageCache` — a plain read caches the page (`max-age=60, stale-while-revalidate=300`), `?refresh=true` bypasses it + emits `no-cache`, and the feed's `?q` filter is part of the cache key; pinned by the new `CommunityFeed_IsServedFromThePageCache_WithRefreshBypassAndCacheControl`, full suite 1,249 green; change 154. Still open for full 19.5.4/19.5.5: the community UI moderation screen + feed screen and the two-instance wire drive of a signed `Block`/`Flag` to a community (live/UI verification). **Phase 19.5.6 (community lifecycle on recreation) is complete** (change 155): a community created in a prior turn — with members, follows, followers, community-scoped moderation edges (19.5.4), and content (the members' outbox activities that feed the unified feed) — survives a `down`/`up` (volume-backed) with every collection intact; the file-backed `FileBackedCommunityStore` + `FileBackedActivityStore` round-trip the full state, pinned by `Community_FullState_MembersFollowsFollowersModerationAndContent_SurvivesRestart` (a fresh provider over the same directory, the `down`/`up` simulation), full suite 1,250 green. Next: continue 19.5.x community slices (19.5.3 community UI "Inbound follows" card + wire drive; 19.5.4/19.5.5 community UI screens; 19.5.6 live Docker `down`/`up` drive) and 19.1.3–19.1.8 live verification (and 19.4 remediation triage).

- **Blocked (external)** — Phase 13.5–13.10 live interop and Phase 14 remediation are folded into Phase 19.1 (live interop verification) + 19.4 (remediation); the CI-testable sub-slices and the CI-gating model are already done.
- **Tabled** — external/remote community-style interaction testing (per operator decision).

Status per phase, with one-line summaries, lives in [docs/ROADMAP.md](docs/ROADMAP.md); per-slice build notes in [docs/changes/](docs/changes/README.md); substantial design calls in [docs/decisions/](docs/decisions/README.md).

## Keeping the docs lean

This file is the **index** — it must stay small. The rules for where information belongs:

- **PLAN.md** (this file): index, conventions summary, short status. Nothing else grows over time.
- **ROADMAP.md**: phases as one-line waypoints — where we've been, where we are, what's left. No build notes, no rationale.
- **docs/changes/**: one document per slice/change (build notes, key types, test counts, lightweight decisions).
- **docs/decisions/**: one document per substantial design decision; change docs link to it.
- **When in doubt, link instead of copy.** A pointer beats a duplicated paragraph.

Full rules, including the per-turn workflow: [docs/reference/AUTONOMOUS_LOOP.md — Keeping the docs lean](docs/reference/AUTONOMOUS_LOOP.md#keeping-the-docs-lean).
