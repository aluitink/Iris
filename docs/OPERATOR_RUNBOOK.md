# Iris Operator Runbook

Concise day-to-day reference for operating a production Iris instance. Detailed procedures
live in [apps/Iris.Web/deploy/](../apps/Iris.Web/deploy/); this runbook is the quick-reference
index + troubleshooting guide.

## Architecture at a Glance

```
Browser / Fediverse
    |
    |  HTTPS (TLS terminated by reverse proxy)
    v
Reverse Proxy (nginx or Caddy)  ←  deploy/nginx.conf or deploy/Caddyfile
    |
    |  HTTP (host:8088)
    v
Docker: iris-web (aspnet:10.0, non-root uid 1001, port 8080)
    |
    |  TCP (Docker internal network, no host port)
    v
Docker: db (postgres:16, port 5432)
```

Three Docker named volumes:

| Volume | Mount | Contents | Loss impact |
|--------|-------|----------|-------------|
| `iris-db-data` | `db:/var/lib/postgresql/data` | All Postgres data (accounts, actors, posts, follows, keys) | **Total instance loss** |
| `iris-media-data` | `iris-web:/data/media` | Uploaded image blobs | Media 404s; users re-upload |
| `iris-dp-keys` | `iris-web:/home/iris/.aspnet/DataProtection-Keys` | Auth cookie + antiforgery key ring | All user sessions invalidated |

## Configuration

All configuration is via environment variables (set in `apps/Iris.Web/.env` or the compose
`environment:` block). Key variables:

| Variable | Default | Purpose |
|----------|---------|---------|
| `IRIS_ADVERTISE_BASE` | — (required) | Public FQDN the instance tells the fediverse. Must be slash-free. |
| `POSTGRES_PASSWORD` | — (required) | Postgres superuser password. |
| `IRIS_ADMIN_USERNAME` / `IRIS_ADMIN_PASSWORD` | empty | Bootstrap admin (idempotent at boot). |
| `IRIS_MAX_REQUEST_BODY_SIZE` | 1 MiB | Inbound request-body cap (bytes). Media upload endpoint is exempt (10 MiB). |
| `IRIS_CORS_ORIGINS` | empty (same-origin only) | Comma-separated CORS allow-list. Never `*`. |
| `IRIS_SHUTDOWN_DRAIN_TIMEOUT` | 15 s | Graceful-shutdown drain for the delivery worker. |
| `IRIS_MEDIA_BLOB_DIR` | `/data/media` | Must point at the mounted volume. |
| `IRIS_LOGIN_MAX_ATTEMPTS` | 5 | Login rate-limit max attempts. |
| `IRIS_LOGIN_RATE_WINDOW_MINUTES` | 15 | Login rate-limit window. |

The WASM client's `appsettings.json` has `Iris:AdvertiseBase` hardcoded, but the client
auto-corrects at runtime: when the static value's host differs from the browser's origin,
the browser's origin is used instead (multi-instance safe, since 55.2).

## Day-to-Day Operations

### Check instance health

```bash
# Liveness (all health checks must pass):
curl -sf https://iris.example.com/ap/v1/health | jq .

# Readiness:
curl -sf https://iris.example.com/ap/v1/ready | jq .

# Metrics (Prometheus text format, no auth):
curl -s https://iris.example.com/local/v1/metrics
```

Health checks included in `/ap/v1/health`:
- `InstanceHealthCheck` — instance name + actor IRI configured
- `DeliveryQueueHealthCheck` — outbound delivery queue depth (degraded >1000, unhealthy >5000)
- `PersistenceHealthCheck` — Postgres reachable (real read)
- `DeliveryWorkerHealthCheck` — delivery worker running

### Check Docker status

```bash
cd apps/Iris.Web
docker compose ps                    # container status + health
docker compose logs iris-web --tail 50   # recent app logs
docker compose logs db --tail 20         # recent Postgres logs
```

### Restart the app (no data loss)

```bash
docker compose restart iris-web
# Wait for health:
curl -sf https://iris.example.com/ap/v1/health
```

### Full restart (app + database)

```bash
docker compose down
docker compose up -d
# Wait for db health (pg_isready) + app health:
curl -sf https://iris.example.com/ap/v1/health
```

