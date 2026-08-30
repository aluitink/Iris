# Sample Docker Composition (Phase 8)

This documents the Phase 8 sample deployment: two real Iris ActivityPub servers **plus the Blazor
WebAssembly "server explorer"** running in containers on an internal Docker network, exercising
**cross-container federation over genuine network I/O** (not the in-process `TestServer` used by the
unit/integration tests).

## Topology

```
                    ┌─────────────────────────────┐
   host:8081 ─────► │  iris-a   (iris-a:8080)      │
                    │  actor alice / community iris│
                    │  base URI http://iris-a:8080 │
                    └───────────────┬─────────────┘
                                    │  iris-net (bridge, Docker DNS)
                    ┌───────────────┴─────────────┐
   host:8082 ─────► │  iris-b   (iris-b:8080)      │
                    │  actor alice / community iris│
                    │  base URI http://iris-b:8080 │
                    └─────────────────────────────┘

   host:8090 ─────► ┌─────────────────────────────┐
                    │  iris-ui  (iris-ui:8090)     │
                    │  Blazor WASM server explorer │
                    │  static site on nginx         │
                    └─────────────────────────────┘
```

- **Two `SampleServer` instances** (`iris-a`, `iris-b`) share one image; the only difference is the
  `Iris__HostName` / `Iris__Port` / `Iris__Actor` environment, which sets each instance's
  **advertised base URI** (the actor/community IRIs) and its bind URL.
- Each container's `Iris__HostName` is its **service name** and `Iris__Port` the **real in-network
  port** (`8080`), so the base URI is routable from the other container. Docker's built-in DNS
  resolves `iris-a` / `iris-b` on the shared `iris-net` network.
- The host port mappings (`8081`/`8082` → `8080`) expose the instances on the host for manual
  inspection and the smoke test; in-container federation uses the service names, not the mappings.
- Both instances seed the same local actor (`alice`) and community (`iris`) — they are
  **independent instances**, each with its own in-memory state.
- **`iris-ui`** is the Blazor WebAssembly "server explorer" (Deliverable B) served as a static site by
  nginx on port `8090`. It is a **browser** app: all ActivityPub I/O is browser → server, so the
  `iris-ui` container itself makes no outbound network calls — it only serves the WASM site. It is
  routable as `iris-ui` on `iris-net` and published to the host on `8090` for manual use and the
  smoke test.

## Files

| File | Purpose |
|------|---------|
| `samples/SampleServer/Dockerfile` | Multi-stage build (SDK → aspnet runtime). Context = repo root. |
| `samples/SampleBlazorClient/Dockerfile` | Multi-stage build (SDK → nginx static host). Context = repo root; the WASM app is static, so nginx serves the published `wwwroot` on `8090`. |
| `.dockerignore` | Keeps the build context lean (excludes bin/obj, tests, docs, scratch). Both sample projects' sources are included (both Dockerfiles build from the root context). |
| `docker-compose.yml` | Two `SampleServer` instances + the `iris-ui` explorer on `iris-net`, health checks, host port mappings. |
| `scripts/docker-smoke-test.sh` | Boots the stack, waits for health, asserts cross-container WebFinger reachability. |

## Running

```bash
# Boot the two-instance stack (builds the image first):
docker compose -f docker-compose.yml up --build -d

# Wait for health (each instance's TCP port 8080 must accept connections):
docker inspect --format '{{.State.Health.Status}}' iris-a   # → healthy
docker inspect --format '{{.State.Health.Status}}' iris-b   # → healthy

# Inspect from the host:
curl "http://localhost:8081/.well-known/webfinger?resource=acct:alice@iris-a"
curl "http://localhost:8082/.well-known/webfinger?resource=acct:alice@iris-b"

# The server explorer (Deliverable B) is served by iris-ui on 8090:
curl "http://localhost:8090/"                       # → the WASM index.html (HTTP 200)
# …and in a browser, log on to a local instance (the dial base is the host-published port):
#   address alice@iris-a  →  base http://localhost:8081   (instance iris-a)
#   address alice@iris-b  →  base http://localhost:8082   (instance iris-b)

# Tear down:
docker compose -f docker-compose.yml down --remove-orphans
```

## Smoke test

```bash
./scripts/docker-smoke-test.sh
```

The script:
1. **Boots** the compose stack (`up --build`).
2. **Waits** for both instances to be healthy.
3. **Asserts health**: each instance serves its own actor's WebFinger (HTTP 200, correct actor IRI).
4. **Asserts cross-container reachability**: a request to `iris-b` over the `iris-net` network
   returns `iris-b`'s actor document (HTTP 200, `id: http://iris-b:8080/ap/v1/u/alice`), proving the
   two containers reach each other over genuine network I/O.
5. **Tears down** the stack (unless `IRIS_COMPOSE_KEEP=1` is set).

> **Opt-in gate**: the script skips (exit 0) when Docker or the daemon is unavailable, so local/dev
> runs without Docker are unaffected. In CI, run it in a job with the Docker service enabled.

## Health check

Each container's health check is a TCP connect to `127.0.0.1:8080` (the `aspnet` base image has no
`curl`/`wget`). A successful connect means the Kestrel web server is listening. The WebFinger
endpoint (a public GET that skips signature validation) is the application-level liveness probe used
by the smoke test.

## Routable addresses (the "Docker-only routable" rule)

The deployment keeps **advertised IRIs** and **browser dial addresses** separate (SAMPLE_PLAN §4.4):

- **Advertised IRI host** = the Docker service name (`iris-a` / `iris-b`). This is what appears in the
  actor/community IRIs inside documents (`http://iris-a:8080/ap/v1/u/alice`). It is only resolvable
  **inside** the `iris-net` network (Docker's built-in DNS), so a browser on the host cannot dial it
  directly.
- **Browser dial address** = a host-published port (`http://localhost:8081` / `http://localhost:8082`).
  This is what the explorer (and any host-side tool) actually dials. The `InstanceBaseUrls` surface
  (SAMPLE_PLAN §4.4) maps advertised host → browser base URL so the explorer pre-fills the dial base
  for a known local instance; the user enters only the WebFinger address (`alice@iris-a`) + password.

So the three services are reachable from the host at:

| Service | Advertised (in-network) host | Browser / host dial address |
|---------|------------------------------|------------------------------|
| `iris-a` | `iris-a:8080` | `http://localhost:8081` |
| `iris-b` | `iris-b:8080` | `http://localhost:8082` |
| `iris-ui` | `iris-ui:8090` | `http://localhost:8090` |

> No real FQDN is committed: the sample is self-contained on `localhost` (host-published ports) +
> service names (in-network). To point the explorer at an **external** instance, the operator supplies
> its address + a browser-reachable base URL at logon (the external-instance mechanism, documented in
> `samples/SampleBlazorClient/README.md`).

## Notes / deferred

- **Blazor client container**: the WASM host for `SampleBlazorClient` is now built and served by the
  `iris-ui` service (static nginx host on `8090`). The client is also exercised end-to-end by the
  in-process two-instance integration tests (`tests/SampleBlazorClient.Tests`), which the Docker stack
  mirrors at the network boundary.
- **CI job**: a dedicated CI job (build → run → smoke → tear down) is deferred until a baseline CI
  workflow exists; the smoke script's opt-in gate is the interim measure.
- **Real follow/post federation** (signed POST delivery between the two containers) is exercised by
  the in-process two-instance integration tests; the Docker stack proves the **deployment and
  network-connectivity** guarantee (both instances boot, advertise routable base URIs, and reach
  each other).
