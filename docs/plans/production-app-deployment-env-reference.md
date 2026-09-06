# Production App — Deployment Environment Reference

> **Level 3.** Parent: [production-app-deployment.md](production-app-deployment.md). Grandparent: [production-app-overview.md](production-app-overview.md).

## 1. `.env.example` (committed; real `.env` is git-ignored)

```dotenv
# ---- Instance identity (mirrors the existing SampleServer Iris__* convention) ----
IRIS_HOST=localhost
IRIS_PORT=8080
IRIS_HTTPS=false
IRIS_INSTANCE_NAME=my-iris
# IRIS_NAMESPACE_IRI=              # optional; defaults to {BaseUri}/ns#

# ---- Database ----
POSTGRES_DB=iris
POSTGRES_USER=iris
POSTGRES_PASSWORD=change-me
# Assembled by Iris.Web into ConnectionStrings:Iris — Host=db;Port=5432;Database=${POSTGRES_DB};Username=${POSTGRES_USER};Password=${POSTGRES_PASSWORD}

# ---- Media ----
MEDIA_BACKEND=Local                # Local | S3
# The following only matter when MEDIA_BACKEND=S3
MEDIA_S3_ENDPOINT=http://media:9000
MEDIA_S3_BUCKET=iris-media
MEDIA_S3_ACCESS_KEY=
MEDIA_S3_SECRET_KEY=

# ---- Admin bootstrap (see production-app-auth-flows.md §7; unset after first run) ----
# Mapped by docker-compose.yml to App__Admin__Username / App__Admin__Password (Iris.Web's own
# config section — not Iris:*, which is reserved for AddActivityPubServer's bound options).
IRIS_ADMIN_USERNAME=
IRIS_ADMIN_PASSWORD=

# ---- CORS (only relevant for a non-UI API consumer; same-origin UI needs none) ----
IRIS_CORS_ORIGINS=

# ---- Ports published to the host ----
# 8088 is not arbitrary: the public https://iris.luit.ink reverse proxy already forwards to this
# host's port 8088 (previously the sample stack's iris-ui; see production-app-overview.md §2 and
# production-app-deployment.md §1). Publishing iris-web on 8088 means no reverse-proxy change is needed.
WEB_PORT=8088
```

## 2. `docker-compose.yml` sketch

```yaml
services:
  db:
    image: postgres:16
    environment:
      POSTGRES_DB: ${POSTGRES_DB:-iris}
      POSTGRES_USER: ${POSTGRES_USER:-iris}
      POSTGRES_PASSWORD: ${POSTGRES_PASSWORD:?set POSTGRES_PASSWORD in .env}
    volumes:
      - iris-db-data:/var/lib/postgresql/data
    healthcheck:
      test: ["CMD-SHELL", "pg_isready -U ${POSTGRES_USER:-iris}"]
      interval: 5s
      timeout: 3s
      retries: 10
    networks: [iris-web-net]

  iris-web:
    build:
      context: ../..
      dockerfile: apps/Iris.Web/Dockerfile
    environment:
      ConnectionStrings__Iris: "Host=db;Port=5432;Database=${POSTGRES_DB:-iris};Username=${POSTGRES_USER:-iris};Password=${POSTGRES_PASSWORD}"
      Iris__HostName: ${IRIS_HOST:-localhost}
      Iris__Port: "${IRIS_PORT:-8080}"
      Iris__Https: "${IRIS_HTTPS:-false}"
      Iris__InstanceName: ${IRIS_INSTANCE_NAME:-my-iris}
      # App:* is Iris.Web's own section (never bound by AddActivityPubServer's ActivityPubServerOptions) —
      # admin bootstrap is a host-app concern, not an Iris:* library option (see production-app-authentication.md §6).
      App__Admin__Username: ${IRIS_ADMIN_USERNAME:-}
      App__Admin__Password: ${IRIS_ADMIN_PASSWORD:-}
      Media__Backend: ${MEDIA_BACKEND:-Local}
      Media__S3__Endpoint: ${MEDIA_S3_ENDPOINT:-}
      Media__S3__Bucket: ${MEDIA_S3_BUCKET:-}
      Media__S3__AccessKey: ${MEDIA_S3_ACCESS_KEY:-}
      Media__S3__SecretKey: ${MEDIA_S3_SECRET_KEY:-}
    ports:
      - "${WEB_PORT:-8088}:8080"
    volumes:
      - iris-media-data:/data/media     # only used when Media__Backend=Local
    healthcheck:
      # Depends on Phase 30.2 (health check + readiness probe, see production-app-overview.md §6) —
      # exposes /health so Compose (and any orchestrator) can tell a booted-but-not-ready container
      # (migrations still applying, key material not loaded) apart from a genuinely healthy one.
      # The base aspnet image has no curl (see the root docker-compose.yml's own note on this) — either
      # add curl in apps/Iris.Web/Dockerfile, or use a TCP-connect probe like the sample stack's iris-a/iris-b.
      test: ["CMD-SHELL", "curl -f http://localhost:8080/health || exit 1"]
      interval: 10s
      timeout: 3s
      retries: 5
      start_period: 20s
    depends_on:
      db:
        condition: service_healthy
    networks: [iris-web-net]

networks:
  iris-web-net:
    driver: bridge

volumes:
  iris-db-data:
  iris-media-data:
```

An optional `minio` service (only relevant when `MEDIA_BACKEND=S3`) can be added under a Compose `profiles: ["s3"]` tag so it doesn't start by default.

## 3. Example reverse-proxy config (Caddy), for a real public deployment

```caddyfile
iris.example.com {
    reverse_proxy iris-web:8080
}
```

Caddy handles TLS (Let's Encrypt) automatically; this file is documentation/appendix only, not part of the MVP Compose stack.

## 4. Smoke test outline

Mirroring [scripts/docker-smoke-test.sh](../../scripts/docker-smoke-test.sh)'s style, a new `scripts/prod-smoke-test.sh` should:

1. `docker compose -f apps/Iris.Web/docker-compose.yml up --build -d` from clean volumes.
2. Wait for `db` and `iris-web` health checks.
3. Register a user via HTTP (or drive it via a headless browser if the registration form has no JSON endpoint), confirm `GET /ap/v1/u/{handle}` resolves.
4. Post a note, confirm it appears in the outbox.
5. `docker compose restart iris-web` (not `down -v`), confirm the actor/post are still there.
6. `docker compose down -v` for teardown.
