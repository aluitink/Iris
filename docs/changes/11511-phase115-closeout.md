# Phase 115 — Polish & Distribution: Closeout

**Date:** 2026-09-13
**Type:** Phase closeout
**Scope:** Full Phase 115 summary + Phase 116 definition

## Phase 115 Summary

Phase 115 focused on **polish + distribution** — completing the D-column (polish pass) of the production-app feature matrix through live verification and reconciliation.

### Slices Completed (115.1–115.10)

| Slice | Cluster | Rows Reconciled | Code Changes |
|---|---|---|---|
| 115.1 | Boost/Like Engagement | 4 | None |
| 115.2 | Notifications | 3 | None (1 left ☐ — headless WASM env limit) |
| 115.3 | Communities | 7 | None |
| 115.4 | Moderation (per-user) | 4 | None |
| 115.5 | Search & Directory | 2 | **Yes** — EF search bug fix (`FromSqlRaw` interpolation) + regression test |
| 115.6 | Settings | 4 | None |
| 115.7 | Instance Admin | 3 | None |
| 115.8 | Auth + Profile | 8 | None (2 left ☐ — server-side) |
| 115.9 | Compose + Timeline | 12 | None |
| 115.10 | Follow Graph + Cross-Cutting | 10 | None |
| **Total** | | **57** | **1 bug fix** |

### Final Matrix State

- **A-column (functionality):** 100% ✅ (all features implemented)
- **C-column (experience):** 100% ✅ (all features have experience pass)
- **D-column (polish):** 66 ✅ / 3 ☐ = **95.6% reconciled**
  - 3 remaining ☐ (all non-code):
    1. **Unread badge/count** — data path verified; badge live-render blocked in headless WASM automation (environment limitation, not a code defect)
    2. **Login rate limiting** — server-side; not UI-exercisable via Playwright
    3. **Admin bootstrap from `.env`** — server-side; not a web UI feature

### Key Finding

One genuine code bug was found and fixed during live verification (115.5): the EF/Postgres `EfActorStore` search built `FromSqlRaw` SQL with a `$$"""` raw-interpolated string, causing a `FormatException` on every non-empty query. Fixed with verbatim literal queries + a real-Postgres regression test.

### Test Counts (Final)

- `Iris.Core.Tests`: 396/0
- `Iris.Client.Tests`: 171/0
- `Iris.Server.Data.Tests`: 11/0
- `Iris.Web.Tests`: 95/95
- `Iris.Server.Tests`: 1105/0 (17 skipped)
- `SampleServer.Tests`: 38/0
- **Total: 1,816 tests, 0 failures**

## Phase 116 — Next Phase Definition

**Theme:** Performance, scale, and new feature verticals.

Phase 115 completed the polish pass on the existing feature set. Phase 116 shifts to:

1. **Performance & latency polish** — measure and optimize critical paths (feed load, search, profile fetch). Target: sub-200ms TTFB on P95 for core endpoints.
2. **Federation stress testing** — multi-instance load testing (10+ concurrent instances, 1k+ follow edges, 10k+ objects). Verify delivery queue backpressure, cache hit rates, and garbage collection behavior.
3. **New feature verticals** (pick 1–2):
   - **Direct messages (DMs)** — private 1:1 messaging between actors (ActivityStreams `Note` with `To`/`Bto` to the recipient's inbox; no `Public` audience).
   - **Hashtag feeds** — `#tag` extraction on post, hashtag index, `/tag/{name}` feed page.
   - **Event scheduling** — `Event` object type with `startDate`/`endDate`, calendar view, RSVP (Accept/Reject).

### Phase 116 Seed Slices

- **116.1** — Performance baseline: measure TTFB + throughput for `/home`, `/actor?iri=…`, `/ap/v1/search?q=…` under load (k6 or wrk). Document baseline.
- **116.2** — Cache hit-rate audit: measure Redis/in-memory cache hit rates for actor docs, collections, search. Identify misses.
- **116.3** — Federation stress test: 10-instance multi-node test with 1k follow edges + 10k objects. Verify delivery queue, cache, GC.
- **116.4** — (candidate) DM vertical: server endpoint + client UI + integration test.
- **116.5** — (candidate) Hashtag feed vertical: `#tag` extraction + `/tag/{name}` feed + integration test.
