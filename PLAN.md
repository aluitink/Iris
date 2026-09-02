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

**Active focus: Phase 19.0b — AP-native rework (COMPLETE).** Plan: `docs/changes/161-ap-native-rework-plan.md`. **Governing principle (operator directive):** the *general flow* is AP-native (every actor/community activity flows through the actor's **outbox** — client authors → outbox POST → server delivers); *specialized* capabilities (proxy relay, search, feeds) are backed by an `iris:`-namespaced JSON-LD extension (`iris:capabilities`) for discovery but keep their own transport; mute/relay are local, non-federated, **not** AP activities (D4a). Decisions D1–D4 are **resolved** (plan §4). All sub-slices **done**: **19.0b.1** (outbox Accept/Reject + client `AcceptAsync`/`RejectAsync`), **19.0b.2a** (removed the `follows/` follow-decision endpoints + handlers; sample UI flipped to the outbox), **19.0b.3** (steps 1+2: removed the dead follow-decision client methods — change 161c; split `ILocalModerationClient` + `LocalModerationClient` off the core AP protocol layer — change 161d), **19.0b.2b** (change 161e: relocated the mute/relay **write** routes off `/ap/v1` onto a separate non-AP `MapGroup('/local/v1')`; added `mute`/`relay` `iris:capabilities` values; the client's `LocalModerationClient` now derives the local URL from the actor IRI under `/local/v1`), and **19.0b.4** (change 161f: doc sweep — swept COMPATIBILITY_MATRIX / LIVE_EVALUATION_CHECKLIST / LIVE_INTEROP_TEST_PLAN for the removed follow-decision endpoints; fixed the stale `InboxOf` doc comments in `IActivityPubClient` (every typed method now publishes to `actorId.OutboxOf()`); renamed `DeliverAsync` param `inboxId` → `targetId`).

**Phase 19.8.7 (change 161h): error & empty states** — added `catch` + user-facing error messages to the four Blazor pages that previously let exceptions propagate unhandled (Actors, Community, Instance, Feed), following the existing `Error`/`div.error` pattern used by ObjectPage/ActorDetail/Home/Compose.

**Phase 19.6.1 (change 161i): client boost/unboost** — added the client's one-call `AnnounceAsync`/`UnannounceAsync` (boost/unboost), both published to the acting actor's outbox through the signed pipeline (the server already handled `Announce` fan-out + `Undo` generically). Every management operation is now expressible as a one-call client method. Two new integration tests pin the outbox recording.

**Phase 19.8.6 (change 161j): ObjectPage boost button** — wired the object view's **Boost/Unboost** toggle (next to Like/Unlike) into the client's `AnnounceAsync`/`UnannounceAsync`, reusing the page's `WriteBusy`/`WriteResult`/`WriteError` machinery. Two new in-process `S7ScreenTests` pin the boost + unboost round-trips.

**Phase 19.6.1 (change 161k): client community-maintenance one-call methods** — added `AddMemberAsync`/`RemoveMemberAsync` (an `Add`/`Remove` with `actor` = the community, delivered directly to the community's own inbox through the signed pipeline). Three new integration tests pin the round-trips. (161k's create-community deferral is resolved by 161l below.)

**Phase 19.5.1 / 19.6.1 (change 161l): client create-community one-call method** — added `CreateCommunityAsync(actorId, name, displayName)` (a `Create` of a `Group` authored by a person to their **own outbox** — the AP-native outbox-publish pattern) + the server's community-creation write path (the outbox-publish handler materializes a community on seeing a `Create` of a local `Group`, minting/reusing its signing key + stamping the `publicKey` extension, so the new community's document/`members`/`feed`/collections resolve). This **resolves the 161k create-community deferral**: a person (not the community) authors the `Create`, sidestepping the chicken-and-egg of a community publishing to its own not-yet-existent outbox — no non-AP activity is invented. Every management operation is now a one-call client method (no side channel). Three new integration tests pin materialization, idempotency (key reuse), and the Note-non-materialization guard. Suite 1,264, 0 failed.

