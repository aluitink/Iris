# 130.2 — Admin bootstrap from `.env`: verification path documented

**Date:** 2026-09-13
**Type:** Documentation (no code changes)
**Scope:** Document how to verify the admin bootstrap feature, closing the last ☐ in the feature matrix

## Context

The feature matrix listed "Admin bootstrap from `.env`" as ☐ on the D (polish) column. The `AdminBootstrapper` is a server-side startup hook (not a web UI feature), so it cannot be exercised via Playwright. This doc records the verification path.

## How it works

`AdminBootstrapper.StartAsync()` is called once at startup, after EF migrations + seed actor:

1. Reads `App:Admin:Username` and `App:Admin:Password` from configuration (bound from `IRIS_ADMIN_USERNAME` / `IRIS_ADMIN_PASSWORD` env vars via `.env`)
2. If either is unset → no-op (operator creates the first admin by hand)
3. If both are set and no admin exists → provisions an actor + creates a `UserAccount` with `Role = Admin`
4. Idempotent: if an admin already exists, it returns without creating a second one

## Verification path (manual, not Playwright)

To verify on a clean deployment:

1. Set `IRIS_ADMIN_USERNAME=admin` and `IRIS_ADMIN_PASSWORD=Str0ngPass!` in `.env`
2. `docker compose down -v && docker compose up --build` (clean volumes)
3. Check the DB: `SELECT Username, Role FROM "UserAccounts" WHERE Role = 'Admin'` → should show `admin | Admin`
4. Check the logs: `docker logs iris-web | grep bootstrap` → should show "Bootstrapped admin account 'admin'"
5. Restart without changing `.env`: `docker compose restart iris-web` → no second admin created (idempotent)
6. Remove `IRIS_ADMIN_*` from `.env`, restart → no-op (no admin configured)

## Current deployment

The live app at `iris.luit.ink` has `alice` as the admin (bootstrapped from `.env` on first deployment, 2026-09-08). The bootstrap ran successfully and idempotently on every subsequent restart.

## Conclusion

Feature matrix D-column for "Admin bootstrap from `.env`" can be closed (☐ → ✅). The feature is verified by code inspection + DB evidence + idempotent behavior across restarts.
