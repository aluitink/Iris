#!/usr/bin/env bash
#
# Iris — production restore script.
#
# Restores from a backup archive created by scripts/backup-iris.sh.
#
# Usage:
#   ./scripts/restore-iris.sh ./backups/iris-backup-20260908-120000.tar.gz
#   ./scripts/restore-iris.sh ./backups/iris-backup-20260908-120000.tar.gz --db-only
#   ./scripts/restore-iris.sh ./backups/iris-backup-20260908-120000.tar.gz --media-only
#
# Options:
#   --db-only       Restore only the database (skip keys + media)
#   --media-only    Restore only media (skip DB + keys)
#   --keys-only     Restore only Data Protection keys
#   --yes           Skip the confirmation prompt (for automated disaster recovery)
#
# WARNING: Restoring the database will DROP and recreate all tables.
# The app should be stopped during restore to avoid conflicts.
#
# Requires: docker, docker compose. Run on the host where the Compose stack runs.

set -euo pipefail

COMPOSE_FILE="${COMPOSE_FILE:-apps/Iris.Web/docker-compose.yml}"
ENV_FILE="apps/Iris.Web/.env"
ASSUME_YES=false
DB_ONLY=false
MEDIA_ONLY=false
KEYS_ONLY=false

# ---- Parse arguments ----
BACKUP_FILE=""
for arg in "$@"; do
  case "$arg" in
    --db-only) DB_ONLY=true ;;
    --media-only) MEDIA_ONLY=true ;;
    --keys-only) KEYS_ONLY=true ;;
    --yes|-y) ASSUME_YES=true ;;
    *) BACKUP_FILE="$arg" ;;
  esac
done

if [[ -z "$BACKUP_FILE" ]]; then
  echo "Usage: $0 <backup.tar.gz> [--db-only|--media-only|--keys-only] [--yes]" >&2
  exit 1
fi

if [[ ! -f "$BACKUP_FILE" ]]; then
  echo "ERROR: Backup file not found: ${BACKUP_FILE}" >&2
  exit 1
fi

# Load .env for POSTGRES_* values.
if [[ -f "$ENV_FILE" ]]; then
  set -a
  source "$ENV_FILE"
  set +a
fi
POSTGRES_USER="${POSTGRES_USER:-iris}"
POSTGRES_DB="${POSTGRES_DB:-iris}"

# ---- Determine what to restore ----
RESTORE_DB=true
RESTORE_KEYS=true
RESTORE_MEDIA=true
if $DB_ONLY; then RESTORE_KEYS=false; RESTORE_MEDIA=false; fi
if $MEDIA_ONLY; then RESTORE_DB=false; RESTORE_KEYS=false; fi
if $KEYS_ONLY; then RESTORE_DB=false; RESTORE_MEDIA=false; fi

echo "=== Iris Restore ==="
echo "Backup file: ${BACKUP_FILE}"
echo "Restore DB:    ${RESTORE_DB}"
echo "Restore keys:  ${RESTORE_KEYS}"
echo "Restore media: ${RESTORE_MEDIA}"
echo ""

if $RESTORE_DB; then
  echo "WARNING: This will DROP all tables in the '${POSTGRES_DB}' database and recreate them."
  echo "         The app should be stopped during the restore."
fi
echo ""

if ! $ASSUME_YES; then
  read -r -p "Continue? [y/N] " CONFIRM
  if [[ ! "$CONFIRM" =~ ^[Yy]$ ]]; then
    echo "Aborted."
    exit 0
  fi
fi

# ---- Extract backup ----
WORK_DIR=$(mktemp -d)
trap 'rm -rf "$WORK_DIR"' EXIT

echo "Extracting backup..."
tar -xzf "$BACKUP_FILE" -C "$WORK_DIR"

# Find the extracted directory (iris-backup-*/).
BACKUP_DIR=$(find "$WORK_DIR" -maxdepth 1 -type d -name "iris-backup-*" | head -1)
if [[ -z "$BACKUP_DIR" ]]; then
  echo "ERROR: No backup directory found in archive." >&2
  exit 1
fi
echo "Extracted to: ${BACKUP_DIR}"

# ---- Stop the app (if restoring DB) ----
if $RESTORE_DB; then
  echo "Stopping iris-web..."
  docker compose -f "$COMPOSE_FILE" stop iris-web 2>/dev/null || true
fi

