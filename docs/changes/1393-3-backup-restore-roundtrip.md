# 139.3 Scenario 3 — Backup/restore round-trip

**Phase:** 139.3 — Data lifecycle & persistence review
**Scenario:** 3 (of 11) — "Backup/restore round-trip"
**Date:** 2026-09-18
**Result:** **FAIL found → FIXED.** The media backup/restore was silently broken (captured 0 of 5,204 media blobs). Fixed both scripts; round-trip now verified clean.

## What was tested

Ran `scripts/backup-iris.sh` then `scripts/restore-iris.sh --yes` against the live,
populated `iris.luit.ink` instance (compose project `irisweb`), and confirmed the
restored instance matches the pre-backup state.

## Baseline (pre-backup, 11:16:46 UTC)

| Table              | Before | After restore | Delta |
|--------------------|-------:|--------------:|------:|
| Actors             |   2095 |          2095 |     0 |
| Objects            |   8516 |          8516 |     0 |
| Activities         |    865 |           865 |     0 |
| Edges              |  11190 |         11190 |     0 |
| BoxItems           |   1419 |          1419 |     0 |
| UserAccounts         |    9 |             9 |     0 |
| Media              |   5222 |          5222 |     0 |
| Keys                |   17 |            17 |     0 |
| InstanceMetadata    |    1 |             1 |     0 |

- **DB row counts: BEFORE == AFTER (identical)** — no data loss.
- **Media blobs (volume root): 5,204 before == 5,204 after** — no loss, no duplication.
- **App:** restarted healthy in 8 s; home feed renders (posts, avatars, like/boost
  counts); a media blob serves HTTP 200; andrew's session cookie survived the
  Data Protection keys restore (DP key ring was restored).

## Bug found: media backup captured 0 files

The first run of the (unmodified) backup script reported:

```
[3/3] Backing up media volume...
    Media volume is empty — skipping.
```

…even though the app had **5,204** media blobs. A restore from that backup would
have **silently deleted every uploaded image**.

### Root cause (two independent defects)

1. **Unprefixed volume name (both scripts).** The media step used a raw
   `docker run -v iris-media-data:/media ...`. But Docker names compose volumes
   `<project>_<declared-name>` — the real volume is **`irisweb_iris-media-data`**.
   The raw mount attached a *different*, empty volume, so:
   - backup read 0 files (the empty `iris-media-data`), and
   - restore would have extracted into the empty volume, leaving the app's real
     volume untouched-but-not-restored (a DR restore that "succeeds" but loses all
     media).

   The DB and Data Protection keys steps were unaffected because they use
   `docker compose exec`/`cp`, which resolve the correct container — only the
   media step used a raw volume mount.

2. **Volume-root vs subdirectory path (backup fix v1).** A first fix that captured
   the media with `tar -C /data media` produced paths prefixed `media/<hash>`.
   The real volume stores blobs at its **root** (`<hash>`, because the volume is
   mounted at `/data/media`). Restoring those `media/…` paths created a nested
   `/media/media/` directory the app never reads — a second silent data-loss mode.
   (Observed during testing: a `--media-only` restore produced 10,408 files =
   5,204 original + 5,204 in the unused `media/media/` subdir; cleaned up.)

3. **Host bind mount of `/tmp` can be empty (restore).** The restore's media step
   used `-v $(dirname "$MEDIA_SOURCE"):/src`. A bind mount of a host `/tmp` path
   resolved to an **empty** `/src` from the helper container (the daemon's view of
   `/tmp` differs), so `tar` reported "No such file or directory" and restored 0
   blobs.

## Fix (commit `95d6150`)

- **`scripts/backup-iris.sh`** — copy media out of the *running* app via
  `docker compose exec iris-web tar -czf - -C /data/media .` (consistent with the
  DB/DP-keys steps; immune to the project-prefix issue). Captures the **volume
  root** so paths stay root-level `<hash>`.
- **`scripts/restore-iris.sh`** — resolve the real volume name from
  `docker compose config` (project name) with a `docker volume ls` suffix
  fallback; **stream** the media tarball over stdin (`gunzip -c … | docker run -i
  … alpine tar -xf - -C /media`) instead of a host bind mount, so it works
  regardless of the daemon's view of the host filesystem.

## Verification of the fix

- Backup now reports `Media files: 5204 (2.3G)`; the media tarball holds 5,204
  root-level entries (0 with a `media/` prefix).
- Full `restore-iris.sh --yes` round-trip: DB restored (DROP+recreate+pg_restore),
  DP keys restored, **media restored into `irisweb_iris-media-data`**, app healthy.
- Post-restore: row counts identical, 5,204 top-level blobs (no `media/media/`
  pollution), media HTTP 200, home feed renders, session preserved.

## Impact

- **Before:** a disaster-recovery restore would lose **all uploaded media** (the
  most user-visible, least-regeneratable data), while reporting success.
- **After:** backup/restore is a faithful round-trip for DB + DP keys + media.

## Decision (autonomous, recorded per loop policy)

- Used `docker compose exec` for the backup media copy rather than deriving the
  volume name, because it's consistent with the existing DB/DP-keys steps and
  needs no `python3`/`jq` dependency.
- For the restore (app is stopped during a DB restore, so `compose exec` is
  unavailable), derived the volume name from `docker compose config`'s project
  field with a `docker volume ls` suffix fallback, and streamed over stdin to
  avoid the bind-mount-of-`/tmp` empty-mount failure.
- Did not change the DB/DP-keys steps (already correct).