**Phase 19.8.1 (change 161m): click-through audit (live, no code change)** — drove the Blazor "server explorer" against the live Docker env (Playwright, logged on as alice@iris-dev1) and clicked through every collection surface. Every *local* collection→view transition renders a proper view (actor detail, home/followed feed, actor search, community feed/members, local object deep-link) — **no raw-JSON dead ends**. Two open gaps keep 19.8.1 unchecked: (a) **remote (cross-instance) object reads CORS-fail** in the browser (the client dials the remote IRI directly with no same-origin proxy route — a 19.1.x live-interop gap), and (b) the Instance page has **no recent-instances list**. Also recorded a **harness trap**: the browser caches the WASM app bundle aggressively, so after an `iris-ui` rebuild the browser served a stale bundle that made the Boost button *appear* missing — `Network.clearBrowserCache` + a fresh boot fixed it (not a code bug). Suite 1,264, 0 failed (unchanged — no code).

 **Phase 19.8.2 (change 161n): rendered object view quality — audiences + published timestamp** — the object view (`ObjectView`) now renders the object's **audience** (its `to`+`cc` recipients, de-duplicated, the `as:Public` sentinel excluded, each as a link to the recipient's actor page) and its **`published`** time (local, RFC-3339 in the element `title`). A new shared read `IriExtensions.GetAudienceIris(this IObject?)` (+ `IsPublicAudience`) backs it. Core unit tests pin the audience read; new in-process `S19ObjectViewQualityTests` (bUnit) pin the rendered markup. Still open on 19.8.2: the reply-chain/conversations view (19.2.4), like/boost counts, and the remote canonical-URL link (needs the 19.1.x remote-read surface). Suite 1,278, 0 failed.

 **Phase 19.1.4 (change 161o): browser signed-POST 401 — the signed date is carried in `X-Signature-Date`** — the *browser* client's direct signed POST to its own outbox 401'd (the proxy fallback masked it). Root cause: a Blazor WASM host's `fetch` treats the standard `Date` request header as **forbidden** and overrides it on the wire, so the server verified the `date` signature component over a value different from the one signed → the base mismatched → 401. The client now signs over its date and carries that value in a custom, non-forbidden `X-Signature-Date` header (the browser sends it faithfully); the server verifier reads the date component from it via a new shared `Signatures.ResolveDateComponent` (falling back to the wire `Date`), and `Date` is still sent for replay protection. Both signer and verifier use the same resolve helper, so the date component can never drift. New: 4 `ResolveDateComponent` unit tests + 2 `SigningHandler` tests (incl. verification-succeeds-when-the-wire-`Date`-is-overridden). **Live-verified** (Playwright, alice@iris-dev1 FQDN dial base): direct outbox POST → **202** (not 401), `X-Signature-Date` present, wire `Date` absent, **no `/proxy/` fallback**. Also fixed a deployment gap: added `https://iris.luit.ink` (the real public UI origin) to `IRIS_CORS_ORIGINS` (`.env`, gitignored) — the cross-origin WebFinger log-on was CORS-blocked without it. Suite 1,288, 0 failed.

 **Phase 20 (planning) — end-to-end usage-story alignment.** Operator directive (2026-09-02): tighten the
 end-to-end architecture so we don't do massive overhauls later — think each topic through thoroughly, deal
 with items **one by one**, align with the **end-to-end usage story** (the user drives the sample explorer as
 a client of a local or remote instance). New [ROADMAP Phase 20](docs/ROADMAP.md#phase-20--end-to-end-usage-story-alignment-in-planning-work-item-by-item)
  items (drafted from the directive, to be refined per change): **20.0** close the in-flight decision-055
  work (ULID/learned-id — server mints object ids + returns the created object), **20.1** confirm
  the four C2S load-bearing pillars (outbox = source of truth, digest auth, proxy fallback for CORS, browse
  external collections) + decide **outbox-returns-Creates**, **20.2** the **C2S inbox design** (browser access,
  attachment storage + CORS rewrite, local-id rewrite, reply/like/boost sync, pull-on-encounter fidelity —
  design doc first), **20.3** sample-UI **outbox enumeration + paging** (local or remote), **20.4**
  implementation features (**media**, **sensitivity**, **markdown viewer**), **20.5** **test-suite triage**
  (remove the useless; keep the integration-first few — the suite is growing too fast), **20.6**
  architecture-cohesion pass, **20.7** the **manual test plan** (sample UI + wire — the capstone, **last**).
  Manual testing is deliberately saved for the end; testing discipline favors a few integration tests that
  genuinely exercise the concepts over a stack of thin unit tests.

  **Phase 20.0 (change 161p) — COMPLETE (decision 055 closed).** The in-flight decision-055 work (ULID/
  learned-id — server is the sole object-id authority) is **done**: `Iris.Core/Identity/Ulid.cs`
  (monotonic ULID) + `Iris.Server/Identity/IdMinter.cs` (DI singleton) mint every authored object's id
  (`{actorBase}/{namespace}/{ulid}`); the outbox handler accepts an id-less `Activity`, mints it (and the
  embedded object's id, preserving any client-set embedded id), and **returns the created object in the 2xx
  body** (`Results.Text`, a JSON string — the first attempt serialized it quoted, so `MintedId` parsed null);
  the inbound follow-response (Accept/Reject) is now minted under `{actor}/accepts|rejects/{ulid}` (the
  handlers inject `IdMinter`); the client drops `Id` from every authoring method and the inverse methods
  (Undo/Unlike/Unannounce/Remove/Delete) take the **learned** id (`DeliveryResult.MintedId`), never a
  recomputed formula; `AddMemberAsync`/`RemoveMemberAsync` repointed to the community **outbox**; new
  `IActivityStore.GetAllActivitiesAsync` + `ICreateIndex`. The ~10 `Iris.Server.Tests`/`Iris.Client.Tests`
  files that predicted ids by the old formula now build id-less helpers and **learn** the minted id (2xx
  body / stored outbox / enumerated activity store); the convergence + federation-signature tests locate
  minted objects **by reference**, not a computed IRI. Two bring-up bugs fixed (quoted 202 body;
  `MintActivityIds` not persisting the embedded-object mutation). `dotnet test` green: **1,111 tests, 0
  failed** (Core 210 / Client 135 / Server 766; was 5 failing at 20.0 start).

  **Phase 20.1 (change 161q) — COMPLETE (four C2S pillars confirmed + outbox-returns-Creates decided).**
  Confirmed all four load-bearing pillars hold as the current architecture implements them: **(a)** every
  client authoring method posts to `actorId.OutboxOf()` (zero inbox call sites) and the outbox-publish
  handler records the activity in the actor's outbox + activity store (embedded object in the object store
  — outbox & object store agree); **(b)** the browser's signed POST verifies (161o `X-Signature-Date`;
  signer + verifier share `ResolveDateComponent`); **(c)** the proxy fallback is a *signature-rejection*
  fallback — it engages on **401/403** (remote can't validate the browser's signature), **not** on a
  network/CORS failure (a CORS-blocked browser fetch throws with no status and propagates); CORS-blocked
  *writes* use the always-proxy mode (same-origin) and *reads* rely on the remote CORS-opening its AP
  routes — pinned by a new `DirectNetworkFailure_Propagates_NotRoutedThroughProxy` test; **(d)**
  `GetCollectionAsync`/`GetCollectionItemsAsync` are host-agnostic (any IRI, paged next-links) and
  ActorDetail (outbox) + ObjectPage (replies) render remote IRIs (Feed/Community local-only by design).
  **Decision (e):** the outbox yields **`Create`** activities (the post is the Create; the embedded Note is
  the object, addressable by its originator-minted id); the 2xx body returns the Create — matches the
  operator's expectation, no divergence. 1,112 tests, 0 failed (+1 proxy test; Client now 136).

   **Phase 20.2 (decision 056) — COMPLETE (C2S inbox: persisted, owner-only, per-actor collection).**
   Resolved all five sub-questions (decision 056): **(a)** the inbox is a **private, owner-only**
   `GET /ap/v1/u/{handle}/inbox` (Basic auth via the existing `IActorCredentialValidator` seam; 403 for a
   non-owner; no-store); **(b)** the inbound object is **stored locally verbatim** (the object store already
   does this under the originator id) and remote attachments are **served verbatim (link-out)** for now —
   local media storage + URL-rewrite is staged to **20.4**; **(c)** **no id rewrite** (inbound keeps the
   originator id per decision 055; the inbox yields by that id and a request serves the local copy);
   **(d)** reply + like/boost sync is **out of scope** (follow-up: the inbox surfaces the delivered
   activities; thread-tree + per-object counters are separate slices); **(e)** **store-on-receive**
   (current, higher-fidelity-than-TTL behavior); fetch-on-encounter is a follow-up. **Implemented:** the
   inbox is a first-class, per-actor collection (`IActivityStore.GetInboxAsync`/`AddToInboxAsync` + both
   impls), recorded on first delivery by the `InboxProcessor`, read via the owner-only route +
   `GetInboxItemsAsync` client method. **Remaining (staged, self-contained):** local media rewrite (20.4),
   reply thread-tree, per-object like/boost counters, fetch-on-encounter. `docs/decisions/056-c2s-inbox-model.md`;
   `docs/changes/161r-20.2-c2s-inbox-design.md`. Suite: Server 770/770 (+4 inbox tests), Client 136/136.
   **Note:** 13 pre-existing 055-fallout failures (a server bug in `RecordCreateLocalAsync` — it did not
   record the object→Create index, so locally-posted notes' Deletes could not remove the Create from the
   outbox — + 12 stale tests still referencing pre-055 deterministic IRIs) were found and fixed in a
   preceding `fix:` commit; with that repair the full suite is green.

   **Phase 20.3 (change 161s) — COMPLETE (Sample-UI outbox enumeration + paging, local or remote).**
   Makes 20.1's "outbox = source of truth" and 20.2's inbox design **visible in the UI**: the actor
   detail page's non-paged "Feed (outbox)" card (which loaded the *entire* outbox up front) is replaced by
   a paged **"Outbox" surface** — first page via the paged `IActivityPubClient.GetCollectionAsync` against
   `{actor}/outbox` (no `CollectionQuery.Limit` — the server pages at its natural size, and "Load more"
   walks those `next`-linked server pages), each item a navigable `<ObjectView>`, a "Load more" button
   resuming from `CollectionPage.NextPage` (one server page per click). **Local or remote** — the paging
   contract is host-agnostic. UI-only (no server/client/persistence changes); reuses the existing
   paged-collection client + object view. `PendingFollows` still reads the full outbox (a separate
   concern needing all `Follow`s). +4 integration tests (`S20OutboxPagingTests`, mirroring
   `S3FollowFeedTests`'s harness). `docs/changes/161s-20.3-outbox-enumeration-paging.md`.
   **Suite green, 0 failed:** Core 210, Client 136, Ext 29, Server 770, LiveInterop 18,
   SampleBlazor 97 (+4), SampleServer 25 — **1,285 total**.

    **Phase 20.4 (b) (change 161t) — PARTIAL: sensitivity done.** A **content-sensitive object**
    (`sensitive` term + `summary`) now renders **behind a notice** in the object view, its real content
    **absent from the DOM** until the viewer reveals it (a Show/Hide toggle). Core:
    `IriExtensions.IsSensitive` (the `sensitive` term surfaces via the library's `ExtensionData` — the
    v0.2.4 `Object` type does not model it as a property — read per the 3rd-Party Rule 6) +
    `IriExtensions.GetSummary` (joins the typed `summary`). ObjectView branches on `IsSensitive` (notice +
    reveal; content rendered only when revealed). +17 tests (10 Core read-helper, 5 bUnit object-view, 2
    integration round-trip). `docs/changes/161t-20.4-sensitivity.md`.
    **Suite green, 0 failed:** Core 220 (+10), Client 136, Ext 29, Server 770, LiveInterop 18,
    SampleBlazor 104 (+7), SampleServer 25 — **1,302 total**.

    **Phase 20.4 (c) (change 161u) — PARTIAL: markdown done.** A note's `content` that is **Markdown** now
    renders properly in the object view (headings, inline/fenced code, links, lists, emphasis) via a small
    **dependency-free** `Markdown` renderer (sample-local — a deliberate choice over adding a markdown
    NuGet just for a sample-UI feature; **no new package**). It HTML-escapes first (raw markup inert) and
    allow-lists link schemes to http/https/mailto (a `javascript:` scheme renders as text, never a live
    link). The object view renders `content` through it (emitted as a `MarkupString`); the sensitive-notice
    path renders the markdown only after reveal. +32 tests (25 `MarkdownTests` unit, 7 bUnit object-view).
    `docs/changes/161u-20.4-markdown.md`.
    **Suite green, 0 failed:** Core 220, Client 136, Ext 29, Server 770, LiveInterop 18,
    SampleBlazor 136 (+32), SampleServer 25 — **1,334 total**.

     **Phase 20.4 (a) (change 161v) — DONE: media (closes Phase 20.4).** A note's attachment is uploaded to
    the author's instance (a local, Basic-authenticated multipart POST, `POST /local/v1/u/{handle}/media`,
    owner-only, 10 MiB cap) which stores the bytes and returns (201) the same-origin media IRI; the uploader
    sets that IRI as the `url` of an `Image` attachment on the note it posts. `GET /ap/v1/media/{id}` serves
    the bytes back from the same origin (long-cacheable), so the browser loads the attachment from the same
    origin, never a cross-origin media host (Decision 056 (b)). Storage: `IMediaStore` (in-memory +
    file-backed, wired into both persistence providers). Client: `IMediaClient` (Basic-auth multipart POST)
    + `PostNoteAsync(Iri, Note, ct)` + factory/`IrisClientBundle` `CreateMediaClient`. Core:
    `IriExtensions.GetMediaAttachments` (single boundary read of an object's `Image` attachments → same-origin
    media IRIs). Sample: compose uploads via `InputFile` (Blazor WASM `IBrowserFile`); the object view renders
    the attachment `<img>`. +23 tests (6 `MediaClientTests`, in-memory + file-backed store tests,
    upload/serve integration, round-trip integration). `docs/changes/161v-20.4-media.md`.
    **Suite green, 0 failed:** Core 220, Client 142 (+6), Ext 29, Server 789 (+19), LiveInterop 18,
    SampleBlazor 136, SampleServer 25 — **1,371 total**.

    **Next: Phase 20.5 — test-suite triage.** Phase 20.4 (media, sensitivity, markdown) is closed. 20.5
    audits the suite (now ~1,371 tests) to **remove tests that add no coverage** (duplicates, over-fine
    unit tests of trivial glue, tests that pin implementation details that changed with 055) and
    keep/grow the integration tests that genuinely exercise the concepts; target a smaller or stable count
    with higher per-test value. Then 20.6 architecture-cohesion pass → 20.7 manual test plan (capstone).

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