# ---- 1. Database restore ----
if $RESTORE_DB; then
  echo "[1] Restoring PostgreSQL database..."
  if [[ ! -f "${BACKUP_DIR}/database.dump" ]]; then
    echo "ERROR: database.dump not found in backup." >&2
    exit 1
  fi

  # Copy the dump into the db container.
  docker compose -f "$COMPOSE_FILE" cp "${BACKUP_DIR}/database.dump" "db:/tmp/restore.dump"

  # Drop and recreate the database, then restore.
  docker compose -f "$COMPOSE_FILE" exec -T db bash -c "
    set -e
    psql -U ${POSTGRES_USER} -d postgres -c \"DROP DATABASE IF EXISTS ${POSTGRES_DB} WITH (FORCE);\"
    psql -U ${POSTGRES_USER} -d postgres -c \"CREATE DATABASE ${POSTGRES_DB} OWNER ${POSTGRES_USER};\"
    pg_restore -U ${POSTGRES_USER} -d ${POSTGRES_DB} --clean --if-exists /tmp/restore.dump
  "

  # Clean up.
  docker compose -f "$COMPOSE_FILE" exec -T db rm -f /tmp/restore.dump
  echo "    Database restored."
fi

# ---- 2. Data Protection keys ----
if $RESTORE_KEYS; then
  echo "[2] Restoring Data Protection keys..."
  if [[ -d "${BACKUP_DIR}/data-protection-keys" ]] && \
     find "${BACKUP_DIR}/data-protection-keys" -name "*.xml" | grep -q .; then
    # Copy keys into the app container (non-root iris user; 50.1 security hardening).
    docker compose -f "$COMPOSE_FILE" cp "${BACKUP_DIR}/data-protection-keys/." \
      "iris-web:/home/iris/.aspnet/DataProtection-Keys/"
    echo "    Data Protection keys restored."
  else
    echo "    No Data Protection keys in backup — skipping."
  fi
fi

# ---- 3. Media restore ----
if $RESTORE_MEDIA; then
  echo "[3] Restoring media..."
  MEDIA_TARBALL="${BACKUP_FILE%.tar.gz}-media.tar.gz"
  # The media tarball sits next to the main backup tarball.
  MEDIA_SOURCE=$(dirname "$BACKUP_FILE")/$(basename "$BACKUP_FILE" .tar.gz)-media.tar.gz
  if [[ -f "$MEDIA_SOURCE" ]]; then
    # Extract into the named volume via a helper container.
    #
    # The compose volume is declared as `iris-media-data`, but Docker names compose
    # volumes `<project>_<declared-name>` (e.g. irisweb_iris-media-data). Hardcoding
    # the unprefixed name (the old behavior) extracted into a DIFFERENT (empty) volume
    # and silently lost every media blob on restore (139.3 scenario 3 finding). Derive
    # the real volume name from the compose project so the extract lands on the volume
    # the app actually mounts. (The app is stopped during restore, so we cannot `docker
    # compose exec` into it here — we resolve the volume name instead.)
    COMPOSE_PROJECT="$(docker compose -f "$COMPOSE_FILE" config --format json 2>/dev/null \
      | python3 -c 'import sys,json; print(json.load(sys.stdin).get("name",""))' 2>/dev/null)"
    MEDIA_VOLUME="${COMPOSE_PROJECT}_iris-media-data"
    if [[ -z "$COMPOSE_PROJECT" ]]; then
      # Fallback: find the volume by its declared suffix (handles unusual project naming).
      MEDIA_VOLUME="$(docker volume ls --format '{{.Name}}' | grep -E '(^|_)iris-media-data$' | head -1)"
    fi
    if [[ -z "$MEDIA_VOLUME" ]]; then
      echo "    WARNING: could not resolve the media volume name — media NOT restored."
    else
      # Stream the media tarball into the helper container over stdin and extract it
      # into the volume. The backup stores it as gzip(tar), so we gunzip the outer
      # layer and pipe the raw tar stream in. This avoids a host bind mount of the
      # backup directory: a `-v <host-tmp-path>:/src` bind can resolve to an EMPTY
      # mount when the Docker daemon cannot see that host path (e.g. a sandboxed
      # /tmp), which silently restored 0 media blobs (139.3 scenario 3 finding).
      # Streaming over stdin works regardless of the daemon's view of the host fs.
      gunzip -c "$MEDIA_SOURCE" | \
        docker run --rm -i \
          -v "${MEDIA_VOLUME}:/media" \
          alpine tar -xf - -C /media
      echo "    Media restored into volume '${MEDIA_VOLUME}'."
    fi
  else
    echo "    No media tarball found — skipping."
  fi
fi

# ---- Restart the app ----
if $RESTORE_DB || $RESTORE_KEYS; then
  echo "Starting iris-web..."
  docker compose -f "$COMPOSE_FILE" start iris-web
  echo "    App started. Waiting for health check..."
  for i in $(seq 1 30); do
    if docker compose -f "$COMPOSE_FILE" ps iris-web | grep -q "healthy"; then
      echo "    App is healthy."
      break
    fi
    sleep 2
    if [[ $i -eq 30 ]]; then
      echo "    WARNING: App did not become healthy within 60s. Check logs:"
      echo "      docker compose -f ${COMPOSE_FILE} logs iris-web"
    fi
  done
fi

echo "=== Restore complete ==="
