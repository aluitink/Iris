# Sample Docker Composition — Build Plan (Phase 8 Enhancement)

> Status: in progress (S1–S3 done, [change 070](changes/070-sample-federation-ready.md) / [change 072](changes/072-sample-blazor-wasm-explorer.md)) · Part of the [Iris plan](../PLAN.md). Detailed plan for the Phase 8 sample; the
> [Roadmap](ROADMAP.md) carries only the waypoints/checkboxes and the root [PLAN.md](../PLAN.md) carries
> only the status row. Per the [doc-lean rules](reference/AUTONOMOUS_LOOP.md#keeping-the-docs-lean), heavy
> build notes for each slice land in [changes/](changes/README.md) as they complete.
>
> **Goal:** turn the existing two-instance server compose stack into a **full, deployable, self-contained
> sample** that a user can boot with one `docker compose up` and then **navigate a Blazor WASM "server
> explorer" UI** to enumerate and explore mock ActivityPub data on the sample servers. The sample is the
> project's **real-world test platform**: it exercises the library end-to-end over real network I/O,
> surfaces interop bugs, and validates feature coverage (instance→instance, and instance→external
> instance).

---

## 1. Purpose and non-goals

### Purpose

- A **one-command, Docker-only** sample: `docker compose up --build` yields two real Iris ActivityPub
  instances plus a browser-reachable **server explorer** UI. No host-side .NET, no FQDNs, no DNS config —
  the project must run **self-contained on routable Docker addresses**.
- The UI is a genuine **client of the Iris libraries** (it is the Blazor WASM host that Phase 7 deferred),
  so every screen exercises the real signed-client pipeline — auth → sign → fetch → follow → post →
  reply → like → search — against live instances.
- The sample doubles as the **interop test bed**:
  - **instance→instance** — `iris-a` and `iris-b` follow each other, post, and read each other's
    collections over `iris-net` (Docker DNS, routable hostnames).
  - **instance→external** — the same UI can log on to / explore a **remote external instance** (a real
    ActivityPub server) using its WebFinger address, so we can probe signatures, content types,
    pagination, and delivery against a foreign implementation.

### Non-goals (deferred / out of scope here)

- **Real persistence.** All sample state stays in-memory (`Iris.Server.InMemory`); a restart resets it.
  Real persistence is Phase 14+.
- **OAuth2 / bearer auth.** The sample keeps **Basic auth** for logon (the v1 model). Auth upgrade is
  Phase 14+.
- **TLS / public FQDNs.** The Docker-only stack runs plain HTTP on routable service names. The real
  public FQDN + TLS work is Phase 9/13 (see [DEPLOYMENT_PREP.md](reference/DEPLOYMENT_PREP.md)).
- **A third "external" Iris instance in compose.** External-instance testing uses the **dev FQDNs** (local
  env only, not part of the project — see §7) or a hand-pointed URL; we do not bake a third container into
  the default topology.
- **Relay / shared-inbox-as-a-service.** Not required to demonstrate the sample.

---

## 2. Current state (what we have to build from)

| Piece | State | Notes |
|---|---|---|
| `samples/SampleServer/` | ✅ runnable, containerized | `CreateWebHostBuilder` composition root; seeds alice + bob + `iris` community + 2 notes; multi-stage `Dockerfile`; wired into compose as `iris-a`/`iris-b`. **No inbound signature validation**, minimal seed, no UI. |
| `samples/SampleBlazorClient/` | ⚠️ console composition root only | `SampleBlazorClient.CreateClientService` + `ClientService` wire the real `Iris.Client.Extensions` bundle (Basic-auth login → PEM key → signed client). `Program.cs` is a console pipeline (login → community feed). **No Blazor WASM project yet.** |
| `docker-compose.yml` | ✅ two instances | `iris-a`/`iris-b` on `iris-net`, host ports `8081`/`8082`, health checks. **No UI service, no UI build.** |
| `scripts/docker-smoke-test.sh` | ✅ opt-in | Boots stack, waits for health, asserts cross-container WebFinger. **No UI checks, no federation-write checks.** |
| Library surface | ✅ rich (Phases 0–12) | Full signed client (`IActivityPubClient`), WebFinger discovery (`bundle.ResolveActorAsync`), communities, feeds, replies, search, moderation, relay, proxy fallback, Ed25519, conformance suite. |

The gap is exactly the user's ask: the **server** needs a README + richer, federation-ready seeding; the
**client** needs to become a real Blazor WASM **server explorer**; and the **compose** needs the UI plus a
self-contained, routable-address topology and a strengthened smoke path.

---

## 3. Deliverable A — Full sample server implementation + README

**Project:** `samples/SampleServer/` (extend in place; keep `CreateWebHostBuilder` the single
composition root so the existing `tests/SampleServer.Tests` + `tests/SampleBlazorClient.Tests` keep
working).

### 3.1 Composition root enhancements

1. **Enable inbound signature validation.** Add `app.UseSignatureValidation()` before `UseRouting()` in
   `ConfigureApp` so the instance is **federation-ready** (accepts and processes signed cross-instance
   Follow/Post/Reply/Like). This is what turns `iris-a`/`iris-b` from "public-read" instances into real
   peers. (Verified: the sample currently omits this, so signed inbound POSTs are not validated.)
2. **Register every seeded local actor's key** with the `IKeyProvider` (currently only `alice` is
   registered). Every actor the UI can log on as must have a registered signing key so the proxy and
   `DeliveryWorker` can sign as them.
