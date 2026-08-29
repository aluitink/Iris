# Sample Docker Composition (Phase 8)

This documents the Phase 8 sample deployment: two real Iris ActivityPub servers running in
containers on an internal Docker network, exercising **cross-container federation over genuine
network I/O** (not the in-process `TestServer` used by the unit/integration tests).

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

## Files

| File | Purpose |
|------|---------|
| `samples/SampleServer/Dockerfile` | Multi-stage build (SDK → aspnet runtime). Context = repo root. |
| `.dockerignore` | Keeps the build context lean (excludes bin/obj, tests, docs, scratch). |
| `docker-compose.yml` | Two `SampleServer` instances on `iris-net`, health checks, host port mappings. |
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

## Notes / deferred

- **Blazor client container**: the WASM host for `SampleBlazorClient` is deferred (the Phase 8 scope
  note). The client is exercised end-to-end by the in-process two-instance integration tests
  (`tests/SampleBlazorClient.Tests`), which the Docker stack mirrors at the network boundary.
- **CI job**: a dedicated CI job (build → run → smoke → tear down) is deferred until a baseline CI
  workflow exists; the smoke script's opt-in gate is the interim measure.
- **Real follow/post federation** (signed POST delivery between the two containers) is exercised by
  the in-process two-instance integration tests; the Docker stack proves the **deployment and
  network-connectivity** guarantee (both instances boot, advertise routable base URIs, and reach
  each other).
