# 135.1b — Deploy a local Lemmy instance for real Iris ↔ Lemmy interop

**Date:** 2026-09-13
**Slice:** 135.1 (PLAN "Up Next" — deploy a local Lemmy container in its own folder and interoperate with it; the second vertical slice, after 135.1a persisted remote community `Group` documents)
**Commits:** *(this commit)* — `Deploy a local Lemmy instance for Iris interop (135.1b)`

## Goal

Stand up a real, separate **Lemmy** server so Iris can be exercised against an actual
third-party ActivityPub implementation (not just Iris-to-Iris). Per the Phase 135.1 requirement the
Lemmy container/compose lives in **its own folder** (`lemmy/`), separate from the Iris app, and is
advertised under the public FQDN `https://iris-dev2.luit.ink` (the reverse proxy forwards
`iris-dev2.luit.ink:443` → host `8082` → the container's `8536`).

## What was deployed

- **`lemmy/docker-compose.yml`** — two services on the shared `irisweb_iris-web-net` Docker network:
  - `db`: `postgres:16` with a named volume (`lemmy-db-data`), health-checked via `pg_isready`.
  - `lemmy`: built from `lemmy/Dockerfile`, host port `8082` → container `8536` (Lemmy 0.19's
    backend port), health-checked against `/api/v3/version`.
- **`lemmy/Dockerfile`** — `FROM dessalines/lemmy:0.19.20` + `COPY config/config.hjson
  /config/config.hjson`. The config is **baked into the image** (not bind-mounted at runtime) because
  this host's Docker runtime **file bind mounts are broken** (a `-v file:target` creates an empty
  directory at the target instead of binding the file); named volumes and build contexts work fine.
- **`lemmy/config/config.hjson`** — Lemmy 0.19's hjson config: hostname `iris-dev2.luit.ink`,
  bind `0.0.0.0:8536`, `tls_enabled: true` (TLS is terminated by the reverse proxy; Lemmy must know
  so it issues `https` IRIs for federation), database → the `db` service, pictrs disabled
  (`image_mode: "None"` — no image hosting for text-only interop), and a first-run `setup` block that
  creates the `lemmyadmin` user + the "Iris Lemmy Interop" site.
- **`lemmy/config/config.toml`** — a pointer note only (Lemmy 0.19 uses hjson, not toml).

This **replaces the iris2** (`iris-dev2.luit.ink`) Iris test instance at the same FQDN/port. The iris2
Postgres data volume (`irisweb_iris2-db-data`) belongs to the iris2 compose project, not this one, so
this project's `down -v` does **not** destroy it — iris2 can be re-provisioned if Iris-to-Iris live
interop is needed again.

## Verification (what works)

- Lemmy is **running and healthy**: it serves `/.well-known/nodeinfo`, its ActivityPub node document
  at `/`, and `/api/v3/site` (federation enabled).
- A real Lemmy **community `test`** was created via the REST API (`POST /api/v3/community`); it serves
  a proper ActivityStreams **`Group`** document at `https://iris-dev2.luit.ink/c/test` with
  `publicKey`, `inbox`, `followers`, and `outbox` — exactly the document shape 135.1a's
  `RemoteCommunityPersister` is built to persist.
- A Lemmy **admin** (`lemmyadmin`) was created (via the first-run `setup`), and the site was
  configured (federation enabled) via the REST API.
- The **network path** is confirmed: Iris and Lemmy share the `irisweb_iris-web-net` Docker network;
  Lemmy's `Group` document is publicly reachable at the FQDN.

## Blockers for the *full* live follow-interop (external infra)

The end-to-end "Iris community follows the Lemmy community → the `Group` is persisted to
`ICommunityStore` and served via `/ap/v1/actor?iri=`" could not be driven this turn, due to two
**external infrastructure** issues outside the containers:

1. **The external nginx reverse proxy returns `400` on the required POST federation endpoints.** Both
   `https://iris.luit.ink/local/v1/c/{name}/follow/…` and `https://iris.luit.ink/ap/v1/proxy/…` return
   an nginx `400` (empty body, `server: nginx/1.29.1`) **before reaching the .NET app** — while other
   POSTs (e.g. `/local/v1/notifications/read`) pass through to the app. The same nginx also 404s
   Lemmy's `POST /api/v3/follow`. Hitting the Iris container **directly** (bypassing nginx, at its
   Docker-network IP) gets past the 400 and reaches the handler (403/500, see #2) — confirming the
   400 is an nginx path-restriction, not an app bug.
2. **The running Iris container's actor Basic-auth credentials do not match the PLAN notes.** On
   direct container access, `alice`/`adminpass123` and `andrew`/`Password1` both return `401`/`403`
   (the proxy and community-follow endpoints), so a community owner cannot be authenticated to drive
   the follow. (`GET /ap/v1/c/technology/members` returns `200` **without** auth — it is public — so
   earlier "auth worked" observations were a false positive.)

**Unblocked when:** the operator (a) whitelists the POST federation paths in the external nginx
(`/local/v1/c/*/follow/*`, `/ap/v1/proxy/*`, and Lemmy's `/api/v3/follow`), and (b) confirms the live
Iris container's actor credentials (or re-seeds known ones). Then: `POST
/local/v1/c/{community}/follow/https://iris-dev2.luit.ink/c/test` as the community owner triggers
`IrisActorDocumentFetcher.GetActorAsync(…/c/test)` → `RemoteCommunityPersister` persists the `Group`;
verify with `GET /ap/v1/actor?iri=https://iris-dev2.luit.ink/c/test` (expect `200`, the stored `Group`).

## Why the persistence logic is still considered proven

The **core 135.1 logic** (persist a remote `Group` to `ICommunityStore` + serve it via
`/ap/v1/actor?iri=`) is **proven by the passing unit/integration tests** from 135.1a
(`RemoteCommunityPersisterTests` (6) + `GetActor_RemoteCommunity_PersistsItAndReturnsItForKeyResolution`),
which exercise exactly the fetch → persist → serve path without needing the live network. This slice
delivers the **real Lemmy deployment** (the other half of 135.1) and confirms the Lemmy `Group`
document is reachable in the exact shape the persister expects; the remaining step is wiring the two
together live, which is gated on the external infra above.

## Files

- `lemmy/docker-compose.yml` (new)
- `lemmy/Dockerfile` (new)
- `lemmy/config/config.hjson` (new)
- `lemmy/config/config.toml` (new — pointer note)