3. **Per-instance distinct identity.** Drive seed identity from config so each instance is visibly
   different (see §3.2). Keep `Iris:HostName`/`Iris:Port`/`Iris:Actor` as the authoritative base-URI
   inputs; add an optional `Iris:InstanceName` and a seed-variant switch.

### 3.2 Richer per-instance seeded data

Seed a **richer, distinct** mock dataset per instance so exploring `iris-a` vs `iris-b` is meaningful and
cross-instance federation has content to move. (Replace the thin alice/bob/iris seed; keep those names so
existing tests' expectations still hold, and *add* to them.)

Per instance (names/theme vary by instance index so a and b are distinguishable):

- **Actors:** `alice` (primary, signable, the default logon), plus `bob`, `carol` (signable), and one
  **`manuallyApprovesFollowers`** actor (e.g. `dave`) to exercise the Decision #46 follow-approval path.
- **A community** (`iris`) with multiple members (alice, bob, carol) so the unified feed and members
  collection are populated.
- **Notes:** several public notes per actor (outbox content), **a reply thread** (note + `inReplyTo`
  replies + mention tags) to exercise the F-12 replies collection, and a couple of notes that are
  cross-referenced (announce/tag) so the community feed has depth.
- **Per-instance unique content** (different note text / a distinct extra actor on `b`) so a user can
  tell which instance they are looking at, and so instance→instance reads return visibly foreign content.

All seeding stays **synchronous and deterministic** (no randomness in IRIs), reusing the `Iri` conventions
(`{base}/ap/v1/u/{handle}`, `{base}/ap/v1/c/{name}`, `{actorIri}#key-1`) and the `KeyPairGenerator`
RSA-2048 default (with one Ed25519 actor to exercise F-05 key loading on the client).

### 3.3 `samples/SampleServer/README.md`

A README that **documents the implemented features** with **pointer information** (where each feature
lives in the library and which spec/decision it maps to). Structure:

- **What it is** — one paragraph: a runnable Iris ActivityPub server used as the sample's "home" instance.
- **Quick start** — `dotnet run` (local) and `docker compose up` (stack) commands; the logon credential
  (`alice` / `iris-sample`) and the base URIs.
- **Implemented features** — a table mapping each feature → endpoint(s) → library type(s) → pointer doc:

  | Feature | Endpoints | Library | Pointer |
  |---|---|---|---|
  | Actor document + owner-only private key | `GET /ap/v1/u/{handle}` | `ActorDocumentHandler` | [ARCHITECTURE](reference/ARCHITECTURE.md) |
  | WebFinger (RFC 8410) | `GET /.well-known/webfinger` | `WebFingerHandler` | [Decision 031](decisions/031-webfinger-root-path.md) |
  | NodeInfo (RFC 8555) | `GET /ap/v1/nodeinfo/2.0` | `NodeInfoHandler` | — |
  | Paged collections | `GET /ap/v1/u/{h}/{outbox,followers,following,liked,...}` | `CollectionEndpointHandler` | — |
  | Followed feed (home timeline) | `GET /ap/v1/u/{h}/feed` | `FeedService` | [Decision 053](decisions/053-followed-feed-pull-on-read.md) |
  | Community doc / members / feed / search | `GET /ap/v1/c/{name}[/members\|/feed\|/search]` | `Community*Handler` | [Decision 036](decisions/036-community-following-and-community-feed.md) |
  | Replies (threading) | `GET {object}/replies` | `ReplyStore` | [Change 054](changes/054-f12-replies-threading.md) |
  | Global search | `GET /ap/v1/search` | `GlobalSearchService` | [Change 056](changes/056-f13-global-search.md) |
  | Inbox federation (signed) | `POST /ap/v1/u/{h}/inbox` | `InboxProcessor` + handlers | [Decision 028](decisions/028-two-sided-follow-lifecycle.md) |
  | Object document + tombstone | `GET /ap/v1/{**path}` | `ObjectDocumentHandler` | [Decision 048](decisions/048-content-object-write-path.md) |
  | Proxy fallback | `POST /ap/v1/proxy/{target}` | `ProxyHandler` | [Decision 037](decisions/037-proxy-route-parameter-and-catch-all.md) |
  | Local moderation (mute/relay) | `POST /ap/v1/u/{h}/mutes\|relays/...` | `ModerationStore`/`RelayStore` | [Change 062/063](changes/) |
  | Capabilities discovery | `iris:capabilities` on community doc | `ActivityPubServerConstants` | [Decision 010](decisions/010-versioned-api-prefix.md) |

- **Configuration** — the `Iris:` section keys (`HostName`, `Port`, `Https`, `Actor`, `InstanceName`) and
  what each sets.
- **Seeded data** — the actor/community/note inventory (so a user knows what to expect to explore).
- **How it is tested** — pointer to `tests/SampleServer.Tests` and the compose smoke test.

---

## 4. Deliverable B — Blazor WASM "server explorer" client

**Project:** convert `samples/SampleBlazorClient/` from a console composition root into a **Blazor
WebAssembly** app. Keep the existing `SampleBlazorClient.CreateClientService` / `ClientService` (they are
the correct composition and are already exercised by `tests/SampleBlazorClient.Tests`); the WASM host
registers them in DI and renders them in components. The console `Program.cs` is removed (or kept as a
`dotnet run` smoke entry) — the WASM host becomes the client's home.

### 4.1 App shell

- Blazor WASM (net10.0), routed UI, minimal-but-clean styling (no heavy UI framework; plain CSS or
  a tiny dependency — **no new NuGet package without a ROADMAP note + justification**).
- **Composition root:** register a singleton `ExplorerSession` (wraps `ClientService`/`IrisClientBundle`)
  that holds the **currently logged-on instance + actor** and can re-login to a different instance
  (local or remote). A `HttpClient` (with an `HttpClientHandler`) is the WASM transport.

### 4.2 Core capability — log on to an instance by WebFinger address

This is the headline feature: a user can **switch between instances** by entering a **WebFinger address**
(`@alice@iris-a`, `alice@iris-b`, or `@user@external-host.tld`) plus the Basic-auth password.

Flow (all via the existing library):
1. Parse the address → `(handle, host)`. Build the candidate actor IRI `{scheme}://{host}/ap/v1/u/{handle}`
   and/or resolve via WebFinger (`bundle.ResolveActorAsync(address)`) to get the authoritative actor IRI.
2. **Log on:** `BasicAuthClientAuthenticator` → owner-only actor document → PEM private key → session
   (`IrisSession.LoginAsync`). On success the session holds the key and the client is signed as that actor.
3. Build the signed client (`bundle.CreateClient`) and route all subsequent explorer calls through it.
4. **Instance switching** = log out + log on to a new address; the UI keeps a list of recent instances so a
   user can flip between `iris-a` / `iris-b` / an external host.

> For **local instances** (`iris-a`/`iris-b`) the scheme is `http` and the host is the Docker service name —
> but the **browser** cannot reach `iris-a:8080` directly (that name is only routable *inside* the
> network). So the UI talks to the instances through the **host-published ports** (`localhost:8081`/
> `localhost:8082`) for logon/fetch, **or** through the **home instance's proxy** for remote targets. The
> explorer must let the user set the **base URL** (host-reachable) independent of the **advertised IRI host**
> (the service name) — see §4.4 "base URL vs IRI host".

### 4.3 Explorer screens (each is a live test of a library feature)

Every screen drives a real `IActivityPubClient` call against the logged-on (or a targeted) instance:

| Screen | Library call(s) exercised | Purpose |
|---|---|---|
| **Logon / instance switch** | `WebFinger` resolve + `IrisSession.LoginAsync` | The headline feature; proves auth + key loading per instance. |
| **Instance overview** | `GetActorAsync`, NodeInfo, `iris:capabilities` | Enumerate the instance's identity + advertised capabilities. |
| **Actors directory** | `SearchAsync(instanceBase, ...)` (global search lists local actors first) | Enumerate all local actors; click to open an actor. |
| **Actor detail** | `GetActorAsync`, `GetCollectionItemsAsync(outboxOf)`, `GetFollowFeedAsync`, `GetBlocksAsync`/`GetFlagsAsync`/`GetMutesAsync`/`GetRelaysAsync` | Explore one actor's outbox, home timeline, and moderation state. |
| **Object view** | `GetObjectAsync`, `GetRepliesAsync(objectIri)` | View a note + its reply thread (F-12). |
| **Community** | `GetCommunityFeedAsync`, members collection, community search | Explore the seeded community feed + members. |
| **Compose** | `PostNoteAsync`, `PostReplyAsync(actor, parent, ...)` | Post a note; reply to an object. Proves the write path + federation. |
| **Follow / unfollow** | `FollowAsync`, (undo via `UndoActivityHandler`) | Follow an actor on *another* instance → signed cross-instance delivery. |
| **Like** | (no `LikeAsync` — build a `Like` + `DeliverAsync(target.InboxOf(), like)`) | Exercises the like edge + liked collection. |
| **Moderation** | `MuteAsync`/`UnmuteAsync` (local Basic auth), `BlockAsync`/`FlagAsync` (federated) | Exercise local + federated moderation. |
| **Proxy fallback** | any call that 401/403s → `ProxyFallbackHandler` → home `/ap/v1/proxy/{target}` | Prove the WASM client can reach a target the browser can't, re-signed by the home server. |
| **Raw JSON inspector** | `SendAsync` (raw) | Show the raw signed request/response for debugging interop. |

The **raw JSON inspector** is intentional: it is the primary tool for **finding interop bugs** (inspect the
exact signed request, the `Signature` header, the content-type, and the raw response) against both local
and external instances.

### 4.4 Base URL vs IRI host (the Docker-only routability rule)

This is the subtle part of "self-contained using Docker-only routable addresses":

- The **IRI host** (what the server *advertises* — `http://iris-a:8080/...`) is what IRIs in documents
  carry and what federation uses **between containers** (Docker DNS resolves `iris-a`/`iris-b`).
- The **base URL** (what the *browser* dials) is the **host-published** address (`http://localhost:8081`)
  for local instances, because the browser is outside the Docker network.
- The explorer therefore configures the **transport base URL** (browser-reachable) separately from the
  **IRIs it requests** (which carry the advertised host). For local instances the UI dials
  `localhost:8081`/`8082`; the actor IRIs returned still say `iris-a:8080`, which is fine for display and
  for **instance→instance** federation (the containers resolve each other). For a target the browser can't
  reach, the **proxy fallback** (via the home instance) re-signs and relays — this is the
  **instance→external** path.
- A small config surface in the UI (and an `appsettings`/query-string default) maps each known instance to
  its browser base URL. External instances are entered by the user as a full base URL + WebFinger address.

> This separation is the key to keeping the sample **Docker-only routable** while still letting a browser
> explore it, and is the thing most likely to surface interop/config bugs — call it out in the README.

### 4.5 `samples/SampleBlazorClient/README.md`

Document the explorer: what it is, how to run it (`dotnet run` against a local server, or via the compose
UI), the logon-by-WebFinger flow, each screen and the library feature it exercises (pointer table mirroring
§4.3), the base-URL-vs-IRI-host rule (§4.4), and how to point it at an external instance for interop
testing.

---

## 5. Deliverable C — Docker composition (self-contained, one-command)

Extend `docker-compose.yml` to a **three-service** topology that boots with one command and is fully
routable on Docker addresses:

```
                ┌──────────────────────────────┐
   host:8081 ─► │ iris-a  (iris-a:8080)        │  SampleServer (signing, rich seed, variant A)
   host:8082 ─► │ iris-b  (iris-b:8080)        │  SampleServer (signing, rich seed, variant B)
                └───────────────┬──────────────┘
                                │ iris-net (bridge, Docker DNS)
                ┌───────────────┴──────────────┐
   host:8090 ─► │ iris-ui (aspnet static:8090) │  Blazor WASM server explorer (static)
                └──────────────────────────────┘
```

- **`iris-a` / `iris-b`:** as today, plus the federation-ready server (inbound signature validation on).
  Each gets a distinct seed variant (see §3.2) via an env switch.
- **`iris-ui`:** a new service that serves the **published Blazor WASM** client. Per the chosen host, this
  is a **minimal ASP.NET Core static-file host** publishing the WASM output on `8090`, host-mapped
  `8090:8090`, and also reachable as `iris-ui` on `iris-net` (so the smoke test can probe it from the
  network). The UI's default instance list points at the host-published local ports and exposes the
  base-URL-vs-IRI-host mapping (§4.4).
- **Routable-address rule:** in-container federation uses service names (`iris-a`/`iris-b`); host access
  uses the published ports (`8081`/`8082`/`8090`). The compose file documents both. No external FQDN, no
  host DNS entries, no TLS — **plain HTTP on routable addresses only**.

### 5.1 New / changed files

| File | Change |
|---|---|
| `samples/SampleBlazorClient/Dockerfile` | New: multi-stage — build the WASM client (SDK) → publish static → serve via an ASP.NET Core static host. Context = repo root. |
| `samples/SampleBlazorClient/wwwroot` (+ `.razor`/`.cs`) | The Blazor WASM app (new project files). |
| `docker-compose.yml` | Add `iris-ui` service; keep `iris-a`/`iris-b`; add the UI build; document routable addresses. |
| `samples/SampleServer/Dockerfile` | Possibly unchanged (same image reused for both instances). |
| `samples/SampleServer/README.md` | New (Deliverable A). |
| `samples/SampleBlazorClient/README.md` | New (Deliverable B). |
| `docs/reference/DEPLOYMENT.md` | Update the topology diagram + files table for the 3-service stack + UI. |

---

## 6. Deliverable D — Testing and the smoke path

The sample is the **real-world test platform**; testing has three layers.

### 6.1 In-process integration (existing projects, extended)

- `tests/SampleServer.Tests` — extend for the richer seed + **inbound signature validation** (assert a
  signed cross-instance Follow is accepted, an unsigned inbox POST is rejected 401).
- `tests/SampleBlazorClient.Tests` — extend to cover the **new client surfaces the UI uses** (logon by
  WebFinger address, follow across two in-process instances, post + reply, like, search) so the UI's
  behavior is covered even without a browser.
- These stay the **fast, always-on** gate (no Docker, no browser).

### 6.2 Docker smoke test (extended `scripts/docker-smoke-test.sh`)

Extend the opt-in smoke script (skips when Docker is unavailable, per the existing gate) to:

1. Boot the 3-service stack (`up --build`).
2. Wait for all three to be healthy.
3. **Health:** each server's WebFinger (as today) + the **UI** serves its index page (HTTP 200, contains
   the app root) over `iris-net`.
