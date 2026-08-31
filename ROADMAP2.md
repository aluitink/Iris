# Iris — Roadmap

Working roadmap: where we've been, where we are, what's left. Per-slice detail lives in [changes/](changes/README.md), [decisions/](decisions/README.md), and [phase-notes/](phase-notes/README.md) — this file stays one line per waypoint.

## Where we've been

| Phase | Status | One line |
|---|---|---|
| -1 — Project Reorganization | ✅ | Domain folders + nested namespaces, mirrored across `src` and `tests`. |
| 0 — Scaffolding | ✅ | Solution, net10.0 projects, central package management, multi-instance `TestServer` harness. |
| 1 — Core | ✅ | `Iri`, identity/keys, HTTP signatures, caching. |
| 2 — Client | ✅ | Signing pipeline, WebFinger, paged collections, retry + content-negotiation handlers. |
| 3 — Server Foundation | ✅ | Persistence seam + in-memory impl, `/ap/v1` endpoints, server caching. |
| 4 — Inbox & Delivery | ✅ | Inbound validation, activity handlers, delivery queue/worker, Follow lifecycle. |
| 5 — Community / Groups | ✅ | Community store + endpoints, unified feed, `iris:capabilities`. |
| 6 — Proxy Fallback | ✅ | Server proxy endpoint + policy stack, client `ProxyFallbackHandler`. |
| 7 — Samples & Blazor | ✅ | `Iris.Client.Extensions`, `SampleServer`, `SampleBlazorClient`, E2E tests. |
| 8 — Sample Docker Composition | ✅ | Multi-instance compose + WASM server-explorer (S1–S11, [DEPLOYMENT](reference/DEPLOYMENT.md)). |
| 9 — Deployment Preparation | ✅ | FQDN/TLS plan, enumeration + compat + risk registers (prep only). |
| 10 — Project & Test Review | ✅ | Suite audit + consolidation (850 → 832 tests). |
| 11 — Usability / Gap Closure | ✅ | User journeys, discovery/follow/post, failure-mode + operator-reject coverage. |
| 12 — Spec Conformance | ✅ | F-01…F-31 gap closure (F-26/F-31 deferred) + conformance suite. |
| 13 — Live Federation Compatibility | 🚧 | CI-testable slices done (13.1–13.4, 13.8; closed the F-26/F-28 deferrals via opaque passthrough); live interop 13.5–13.7, 13.9–13.10 **blocked on Phase 9 FQDN + real partner instances**. |
| 14 — Live-Interop Execution & Remediation | 🚧 | Run the live-interop suite (incl. the deferred Mastodon live test, [TESTING](reference/TESTING.md)) + remediate findings; F-27 (custom-emoji) + F-31 (ld+json production) ride along if live tests surface them. Blocked on the same external prerequisites (see 13). |
| 15 — Auth Upgrade | ✅ | Bearer validator, full OAuth2 (token, refresh, authorize + WASM browser flow), samples/docs. |
| 16 — Persistence & Scaling | ✅ | Bounded delivery concurrency, file-backed queue + dead-letter, per-peer rate limiting, file-backed persistence (all opt-in). |
| 17 — Observability & Transport Hardening | ✅ | Health checks + graceful shutdown, delivery metrics, circuit breaker + retry hardening, inbound rate limiting. |
| 18 — Client/Server Hardening | ✅ | 18.1–18.3 done (`Retry-After` HTTP-date client+server, e2e 429→retry); no further slices defined. |
| Sample Explorer — 2nd & 3rd rounds | ✅ | Library-coverage gaps closed (relays, home timeline, deep-linking, unlike, delete, …); live-browser acceptance verified (change [123](changes/123-sample-explorer-live-browser-acceptance.md)). |

## Remaining work

- [ ] **Phase 13.5–13.7, 13.9–13.10 (live interop)** — stand up real partner instances (Mastodon, Lemmy, Threads) and verify interop. Blocked on Phase 9 public FQDN/TLS + external instances; the CI-gating model is in place (change 101).
- [ ] **Phase 14** — remediate whatever the live-interop findings surface.
- [ ] **Sample polish (non-blocking)** — media/attachment upload in the explorer compose screen (deferred from the 2nd-round plan; all other screens are live-verified).

## Notes

- CI gate: `dotnet build` clean (warnings = errors) + `dotnet test` green, 1180 tests across 8 projects.
- No new NuGet packages without a note here + justification (see [CODING_STYLE](reference/CODING_STYLE.md)).
- Doc placement rules: [reference/AUTONOMOUS_LOOP.md — Keeping the docs lean](reference/AUTONOMOUS_LOOP.md#keeping-the-docs-lean).
