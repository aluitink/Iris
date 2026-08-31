# Iris — Roadmap

This is the short working roadmap. Detailed phase notes, change docs, and design decisions live in [changes/](changes/README.md), [decisions/](decisions/README.md), and [phase-notes/](phase-notes/README.md).

## Status at a glance

| Phase | Status |
|---|---|
| -1 — Project Reorganization | ✅ complete |
| 0 — Scaffolding | ✅ complete |
| 1 — Core | ✅ complete |
| 2 — Client | ✅ complete |
| 3 — Server Foundation | ✅ complete |
| 4 — Inbox & Delivery | ✅ complete |
| 5 — Community / Group Support | ✅ complete |
| 6 — Proxy Fallback | ✅ complete |
| 7 — Samples & Blazor | ✅ complete |
| 8 — Sample Docker Composition | ✅ complete |
| 9 — Deployment Preparation | ✅ complete |
| 10 — Test Review | ✅ complete |
| 11 — Usability / Gap Closure | ✅ complete |
| 12 — Spec Conformance | ✅ complete |
| 13 — Live Federation Compatibility | 🚧 in progress |
| 14+ — Future Work | 📋 planned |
| Sample Explorer — 2nd round | ✅ complete |
| Sample Explorer — 3rd round (S9–S10) | ✅ complete |

## Completed work

Phases -1 through 12 are complete, including the core federation layer, client/server interop features, community support, deployment prep, and conformance work.

## Remaining work

- [x] **Sample Explorer — second-round enhancement** — closed the library-coverage gap in the Blazor WASM explorer + fixed the broken compose write path. S1–S8 done (changes 114–122); all §6 acceptance criteria verified live (change [123](changes/123-sample-explorer-live-browser-acceptance.md)). Full plan: [SAMPLE_EXPLORER_PLAN.md](SAMPLE_EXPLORER_PLAN.md).
- [x] **Sample Explorer — 3rd round (S9+)** — closed the §3.1 client-method gaps that have a clean UI home. **S9 (change [124](changes/124-s9-typed-actor-fetch.md))**: `ActorDetail` uses the **typed** `GetActorAsync` + surfaces its null contract. **S10 (change [125](changes/125-s10-raw-delivery-screen.md))**: new `/deliver` page drives the raw `DeliverAsync` escape hatch directly (build a `Follow`, show its signed JSON, POST to the target's inbox; 3 tests) — **live-browser-verified (change [126](changes/126-s10-raw-delivery-live-browser-acceptance.md))**: 202 + the follow edge recorded on the happy path, 404 on an unknown inbox. **S11 (change [127](changes/127-s11-search-of-derivation.md))**: the §3.2 `IriExtensions` audit closed — `SearchAsync` now derives its endpoint via the canonical `SearchOf` (single source of truth) and the Actors page surfaces it. **S12 (change [128](changes/128-s12-unlike-undo-like.md))**: unlike (`Undo(Like)`) — client `UnlikeAsync`, server removes the like edge on the Undo (local outbox + remote inbound paths), and the Object page's Like button now toggles to Unlike. Closes the §3.3 "unlike" library-surface decision (§3.1 is now fully closed in the UI; `GetFollowFeedAsync` stays client-tested — the typed method can't carry the `next`-link the paginated Feed page needs).
- [x] **Priority: troubleshoot & fix the docker-compose sample UI** (`iris-ui` / `SampleBlazorClient` Blazor WASM server-explorer) — verified end-to-end against the compose stack with Playwright (2026-08-30): logon, actor directory/detail, note view, compose/post, follow/unfollow, community, like, and instance switching all work; `IrisStaticHost` SPA fallback fixed. (Note: the compose *post* was confirmed to return 200 via the proxy fallback but the note was not actually created — that is now the S1 item above.)
- [ ] Phase 13.5–13.10: stand up real partner instances and verify interop with Mastodon, Lemmy, and Threads.
- [ ] Phase 14: live-interop remediation and gap fixes.
- [x] **Phase 15.2 (remaining): OAuth2 `/oauth2/authorize` + Blazor WASM integration** (2026-08-30) — the server `GET /ap/v1/oauth2/authorize` browser-redirect endpoint (auto-approve + one-time code + 302) and the `SampleBlazorClient` browser flow (`OAuth2BrowserFlow` + `LogOnWithOAuth2Async` + `Home.razor` OAuth2 logon + `IrisStaticHost` `/callback`); Phase 15 (auth upgrade) now fully done (15.1, 15.2a, 15.2b, 15.2, 15.3, 15.4).
- [x] **Phase 16: production persistence and scaling** (2026-08-30) — fully done. **16.1**: `DeliveryWorker` bounded-concurrency pump — `DeliveryWorkerOptions.MaxConcurrentDeliveries` (default 1 = serial) lets a host deliver a burst in parallel without exhausting the connection pool; no deadlock on drain (change 109). **16.2**: persistent file-backed delivery queue + dead-letter store — `FileBackedDeliveryQueue` / `FileBackedDeliveryDeadLetterStore` journal pending (and dead-lettered) deliveries to disk and replay them on restart (at-least-once; deduped by `Id`, C-07); opt-in via `UseFileBackedDelivery` (default stays in-memory) (change 110). **16.3**: per-peer outbound-delivery rate limiting — `SlidingWindowDeliveryRateLimiter` (keyed by inbox host) gates each delivery to `DeliveryRateLimitOptions.PerPeerMaxRequestsPerMinute` per sliding minute; disabled (0) by default, opt-in via rebind (change 111). **16.4**: persistent file-backed `IPersistenceProvider` — `FileBackedPersistenceProvider` (+ nine `FileBacked*Store` and a synchronous `FileBackedKeyStore`) over one JSON file per store so actors/objects/activities/communities/edges/keys survive a restart (atomic writes; corrupt/missing file degrades to empty); opt-in via `UseFileBackedPersistence(directory)`, default stays in-memory (change 112).
- [ ] Phase 17: observability and transport hardening. **17.1 done** (2026-08-30): health-check endpoints + graceful shutdown — two `IHealthChecks` (`InstanceHealthCheck` liveness, `DeliveryQueueHealthCheck` delivery backlog) on a public `GET /ap/v1/health` (200 healthy/degraded, 503 unhealthy) + a `DeliveryQueueShutdownService` that completes the delivery queue on host stop so the worker drains; no new NuGet (HealthChecks + `IHostedService` ship in the ASP.NET shared framework) (change 113). Remaining: **17.2** structured logging + OpenTelemetry metrics, **17.3** circuit breaker + retry hardening, **17.4** inbound rate limiting.

## Notes

- The immediate live-interop work is blocked on the Phase 9 public FQDN/TLS setup and external partner instances.
- The CI-testable Phase 13 sub-slices (13.1–13.4, 13.8) are already complete and captured in the change docs.
- Detailed rationale remains in [phase-notes/README.md](phase-notes/README.md), not in this file.