4. **Instance→instance federation (write path):** drive a **signed cross-container Follow** from `iris-a`
   to `iris-b` over the network (the in-process tests already prove this; here we prove it over real
   sockets) and assert the follow edge + an `Accept` landed on the remote. This is the key upgrade from
   "WebFinger reachability only" to "real signed federation over Docker".
5. **Proxy fallback over the network:** a proxy POST from one instance targeting the other returns 200.
6. Tear down (unless `IRIS_COMPOSE_KEEP=1`).

> The smoke test asserts at the **HTTP/network boundary** (it cannot click the browser). The browser
> behaviors are covered by 6.1 in-process tests + manual exploration (the README documents the manual
> exploration checklist).

### 6.3 Manual / live exploration checklist (documented in the READMEs)

A short, repeatable checklist a human (or a later automation) runs against the live stack:
log on to `iris-a` → enumerate actors → open a note → post a reply → follow `alice@iris-b` → see the
follow accepted → switch to `iris-b` and confirm the follower → post a note on `b` and see it in `a`'s
followed feed → point the UI at an **external dev instance** (FQDN) and repeat the read + follow paths.
This checklist is the standing **interop bug-hunting routine** for the project.

---

## 7. Dev FQDNs (local env only — NOT part of the project)

The user has **dev FQDNs** available for testing **instance→external-instance** compatibility. Per the
user's constraint, these are **local environment only** and must **not** be committed to the project:

