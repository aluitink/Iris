#!/usr/bin/env bash
#
# Iris — production backup script.
#
# Backs up:
#   1. PostgreSQL database (pg_dump, custom format — allows selective restore)
#   2. ASP.NET Core Data Protection keys (auth cookies, antiforgery tokens)
#   3. Media volume (uploaded images)
#
# Output: a timestamped tar.gz archive containing all three, written to BACKUP_DIR.
#
# Usage:
#   ./scripts/backup-iris.sh                          # uses defaults from apps/Iris.Web/.env
#   BACKUP_DIR=/mnt/backups ./scripts/backup-iris.sh  # override output directory
#
# For scheduled backups, see the systemd timer / cron examples in
# apps/Iris.Web/deploy/BACKUP.md.
#
# Requires: docker, docker compose. Run on the host where the Compose stack runs.

set -euo pipefail

# ---- Configuration (overridable via env vars) ----
COMPOSE_FILE="${COMPOSE_FILE:-apps/Iris.Web/docker-compose.yml}"
BACKUP_DIR="${BACKUP_DIR:-./backups}"
TIMESTAMP="$(date +%Y%m%d-%H%M%S)"
BACKUP_NAME="iris-backup-${TIMESTAMP}"
BACKUP_PATH="${BACKUP_DIR}/${BACKUP_NAME}"
RETENTION_DAYS="${BACKUP_RETENTION_DAYS:-14}"

# Load .env for POSTGRES_* values (docker compose reads it, but we need the values here too).
ENV_FILE="apps/Iris.Web/.env"
if [[ -f "$ENV_FILE" ]]; then
  set -a
  source "$ENV_FILE"
  set +a
fi

POSTGRES_USER="${POSTGRES_USER:-iris}"
POSTGRES_DB="${POSTGRES_DB:-iris}"

echo "=== Iris Backup: ${BACKUP_NAME} ==="
echo "Compose file: ${COMPOSE_FILE}"
echo "Output:       ${BACKUP_PATH}"

# ---- Prerequisites ----
if ! docker compose -f "$COMPOSE_FILE" ps db | grep -q "healthy"; then
  echo "ERROR: Postgres container is not healthy. Aborting backup." >&2
  exit 1
fi

mkdir -p "$BACKUP_PATH"

# ---- 1. Database dump ----
echo "[1/3] Dumping PostgreSQL database (${POSTGRES_DB})..."
docker compose -f "$COMPOSE_FILE" exec -T db \
  pg_dump -U "$POSTGRES_USER" -d "$POSTGRES_DB" \
  --format=custom \
  --compress=6 \
  --file="/tmp/iris-dump-${TIMESTAMP}.dump"

# Copy the dump out of the container.
docker compose -f "$COMPOSE_FILE" cp "db:/tmp/iris-dump-${TIMESTAMP}.dump" \
  "${BACKUP_PATH}/database.dump"

# Clean up in-container temp file.
docker compose -f "$COMPOSE_FILE" exec -T db \
  rm -f "/tmp/iris-dump-${TIMESTAMP}.dump"

echo "    Database dump: $(du -h "${BACKUP_PATH}/database.dump" | cut -f1)"

# ---- 2. Data Protection keys ----
echo "[2/3] Backing up Data Protection keys..."
# The keys are in the app container's filesystem at /root/.aspnet/DataProtection-Keys
# (the default location when running as root in the aspnet image).
# In production, persist these on a named volume (see BACKUP.md) so they survive
# container recreation. The backup copies them from the live container.
mkdir -p "${BACKUP_PATH}/data-protection-keys"
docker compose -f "$COMPOSE_FILE" exec -T iris-web \
  tar -cf - -C /root/.aspnet DataProtection-Keys 2>/dev/null \
  | tar -xf - -C "${BACKUP_PATH}/data-protection-keys" \
  || echo "    WARNING: Data Protection keys not found (may not be generated yet)."

KEY_COUNT=$(find "${BACKUP_PATH}/data-protection-keys" -name "*.xml" 2>/dev/null | wc -l)
echo "    Data Protection keys: ${KEY_COUNT} key file(s)"

# ---- 3. Media volume ----
echo "[3/3] Backing up media volume..."
# The media volume is named iris-media-data. We use docker run with a volume mount
# to copy its contents (works whether or not the app container is running).
MEDIA_DIR="${BACKUP_PATH}/media"
mkdir -p "$MEDIA_DIR"

# List the volume's contents (skip if empty).
MEDIA_COUNT=$(docker run --rm -v iris-media-data:/media alpine find /media -type f 2>/dev/null | wc -l)
if [[ "$MEDIA_COUNT" -gt 0 ]]; then
  docker run --rm -v iris-media-data:/media -v "${BACKUP_DIR}:/backup" alpine \
    tar -czf "/backup/${BACKUP_NAME}-media.tar.gz" -C /media .
  echo "    Media files: ${MEDIA_COUNT} ($(du -h "${BACKUP_DIR}/${BACKUP_NAME}-media.tar.gz" | cut -f1))"
else
  echo "    Media volume is empty — skipping."
fi

# ---- 4. Manifest ----
echo "Writing backup manifest..."
cat > "${BACKUP_PATH}/MANIFEST.txt" <<EOF
Iris Backup
===========
Timestamp:       ${TIMESTAMP}
Host:            $(hostname)
Compose file:    ${COMPOSE_FILE}
Postgres DB:     ${POSTGRES_DB}
Postgres User:   ${POSTGRES_USER}
Media files:     ${MEDIA_COUNT}
DataProt keys:   ${KEY_COUNT}

Contents:
  database.dump           — pg_dump (custom format), restore with: pg_restore -d ${POSTGRES_DB} database.dump
  data-protection-keys/   — ASP.NET Core Data Protection key ring
  (see ${BACKUP_NAME}-media.tar.gz for media, if present)
  MANIFEST.txt            — this file

Restore procedure: see apps/Iris.Web/deploy/BACKUP.md
EOF

# ---- 5. Package (optional: tar the whole directory for easy transfer) ----
echo "Packaging backup..."
# Use absolute paths to avoid CWD confusion.
BACKUP_DIR_ABS="$(cd "$BACKUP_DIR" && pwd)"
BACKUP_NAME_ABS="${BACKUP_DIR_ABS}/${BACKUP_NAME}"
tar -czf "${BACKUP_DIR_ABS}/${BACKUP_NAME}.tar.gz" -C "$BACKUP_DIR_ABS" "${BACKUP_NAME}/"
rm -rf "${BACKUP_NAME_ABS}"  # remove the unpacked directory; keep only the tarball

FINAL_SIZE=$(du -h "${BACKUP_DIR_ABS}/${BACKUP_NAME}.tar.gz" | cut -f1)
echo "=== Backup complete: ${BACKUP_DIR_ABS}/${BACKUP_NAME}.tar.gz (${FINAL_SIZE}) ==="

# ---- 6. Retention ----
echo "Pruning backups older than ${RETENTION_DAYS} days..."
find "$BACKUP_DIR_ABS" -name "iris-backup-*.tar.gz" -type f -mtime +"$RETENTION_DAYS" -print -delete
echo "=== Done ==="
