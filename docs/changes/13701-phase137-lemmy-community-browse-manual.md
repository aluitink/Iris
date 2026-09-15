# 137.1 — Lemmy and Iris community peering: manual browser pass (root cause)

**Date:** 2026-09-14
**Slice:** 137.1 (PLAN "Up Next" — manual testing to figure out why we cannot view Lemmy communities from Iris)
**Status:** **DIAGNOSIS COMPLETE — no fix made yet.** Two independent root causes found and
verified live. Awaiting operator direction on the fix before implementing.

## What was tested

Clean-entry Playwright pass (primary account `andrew`) against the live stack at
`https://iris.luit.ink`, with the live Lemmy 0.19.20 instance. Two Lemmy communities were in play:

| Community | IRI Iris has | Status |
|---|---|---|
| `test` (old) | `https://iris-dev2.luit.ink/c/test` | **STALE** — host unreachable |
| `interop` (current) | `https://lemmy.luit.ink/c/interop` | valid Lemmy community (1 post, 1 follower) |

## Root cause 1 — a stale Lemmy community IRI is persisted and its host is dead

Iris's `/communities` page lists the old Lemmy community `test` with IRI
`https://iris-dev2.luit.ink/c/test`. That IRI is persisted in the durable store
(`Actors` table: `Handle=test`, `Document->>'id' = https://iris-dev2.luit.ink/c/test`, `type=Group`).

The Lemmy instance was **re-deployed** at some point:

- The baked `lemmy/config/config.hjson` `hostname` is now `lemmy.luit.ink` (originally
  `iris-dev2.luit.ink`, per the 135.1b deployment doc).
- The old `test` community was replaced by `interop`.
- The external reverse proxy **no longer routes `iris-dev2.luit.ink`** — every request to
  `https://iris-dev2.luit.ink/...` fails at the TCP layer (`curl` returns `000`, connection
  failure). `lemmy.luit.ink` and `iris.luit.ink` both resolve to the same host IP and work.

**Repro (live):**

```
GET  https://iris.luit.ink/community?iri=https%3A%2F%2Firis-dev2.luit.ink%2Fc%2Ftest
     → page renders the community header, then the Feed tab is stuck on "Loading…" forever.
POST https://iris.luit.ink/ap/v1/proxy/https://iris-dev2.luit.ink/c/test/feed
     → 504 (the proxy waits ~60s for iris-dev2.luit.ink, which never answers, then times out)
POST https://iris.luit.ink/ap/v1/proxy/https://iris-dev2.luit.ink/c/test/members
     → 504 (same)
curl  https://iris-dev2.luit.ink/api/v3/site  → 000 (connection failure; host is dead)
```

The UI's `PagedCollection` for the feed never resolves, so the page shows "Loading…" with no
error and no timeout — the user cannot tell anything went wrong.

## Root cause 2 — Lemmy 0.19 does not expose the AP routes Iris's community page fetches

Even with the **current, valid** Lemmy community, the feed and members are empty. Iris's
`CommunityDetail.razor` fetches:

- the **feed** from `<communityIri>/feed` (`Iri.FeedOf()` → `IriExtensions.cs:93` appends `/feed`), and
- the **members** from `<communityIri>/members` (`AppendSegment(iri, "members")`,
  `CommunityDetail.razor:404`).

Lemmy 0.19.20 **does not serve** those AP routes. Verified live:

| Lemmy AP route | Status | What it is |
|---|---|---|
| `GET /c/interop` | **200** | the `Group` actor document (Iris fetches this fine) |
| `GET /c/interop/outbox` | **200** | `OrderedCollection` of the community's **posts** (`totalItems=1`) |
| `GET /c/interop/followers` | **200** | `OrderedCollection` of **followers** (`totalItems=1`) |
| `GET /c/interop/feed` | **404** | **does not exist in Lemmy** (Mastodon/Pleroma convention) |
| `GET /c/interop/members` | **404** | **does not exist in Lemmy** (Mastodon/Pleroma convention) |

**Repro (live):**

