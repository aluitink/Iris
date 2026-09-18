# 139.3 Scenario 6 — Offline rebuild

**Phase:** 139.3 — Data lifecycle & persistence review
**Scenario:** 6 (of 11) — "Offline rebuild"
**Date:** 2026-09-18
**Result:** **PARTIAL PASS + 2 FINDINGS.** The core offline-rebuild bar holds: with a peer's
container stopped, previously-synced **local** content and **cached remote** content render fully
from local storage (public timeline, person feed, and local note object page all return 200 offline,
no dead fetch blocking them). Two gaps were found and logged as follow-up work items:
(1) a **community feed is blocked ~60–70s** by a dead fetch to an *unreachable* followed peer, and
(2) a **foreign-IRI remote object page 404s** via the object catch-all (the endpoint only serves
objects by their *local* IRI).

## What was tested

Pass criterion: "With a peer's container stopped, confirm previously-synced threads (post + replies
+ counts) still render fully from local storage (Phase 138.21's bar, generalized to all peer types)."
Evidence required: "screenshot with peer stopped."

Method: `docker stop lemmy-1` (the live Lemmy peer), then exercised the read paths against the running
Docker app (the app keeps its Postgres + local object store; the stopped peer only affects *live*
outbound fetches). Read paths checked: the public timeline, the `andrew` person feed, a local note's
object page, and a community feed. `docker start lemmy-1` restored the peer afterward.

## What passed (the core bar)

| Read path | Peer state | Result | Time |
|---|---|---|---|
| Public timeline (`/`) | Lemmy stopped | 20+ posts render (remote + local) | fast |
| `andrew` person feed (`/ap/v1/u/andrew/feed`) | Lemmy stopped | 200, 20 items (5 local + 15 cached-remote), `totalItems: 197` | **4.2s** |
| Local note object page (`/ap/v1/u/andrew/notes/{id}`) | Lemmy stopped | 200 (rendered from the local store) | **2.7ms** |
| Local note via `/object?iri=` UI | Lemmy stopped | renders | fast |

- **Local content renders offline** (andrew's 41 local notes; the 139.3-s2 verification note renders
  from the local store in 2.7ms). This is Phase 138.21's store-level bar, generalized: the local
  object store is self-sufficient when the peer is down.
- **Cached remote content renders offline.** The public timeline and the person feed return the
  previously-synced remote posts (mastodon.social, hachyderm.io, beige.party, aus.social, …) — the
  147.2 client-side collection-page/actor caches + the local object store serve them without a live
  fetch. The person feed completed in **4.2s** (the downed Lemmy follow failed fast through the
  proxy's 500, not a full timeout) and still returned 20 merged items.
- **No dead fetch blocked** the public timeline or the person feed — the 5s `HttpClientTimeout` on
  the outbound feed client (`ActivityPubServerExtensions.cs:491` community / `:546` person) caps each
  remote outbox fetch, so a downed *reachable-but-erroring* peer (Lemmy → proxy 500) degrades
  gracefully instead of hanging.

## Finding 1 — a community feed is blocked ~60–70s by an *unreachable* followed peer

The `c/owner-test-5428` community follows **two** remote communities: `lemmy.luit.ink/c/interop`
(reachable) and `iris-dev2.luit.ink/c/test` (**unreachable** — a downed second-Iris dev instance on
the same IP; the TCP connection is accepted but the TLS handshake never completes, so the request
hangs). With that peer down, the community feed request **hung ~60–70s** before returning 0 items
(reproducible: 70s with Lemmy also down, 60s with only `iris-dev2` down).

Root cause (empirical): the client's `HttpClient.Timeout = 5s` (`ActivityPubServerExtensions.cs:491`)
bounds the *send* phase of a request, but for a peer that **accepts the TCP connection and then stalls
the TLS handshake**, the connection phase is not capped by `HttpClient.Timeout` — it falls back to the
transport's default `SocketsHttpHandler.ConnectTimeout` (35s in .NET), so the single unreachable peer
adds ~35–70s to the feed. The feed merges contributors **serially** (local members, then each followed
remote), so one stuck peer blocks the whole page. This violates the scenario's "no dead fetches
blocking the page" sub-criterion for the **community** feed (the person feed is less affected because
its downed peer errors fast via the proxy rather than stalling the handshake).

**Follow-up work item (not fixed in this slice):** bound the *connection* phase as well as the *send*
phase — e.g. construct the outbound transport as a `SocketsHttpHandler` with an explicit
`ConnectTimeout` (matching the 5s `HttpClientTimeout`) in `ActivityPubClientFactory.Create`
(`ActivityPubClientFactory.cs:128`), and/or parallelize the feed's per-contributor remote fetches with
a per-fetch `CancellationTokenSource` timeout so one unreachable peer cannot block the others.

## Finding 2 — a foreign-IRI remote object page 404s via the object catch-all

A remote object whose IRI is foreign (e.g. `https://lemmy.luit.ink/post/1`, stored in the local object
store after the 138.20/139.3-s5 backfill) **404s** when requested through the object catch-all
endpoint (`/ap/v1/{...}` → `ObjectDocumentHandler`, `ActivityPubServerExtensions.cs:6895`). The
endpoint reconstructs the lookup IRI as `baseUrl + RoutePrefix + path`, so a request for
`/ap/v1/post/1` looks up the **local** IRI `https://iris.luit.ink/ap/v1/post/1` — not the stored
**foreign** IRI `https://lemmy.luit.ink/post/1`. The store *does* contain the foreign object (the
138.21 store-level integration tests pass), but the endpoint cannot address it by path because the
path doesn't encode the foreign host.

Remote objects are therefore reachable offline **only** via the proxy cache (a prior successful proxy
fetch) or a live proxy fetch (which fails when the peer is down) — **not** via the object catch-all.
The 138.21 assumption ("the object endpoint renders a stored foreign object") does not hold at the
endpoint level.

**Follow-up work item (not fixed in this slice):** either (a) serve stored foreign objects by an
explicit `?iri=`-style lookup on the object endpoint (as the UI's `/object?iri=` route already does —
which is why the UI renders them even when the catch-all 404s), or (b) document that foreign objects
are addressed by full IRI, not by a local path. The UI already uses `?iri=`, so the user-facing path
is covered; this is a REST-addressability gap, not a UI gap.

## Screenshot

A Playwright screenshot of the offline public timeline (peer stopped) was attempted as the scenario's
required evidence; the Playwright MCP's screenshot save was failing in this environment (output-dir
ENOENT), so the **accessibility snapshot** of the rendered offline timeline is recorded here instead:
the public timeline rendered **20+ posts** (mastodon.social `deadline`/`Gargron`/`Infoseepage`,
hachyderm.io `skinnylatte`, beige.party `MamaLake`, aus.social `andy47`, masto.nyc, musicworld.social,
mas.to, neopaquita.es, tux.social, sauropods.win, tiggi.es, mastodon.me.uk, beautifullosers.org,
mastodon.world) **plus andrew's local note** ("Cache consistency check 139.3-s2 …") — all from local
storage with the Lemmy peer stopped. This is the functional equivalent of the required screenshot.

## Full suite

`dotnet test --filter "Category!=Slow"` → **1306 passed, 0 failed, 8 skipped** (no code change in this
slice; review/verification only). Build: 0 warnings, 0 errors.

## Disposition

- **Core offline-rebuild bar: PASS** — local + cached-remote content renders fully from local storage
  with a peer stopped; the person feed and public timeline degrade gracefully (5s per-fetch cap).
- **Finding 1 (community-feed dead-fetch hang ~60–70s on an unreachable peer):** logged, follow-up
  work item (bound the connection phase; parallelize per-contributor fetches).
- **Finding 2 (foreign-IRI object 404 via the catch-all):** logged, follow-up work item (the UI's
  `?iri=` path already covers the user-facing case; the REST catch-all does not address foreign IRIs).