- They are **not** in the compose file, **not** in any README, and **not** in any committed config.
- The sample's **UI base-URL config** is the injection point: a user supplies the external instance's base
  URL + WebFinger address **at runtime** (UI input or a local, git-ignored env file), and the explorer's
  proxy-fallback + WebFinger-discovery paths reach it.
- A **local, git-ignored** file (e.g. `samples/SampleBlazorClient/.env.local`, added to `.gitignore`) can
  hold the dev FQDNs for convenience; the committed defaults point only at the Docker service names /
  host-published ports. The README documents the *mechanism* (how to point the UI at an external
  instance) with a placeholder, never a real dev FQDN.

> Guard: a pre-commit/CI check (or at minimum the `.gitignore` + a code-review note) ensures no real
> external FQDN is committed. The project itself runs **self-contained on Docker-only routable
> addresses**.

---

## 8. Work breakdown (slices, each vertically complete: impl + tests)

Each slice is one autonomous-loop turn (see [AUTONOMOUS_LOOP.md](reference/AUTONOMOUS_LOOP.md)); each
lands a change doc in [changes/](changes/README.md). Ordered so the stack stays green and deployable
throughout.

- [x] **S1 — Sample server: federation-ready + rich seed** (done, [change 070](changes/070-sample-federation-ready.md)). Added `UseSignatureValidation()`, registered all
  seeded actors' keys, expanded the per-instance seed (three actors incl. an Ed25519 remote-host
  stand-in, a community, follows, notes/reply/like), kept `CreateWebHostBuilder` the
  composition root. Extended `tests/SampleServer.Tests` with 8 federation facts (signed inbox accept
  RSA + Ed25519, unsigned 401, per-actor auth, remote-host boundary, seed edges).