### View delivery metrics

```bash
curl -s https://iris.example.com/local/v1/metrics | grep iris_delivery
```

Key counters:
- `iris_delivery_enqueued_total` — activities queued for delivery
- `iris_delivery_delivered_total` — activities successfully delivered
- `iris_delivery_attempt_failed_total` — delivery attempts that failed
- `iris_delivery_dead_lettered_total` — activities that exhausted retries

## Backup and Restore

Full procedure: [deploy/BACKUP.md](../apps/Iris.Web/deploy/BACKUP.md).

### Daily backup (automated)

The `scripts/backup-iris.sh` script creates a timestamped tarball in `./backups/`:
1. `pg_dump --format=custom` (selective table restore possible)
2. Data Protection key copy
3. Media volume archive
4. `MANIFEST.txt` with metadata + restore instructions

Scheduling: systemd timer (daily 03:00, `Persistent=true`) or cron. Off-site copy via
rsync/S3 recommended. Retention: 14 days (configurable via `BACKUP_RETENTION_DAYS`).

### Restore

```bash
# Full restore:
scripts/restore-iris.sh ./backups/iris-backup-YYYYMMDD-HHMMSS.tar.gz

# Database only:
scripts/restore-iris.sh --db-only ./backups/iris-backup-YYYYMMDD-HHMMSS.tar.gz
```

Flow: stop app → drop/recreate DB → `pg_restore` → copy DP keys → extract media → start app →
wait for health. RTO: < 15 minutes.

## Monitoring and Alerting

Full guide: [deploy/MONITORING.md](../apps/Iris.Web/deploy/MONITORING.md).

### Key log patterns to alert on

| Pattern | Level | Meaning |
|---------|-------|---------|
| `Signature rejected` | Warn | Expected federation noise (unresolvable remote key). Alert only on sustained spikes. |
| `Inbox accepted` | Info | Normal inbound activity. |
| `Inbox rejected` | Warn | Inbound activity rejected (bad signature, invalid format). Alert on sustained rate. |
| `dead letter` | Error | Delivery exhausted retries. Investigate the target instance. |
| `DeliveryWorker.*exception` | Error | Delivery worker crashed. Restart the container. |
| `UnauthorizedAccessException` | Error | Volume permission issue. Check `iris-media-data` ownership. |

### Prometheus scrape

Point Prometheus at `https://iris.example.com/local/v1/metrics` (no auth; restrict via
network policy or a reverse-proxy auth rule for production).

## Troubleshooting

### "All user sessions invalidated after container restart"

**Cause:** Data Protection keys not persisted to the `iris-dp-keys` volume.

**Check:**
```bash
docker compose exec iris-web ls -la /home/iris/.aspnet/DataProtection-Keys/
# Should show key XML files owned by uid 1001
```

**Fix:** Ensure `HOME: /home/iris` is set in the compose environment (required because
`setpriv` drops root without setting HOME). If keys are missing, users must re-login;
future restarts will persist the new key ring.

### "Images 404 after container restart"

**Cause:** Media blobs not persisted to the `iris-media-data` volume.

**Check:**
```bash
docker compose exec iris-web ls -la /data/media/ | head
docker compose exec iris-web stat /data/media  # Should be owned by uid 1001
```

**Fix:** Ensure `IRIS_MEDIA_BLOB_DIR=/data/media` is set and the entrypoint's
`chown -R 1001:1001 /data/media` ran successfully.

### "Federation not working — remote instances can't reach us"

**Checklist:**
1. `IRIS_ADVERTISE_BASE` is set to the correct public FQDN.
2. The reverse proxy forwards to host port 8088.
3. `curl https://iris.example.com/.well-known/nodeinfo` returns the NodeInfo discovery doc.
4. `curl https://iris.example.com/ap/v1/nodeinfo/2.0` shows the correct `base` URL.
5. The FQDN resolves to the host IP (DNS check: `dig +short iris.example.com`).
6. The reverse proxy is not blocking large POST bodies (nginx `client_max_body_size` /
   Caddy `request_body max_size` should be ≥ 10 MiB for media).

### "WASM client shows wrong actor IRI for remote users"

