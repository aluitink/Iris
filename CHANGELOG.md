# Changelog

All notable changes to the Iris ActivityPub platform.

## v1.0.0 (2026-09-08)

First production release. A complete ActivityPub social platform built on .NET 10,
comprising reusable libraries (Iris.Core, Iris.Client, Iris.Server, Iris.Server.Data,
Iris.Server.InMemory) and a production web app (Iris.Web) with a Blazor WebAssembly
frontend.

### Platform Foundation (Phases -1–18)

- Project reorganization into a clean solution layout with central package management.
- Core identity (`Iri`, `IActor`), HTTP signature signing/validation (RFC 9421),
  and ActivityStreams integration via `KristofferStrube.ActivityStreams`.
- ActivityPub server: inbox processing, outbox publishing, delivery worker with
  bounded concurrency, file-backed queueing, dead-letter handling, retry with backoff.
- Communities (Group actors): create, join, leave, feed, members, moderation.
- Proxy fallback: cross-instance read relay for objects on remote instances.
- Operational readiness: rate limiting, opt-in persistence, OAuth2 (token/revoke/authorize),
  bearer token validation, health checks, graceful shutdown, delivery metrics, transport
  hardening (bounded body size, request timeout).

### Federation Maturity (Phases 19–28)

- Full ActivityPub write/read flows: follow, block, flag, mute, reply, like, boost,
  delete, community lifecycle — verified across local and cross-instance topologies.
- Outbox as the single source of truth for published activities.
- Cross-instance propagation: `Accept`, `Reject`, `Undo(Follow)`, `Undo(Like)`,
  `Undo(Announce)`, `Tombstone`, `Move` (key rotation), `Update` (object edit),
  `Mute` (Iris extension type), moderation undo, community un-follow.
- Idempotent handling of duplicate inbound deliveries.
- Relay fan-out (ActivityPub §5.1.3) on `Create`/`Announce`/`Update`/`Delete`.
- WebFinger RFC 7033 host handling + client base-URI retry.
- Mastodon wire compatibility (`Digest` header casing).
- Extension API-surface conformance: `iris:`-namespaced JSON-LD context, all wire
  terms centralized in `Iris.Core`.

### Production App (Phases 30–33)

- **Iris.Web**: ASP.NET Core Blazor Web App hosting the ActivityPub server.
- **Persistence**: EF Core + PostgreSQL provider (hybrid schema: relational columns
  for queryable shape + `jsonb` document column for the full AS document).
- **Local auth**: registration, login, logout (cookie auth, password hashing via
  ASP.NET Core Identity, rate limiting, admin bootstrap).
- **Signing identity persistence**: keys survive restarts (durable `IKeyStore` +
  `DelegatingKeyProvider`).
- **Security**: inbound request-body size cap (1 MiB default), CORS same-origin
  default with opt-in allow-list, structured logging, graceful shutdown drain.
- **Deployment**: multi-stage Dockerfile, docker-compose (Postgres + app),
  `.env`-driven configuration, production smoke test script.

### Product UI (Phases 32.4–44)

- **Blazor WebAssembly** client (Iris.Web.Client): all UI runs in the browser.
- Home timeline (followed feed), compose (Note/Article, character count, CW/sensitive,
  media attachment, edit own post, reply with context preview), profile (header,
  posts/replies/likes tabs, edit form, engagement), object detail (full thread,
  replies, like/boost counts, delete, moderation actions), search (actors + content),
  directory (local actors), communities (directory, join/leave, create, edit, member
  management, feed filtered to members, membership requests), notifications (read-state,
  unread badge, 60s poll), settings (account, password, communities), admin (user list),
  moderation (block/mute/flag on posts and actors, follow-request queue, community
  join-request queue), landing page (hero, sign-in/out states).
- Accessibility: ARIA labels, `aria-pressed`, keyboard navigation, `aria-hidden`.
- Visual design: responsive layout (1280×800 + 375×812), card system, engagement bar,
  empty/loading states, relative timestamps, circular avatars, favicon, meta tags.

### WASM Stabilization & Design (Phases 45–47)

- 14 defects found and fixed across 7 manual Playwright test slices (auth/session,
  content round-trips, media/CW, social graph, notifications/moderation, edge states).
- Visual inspection: 14 design findings across 12 pages, all addressed.
- Post-design polish: boost card fix, user-friendly error copy, accessibility pass,
  static-file Cache-Control, timeline page size increase, CSS variable cleanup.

### Production Operations (Phases 48–50)

- **Reverse proxy**: nginx + Caddy configs (TLS, WebSocket upgrade, security headers,
  gzip, rate limiting 10r/s burst 20).
- **Backup & restore**: `pg_dump` + Data Protection keys + media volume scripts,
  retention pruning, selective restore (`--db-only`, `--media-only`, `--keys-only`).
- **Monitoring**: `/local/v1/metrics` Prometheus endpoint (delivery queue metrics),
  monitor script (health + metrics polling, Slack/email alerts), Prometheus scrape
  config, alert rules, Grafana panel definitions.
- **Federation verification**: WebFinger (local + remote), NodeInfo, actor documents,
  health, metrics — all verified live against the Docker stack.
- **Load testing**: asyncio-based load test script; ~62–65 rps at 0% errors across
  10–100 concurrency; no app bottlenecks; scaling path = more containers.
- **API documentation**: OpenAPI 3.1 spec (`/openapi/v1.json`) covering 44 endpoints;
  Swagger UI at `/api/`.
- **Security hardening**: non-root Docker user (uid 1001), explicit cookie security
  flags (`HttpOnly`, `SameSite=Lax`, `SecurePolicy=SameAsRequest`), parameterized
  design-time connection string. 0 vulnerable NuGet packages.

### Test Suite

- **1,210 tests**, 0 failures, 17 skipped.
- `Iris.Client.Tests`: 154 (client library unit + integration).
- `Iris.Server.Tests`: 951 (federation protocol, delivery, inbox, outbox, communities,
  moderation, proxy fallback, OAuth2, health, metrics, graceful shutdown, CORS,
  request-body limit, structured logging, signing identity persistence).
- `Iris.Server.Data.Tests`: 10 (EF Core persistence contract + durability).
- `Iris.Web.Tests`: 63 (auth, endpoints, product screens, antiforgery, notifications,
  admin, metrics).
- `SampleServer.Tests`: 32 (sample federation harness).