- [x] **S2 — Sample server README** (done). `samples/SampleServer/README.md` (Deliverable A) — what-it-is,
  quick start (local `dotnet run` + Docker compose + smoke script), logon credential + base URIs,
  feature → endpoint → library → pointer table, `Iris:` config table, seeded-data inventory, and test
  pointers (in-process suite + compose smoke).
- [x] **S3 — Blazor WASM scaffold + composition root** (done, [change 072](changes/072-sample-blazor-wasm-explorer.md)).
   `SampleBlazorClient` is a routed Blazor WASM host (Deliverable B): `ExplorerSession` (wraps the
   existing `ClientService`/`CreateClientService` bundle) + `AddIrisExplorer` DI, app shell + routing,
   `WebFingerAddress` parser, and a tagged console smoke entry (`-p:ConsoleSmoke=true`) so both `dotnet
   run` (WASM) and the pipeline smoke build from one project. 17 in-process `ExplorerTests` (parser,
   session log-on/switch/recents, DI); the Phase 7 pipeline facts stay green. (The session's log-on-by-
   address + recents — the S4 core minus WebFinger *resolve* and the UI — are included here; S4 adds the
   resolve step + screens.)
 - [x] **S4 — Logon by WebFinger address + instance switching** (done, [change 073](changes/073-webfinger-resolve-instance-switching.md)).
    The headline feature (§4.2): address parse → **WebFinger resolve** (scheme-aware `dialScheme`, default
    `https` / `http` for local instances) → `LoginAsync` → signed client; recent-instances list;
    log out/switch. The session resolves the address to the authoritative actor IRI over the injected
    transport (falling back to the direct IRI when unreachable) and authenticates as that IRI (whose host
    may differ from the dial base for local instances). In-process tests: WebFinger resolve, direct-IRI
    fallback, instance switch, scheme-aware `WebFingerClient`; logon to two in-process instances by
    address, key loaded, client signs.
