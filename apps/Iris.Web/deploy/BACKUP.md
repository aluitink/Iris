# Iris — Backup & Restore

## What to back up

| Component | Location in container | Why |
|-----------|----------------------|-----|
| PostgreSQL | `iris-db-data` volume | All accounts, actors, posts, follows, communities. **Critical.** |
| Data Protection keys | `/root/.aspnet/DataProtection-Keys/` in `iris-web` | Auth cookie encryption, antiforgery tokens. Without these, all user sessions are invalidated on restore. |
| Media | `iris-media-data` volume | Uploaded images. Loss is inconvenient but not catastrophic (users can re-upload). |

## Backup script

`scripts/backup-iris.sh` creates a timestamped tar.gz containing all three components:

```bash
# Manual backup (output: ./backups/iris-backup-YYYYMMDD-HHMMSS.tar.gz)
./scripts/backup-iris.sh

# Custom output directory
BACKUP_DIR=/mnt/backups ./scripts/backup-iris.sh

# Custom retention (default: 14 days)
BACKUP_RETENTION_DAYS=30 ./scripts/backup-iris.sh
```

The script:
1. Verifies Postgres is healthy.
2. `pg_dump --format=custom` (allows selective table restore via `pg_restore --table=...`).
3. Copies Data Protection keys from the app container.
4. Archives the media volume (skipped if empty).
5. Writes a `MANIFEST.txt` with metadata + restore instructions.
6. Prunes backups older than `BACKUP_RETENTION_DAYS`.

## Restore script

`scripts/restore-iris.sh` restores from a backup archive:

```bash
# Full restore (DB + keys + media)
./scripts/restore-iris.sh ./backups/iris-backup-20260908-120000.tar.gz

# Database only
./scripts/restore-iris.sh ./backups/iris-backup-20260908-120000.tar.gz --db-only

# Non-interactive (disaster recovery)
./scripts/restore-iris.sh ./backups/iris-backup-20260908-120000.tar.gz --yes
```

The restore:
1. Stops `iris-web` (if restoring DB).
2. Drops and recreates the Postgres database, then `pg_restore`.
3. Copies Data Protection keys back into the app container.
4. Extracts media into the named volume.
5. Starts `iris-web` and waits for the health check.

## Scheduled backups (systemd timer)

Create `/etc/systemd/system/iris-backup.service`:

```ini
[Unit]
Description=Iris production backup
After=docker.service
Requires=docker.service

[Service]
Type=oneshot
WorkingDirectory=/opt/iris
ExecStart=/opt/iris/scripts/backup-iris.sh
Environment=BACKUP_DIR=/mnt/backups/iris
Environment=BACKUP_RETENTION_DAYS=30
# If the .env is not at the default location:
# Environment=COMPOSE_FILE=/opt/iris/apps/Iris.Web/docker-compose.yml
```

Create `/etc/systemd/system/iris-backup.timer`:

```ini
[Unit]
Description=Run Iris backup daily at 03:00

[Timer]
OnCalendar=*-*-* 03:00:00
Persistent=true

[Install]
WantedBy=timers.target
```

Enable:

```bash
sudo systemctl daemon-reload
sudo systemctl enable --now iris-backup.timer
# Verify:
systemctl list-timers | grep iris
```

## Scheduled backups (cron)

If systemd timers are not available:

```cron
# /etc/cron.d/iris-backup
0 3 * * * root cd /opt/iris && BACKUP_DIR=/mnt/backups/iris BACKUP_RETENTION_DAYS=30 ./scripts/backup-iris.sh >> /var/log/iris-backup.log 2>&1
```

## Data Protection key persistence (recommended)

By default, ASP.NET Core stores Data Protection keys in `/root/.aspnet/DataProtection-Keys/`
**inside the container filesystem**. This means `docker compose down` + `up` (which recreates
the container) loses the keys, invalidating all user sessions.

**Fix:** mount a named volume for the keys in `docker-compose.yml`:

```yaml
  iris-web:
    volumes:
      - iris-media-data:/data/media
      - iris-dp-keys:/root/.aspnet/DataProtection-Keys   # ADD THIS LINE
```

And declare the volume at the bottom:

```yaml
volumes:
  iris-db-data:
  iris-media-data:
  iris-dp-keys:          # ADD THIS LINE
```

With this volume, keys survive container recreation. The backup script still copies them
(belting and suspenders — the volume protects against container loss; the backup protects
against volume loss).

## Off-site backup (recommended for production)

The backup script writes to a local directory. For disaster recovery, copy the tarball to
off-site storage after each backup:

```bash
# Add to the systemd service or a separate timer:
rsync -az /mnt/backups/iris/ backup-user@backup-host:/backups/iris/
# or
aws s3 cp /mnt/backups/iris/ s3://iris-backups/ --recursive
```

## Verify backups

After the first backup, test the restore on a staging environment:

```bash
# 1. Create a test stack with different volume names (copy docker-compose.yml, change volume names)
# 2. Run the restore script pointing at the test stack
# 3. Verify the app comes up healthy and data is present
```

## RPO / RTO targets

- **RPO (Recovery Point Objective):** 24 hours (daily backup at 03:00).
- **RTO (Recovery Time Objective):** < 15 minutes (stop app → restore DB → start app).

For a higher RPO (e.g., 1 hour), run the backup more frequently or add a continuous
`pg_dump` stream (e.g., `wal-g` for WAL archiving).