```
GET  https://iris.luit.ink/community?iri=https%3A%2F%2Flemmy.luit.ink%2Fc%2Finterop
     → header renders (name "Iris Interop", description correct), but:
       Feed tab   → "No posts in this community yet."   (proxy /feed → 404)
       Members tab→ "No members yet."                    (proxy /members → 404)
Console errors:
  [500] /ap/v1/actor?iri=https://lemmy.luit.ink/c/interop     (key-resolution fetch; doc is served 200 via proxy)
  [404] /ap/v1/proxy/https://lemmy.luit.ink/c/interop/feed
  [404] /ap/v1/proxy/https://lemmy.luit.ink/c/interop/members  (×2)
```

So the community **resolves** (the `Group` doc is fetched and the page renders its metadata), but
the **feed and members are empty** because Iris asks Lemmy for Mastodon-shaped routes (`/feed`,
`/members`) that Lemmy never implements — Lemmy uses `/outbox` (posts) and `/followers`.

## Side observation (separate, pre-existing) — DB password-auth burst at startup

On the first load after a container recreate, the app logged a burst of
`Npgsql 28P01: password authentication failed for user "iris"` (one ~500ms burst at startup),
which produced the 500s on a few proxy calls at that moment. It did **not** recur afterwards, and a
direct `Npgsql` connect from inside the app container (host `db`, user `iris`, the configured
password) succeeds. This is a transient startup-timing artifact (the EF connection opening while the
Postgres container was still settling), **not** a credential mismatch — the credentials and the
network path are both verified correct. Noted here only so it isn't re-chased as a separate bug.

## Why we "cannot view Lemmy communities from Iris" — summary

Two independent problems, both confirmed live:

1. **Stale IRI** — the only Lemmy community listed on `/communities` (`test`) points at
   `iris-dev2.luit.ink`, a host that is no longer served by the reverse proxy. Browsing it hangs on
   "Loading…" (proxy 504). The live Lemmy community is now `lemmy.luit.ink/c/interop`, which is not
   in Iris's community list.
2. **AP-route mismatch** — even the valid `lemmy.luit.ink/c/interop` renders its header but shows
   no posts / no members, because Iris fetches `/feed` + `/members` (Mastodon/Pleroma routes) while
   Lemmy only serves `/outbox` (posts) + `/followers`.

## Proposed fix (not yet implemented — needs a decision)

- **Lemmy adapter in the community feed/members read path** (server-side, in
  `CommunityFeedService` / the proxy, or a capability-aware `Iri` extension): when the community's
  `id`/`type`/`@context` identifies it as a **Lemmy** `Group`, map
  **feed → `<iri>/outbox`** and **members → `<iri>/followers`** (both verified 200 on Lemmy).
  Keep `/feed` + `/members` for Mastodon/Pleroma-shaped communities. This is the real interop fix.
- **Stale-IRI hygiene**: either (a) re-point/remove the dead `iris-dev2.luit.ink/c/test` entry (it is
  an operator data decision — the community no longer exists on Lemmy), or (b) add a bounded
  timeout + "unreachable community" error state to the community page so a dead IRI shows a clear
  message instead of an infinite "Loading…".
- The startup PG password-auth burst is a separate, transient item — if it recurs, investigate the
  EF connection-open ordering vs. the Postgres healthcheck, not the credentials.

## Evidence (commands, run this turn)

- `curl -sk https://iris-dev2.luit.ink/api/v3/site` → `000` (dead host); `curl -sk
  https://lemmy.luit.ink/api/v3/site` → site JSON (`Iris Lemmy Interop`).
- `curl -sk https://lemmy.luit.ink/c/interop -H 'Accept: application/activity+json'` → 200 `Group`.
- `curl -sk https://lemmy.luit.ink/c/interop/outbox` → 200 `OrderedCollection totalItems=1`.
- `curl -sk https://lemmy.luit.ink/c/interop/{feed,members}` → 404.
- `docker exec irisweb-db-1 psql …` → `Actors` row `test` / `https://iris-dev2.luit.ink/c/test` / `Group`.
- Playwright: `iris-dev2.../c/test` page stuck on "Loading…"; `lemmy.luit.ink/c/interop` page →
  "No posts" + "No members", console 404s on `/feed` + `/members`.