- [x] **S5 — Base URL vs IRI host separation** (done, [change 074](changes/074-base-url-vs-iri-host-config.md)).
  The transport-base vs advertised-IRI split (§4.4) + the **instance base-URL config surface**
  (`InstanceBaseUrls`: advertised host → browser base URL, case-insensitive, pre-fills the dial base so a
  user only enters address + password). The session carries the map (`BaseUrls`) and `AddIrisExplorer`
  wires it; `Home.razor` resolves the address's host against it before logon. The canonical fact is pinned
  by `LogOn_DialsOneBaseUrl_RequestsIrisCarryingAnotherHost`: the client dials one base URL (localhost)
  while the actor IRIs it requests carry another host (iris-a). 4 in-process tests (map read/overwrite,
  two-host logon + signed feed read, DI wiring).
- **S6 — Explorer screens: read paths.** Instance overview, actors directory (search), actor detail
  (outbox/feed/moderation), object view (+replies), community (feed/members/search). Each screen's call
  covered by an in-process test.
- **S7 — Explorer screens: write paths.** Compose (post/reply), follow/unfollow (cross-instance), like,
  moderation (mute local / block+flag federated). In-process two-instance tests for the federated writes.
- **S8 — Raw JSON inspector + proxy-fallback screen.** Raw signed request/response view; a screen that
  forces the proxy path. Tests: proxy 401→relay over in-process two instances.
