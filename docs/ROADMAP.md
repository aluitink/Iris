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

## Completed work

Phases -1 through 12 are complete, including the core federation layer, client/server interop features, community support, deployment prep, and conformance work.

## Remaining work

- [x] **Priority: troubleshoot & fix the docker-compose sample UI** (`iris-ui` / `SampleBlazorClient` Blazor WASM server-explorer) — verified end-to-end against the compose stack with Playwright (2026-08-30): logon, actor directory/detail, note view, compose/post, follow/unfollow, community, like, and instance switching all work; `IrisStaticHost` SPA fallback fixed.
- [ ] Phase 13.5–13.10: stand up real partner instances and verify interop with Mastodon, Lemmy, and Threads.
- [ ] Phase 14: live-interop remediation and gap fixes.
- [ ] Phase 15.2 (remaining): OAuth2 `/oauth2/authorize` browser-redirect endpoint + Blazor WASM integration (15.1, 15.2a, 15.2b, 15.3, 15.4 done).
- [ ] Phase 16: production persistence and scaling.
- [ ] Phase 17: observability and transport hardening.

## Notes

- The immediate live-interop work is blocked on the Phase 9 public FQDN/TLS setup and external partner instances.
- The CI-testable Phase 13 sub-slices (13.1–13.4, 13.8) are already complete and captured in the change docs.
- Detailed rationale remains in [phase-notes/README.md](phase-notes/README.md), not in this file.