**Cause (fixed in 55.2):** The WASM client's static `Iris:AdvertiseBase` in
`appsettings.json` was hardcoded to one instance's FQDN. The fix auto-corrects when the
static host differs from the browser's origin. If this recurs, verify the fix is deployed
and that the browser's origin matches the instance's FQDN.

### "Delivery queue growing"

**Check:**
```bash
curl -s https://iris.example.com/local/v1/metrics | grep delivery_queue
docker compose logs iris-web --tail 100 | grep -i "delivery\|dead"
```

If the queue is growing, check:
- Target instances are reachable (network/firewall).
- Delivery worker is running (`DeliveryWorkerHealthCheck` in `/ap/v1/health`).
- No sustained `dead letter` errors (investigate the target instances).

### "503 from /ap/v1/health"

At least one health check is unhealthy. Check the response body for which check failed:
```bash
curl -s https://iris.example.com/ap/v1/health | jq '.checks'
```

Common causes:
- `PersistenceHealthCheck` — Postgres is down. Check `docker compose ps db`.
- `DeliveryWorkerHealthCheck` — Worker crashed. Restart the container.
- `DeliveryQueueHealthCheck` — Queue backlog. See "Delivery queue growing" above.

## Upgrades

Full procedure: [deploy/RELEASE.md](../apps/Iris.Web/deploy/RELEASE.md).

### Standard upgrade

```bash
# 1. Build + tag the new image:
cd /workspace
docker build -t iris-web:NEW_TAG -f apps/Iris.Web/Dockerfile apps/Iris.Web/

# 2. Update docker-compose.yml image tag (or .env IMAGE_TAG)

# 3. Deploy:
cd apps/Iris.Web
docker compose pull
docker compose up -d --force-recreate iris-web

# 4. Verify:
curl -sf https://iris.example.com/ap/v1/health | jq .
```

Database migrations run automatically at startup (EF Core `EnsureCreated` /
`Migrate`). No manual migration step is required.

### Rollback

```bash
# Revert to the previous image tag:
docker compose up -d --force-recreate iris-web
```

If the new version's schema migration is incompatible, restore the database from the
pre-upgrade backup (see Backup and Restore).

## Security Notes

- **Non-root:** The app runs as uid 1001 (`iris` user). The entrypoint `chown`s the
  volume mount points before dropping privileges via `setpriv`.
- **TLS:** Terminated by the reverse proxy. The app uses `UseForwardedHeaders()` to read
  `X-Forwarded-Proto` / `X-Forwarded-For`.
- **Cookie auth:** `iris.auth` cookie, HttpOnly, SameSite=Lax, SecurePolicy=SameAsRequest,
  14-day sliding expiration.
- **Rate limiting:** Login endpoint is rate-limited (5 attempts / 15 min by default).
  The reverse proxy adds additional rate limiting on non-health endpoints.
- **CORS:** Same-origin only by default. Configure `IRIS_CORS_ORIGINS` only if needed.
- **Request body cap:** 1 MiB for federation endpoints (10 MiB for media upload).
- **OpenAPI/Swagger:** The `/api` Swagger UI is available in all environments. Restrict
  access via the reverse proxy in production if desired.

## File Reference

| File | Purpose |
|------|---------|
| `apps/Iris.Web/docker-compose.yml` | Production compose (app + Postgres) |
| `apps/Iris.Web/docker-compose.fed2.yml` | Second instance for federation testing |
| `apps/Iris.Web/.env` | Environment variable overrides |
| `apps/Iris.Web/.env.example` | Documented env var template |
| `apps/Iris.Web/Dockerfile` | Two-stage build (sdk:10.0 → aspnet:10.0) |
| `apps/Iris.Web/deploy/README.md` | Reverse proxy setup (nginx + Caddy) |
| `apps/Iris.Web/deploy/BACKUP.md` | Backup/restore procedure |
| `apps/Iris.Web/deploy/MONITORING.md` | Monitoring + alerting guide |
| `apps/Iris.Web/deploy/RELEASE.md` | Release/upgrade procedure |
| `apps/Iris.Web/deploy/nginx.conf` | Production nginx server block |
| `apps/Iris.Web/deploy/Caddyfile` | Equivalent Caddy config |
| `scripts/backup-iris.sh` | Backup script |
| `scripts/restore-iris.sh` | Restore script |