- **S9 — WASM Dockerfile + `iris-ui` compose service.** Multi-stage build → static host; add `iris-ui` to
  compose; routable-address documentation. (No automated browser test here; covered by S6/S7 in-process.)
- **S10 — Smoke test: UI + signed federation over Docker.** Extend `docker-smoke-test.sh` (UI index 200
  over `iris-net`; signed cross-container Follow a→b + Accept; proxy 200). Opt-in gate preserved.
- **S11 — Sample Blazor README + DEPLOYMENT.md update.** `samples/SampleBlazorClient/README.md`
  (Deliverable B) + update `docs/reference/DEPLOYMENT.md` topology/files; document the manual
  interop checklist + the external-instance (dev FQDN) mechanism (no real FQDN committed).

> Slices S1–S2 make the **server** a real, documented federation peer. S3–S8 build the **explorer**.
> S9–S10 wire the **stack** + **smoke path**. S11 finishes the **docs**. The stack is deployable and
> green after S9; the full "boot + explore + interop" story is complete after S11.

---

## 9. Risks and mitigations

| Risk | Mitigation |
|---|---|
| **Browser can't reach `iris-a:8080`** (name only routable in-network) | §4.4 base-URL-vs-IRI-host split; UI dials host-published ports; proxy fallback for unreachable targets. This is the #1 thing to get right — it is where interop/config bugs will surface. |
| **In-browser signing / key handling** (WASM) | The `Iris.Client.Extensions` bundle already does Basic-auth → PEM key → sign in pure .NET (no native deps). RSA/ECDSA/Ed25519 all BCL/BouncyCastle — WASM-safe. Verify in S4/S6. |
| **CORS** (browser → `localhost:8081/8082` from `localhost:8090`) | The sample servers must send permissive CORS for the UI origin (or same-origin via the UI host reverse-proxying). Decide in S5; document in READMEs. |
| **In-memory state resets on restart** | Expected (non-goal). README states it; smoke test re-seeds by rebuilding. |
| **Dev FQDNs leak into the repo** | §7: local-only, git-ignored, mechanism documented with placeholders; CI/review guard. |
| **WASM build adds a heavy Docker stage** | Multi-stage: SDK build → publish static → small static host; reuse the cached SDK layer. |
| **Sample seed divergence breaks existing tests** | Keep `alice`/`bob`/`iris` names + the two seeded notes; only *add*. Update tests' count assertions to `>=` (they already use `>=`). |

---

## 10. Acceptance criteria (definition of done for the whole Phase 8 enhancement)

- [ ] `docker compose up --build` boots **three** healthy services (`iris-a`, `iris-b`, `iris-ui`) on
      routable addresses with no host-side .NET, no FQDN, no DNS config, no TLS.
- [ ] Navigating to the **UI** (host:8090) lets a user **log on by WebFinger address** to `iris-a` and
      `iris-b` (and switch between them), and **enumerate + explore** the seeded mock data (actors,
      objects, replies, community feed, search).
- [ ] The UI can **post, reply, follow (cross-instance), like, and moderate**, and these are **accepted**
      by the other instance over the Docker network (signed federation, not just reads).
- [ ] The UI can be pointed at an **external instance** (runtime-supplied base URL + WebFinger address)
      and exercise the read + follow + proxy-fallback paths against it.
- [ ] `samples/SampleServer/README.md` documents the implemented features with pointer information.
- [ ] `samples/SampleBlazorClient/README.md` documents the explorer + the external-instance mechanism
      (no real dev FQDN committed).
- [ ] `scripts/docker-smoke-test.sh` (opt-in) boots the 3-service stack and asserts UI reachability +
      signed cross-container federation + proxy fallback; skips cleanly without Docker.
- [ ] In-process `SampleServer.Tests` + `SampleBlazorClient.Tests` are green and cover the new server
      (signature validation, richer seed) and client (logon-by-address, cross-instance writes) surfaces.
- [ ] Full solution `dotnet build` (0 warnings, `TreatWarningsAsErrors`) + `dotnet test` green.
- [ ] No new NuGet package without a ROADMAP note + justification; no real dev FQDN in the repo.

---

## 11. Pointers

- Library client surface: [PROJECTS.md](reference/PROJECTS.md) (`Iris.Client`, `Iris.Client.Extensions`).
- Library server surface + endpoints: [PROJECTS.md](reference/PROJECTS.md) (`Iris.Server`,
  `Iris.Server.InMemory`), [ARCHITECTURE.md](reference/ARCHITECTURE.md).
- Deployment / topology: [DEPLOYMENT.md](reference/DEPLOYMENT.md),
  [DEPLOYMENT_PREP.md](reference/DEPLOYMENT_PREP.md),
  [Decision 039](decisions/039-sample-docker-topology.md).
- Interop / compatibility framing: [COMPATIBILITY_MATRIX.md](reference/COMPATIBILITY_MATRIX.md),
  [INTEROP_TEST_HARNESS.md](reference/INTEROP_TEST_HARNESS.md),
  [ENUMERATION_DESIGN.md](reference/ENUMERATION_DESIGN.md).
- Coding rules (binding): [CODING_STYLE.md](reference/CODING_STYLE.md).
- Doc-lean rules: [AUTONOMOUS_LOOP.md](reference/AUTONOMOUS_LOOP.md#keeping-the-docs-lean).
