# 131.5b — Disable anti-forgery tokens (development stack)

**Date:** 2026-09-13
**Slice:** 131.5 (user request — "Disable anti-fogery tokens … they cause a lot of greif as we change the implementation and redeploy, we should disable for now.")
**Commit:** `feat(web): disable antiforgery by default in the dev compose stack`

## Problem

The ASP.NET Core antiforgery middleware (added in Phase 94) gates every non-GET request
on a per-session token pair (a `__RequestVerificationToken` form field + a
`.AspNetCore.Antiforgery.*` handler cookie). The token is cryptographically bound to the
**Data Protection key ring**. Every time the `iris-web` container is rebuilt/recreated
during development, the in-memory key ring is regenerated, so any antiforgery token the
browser holds (fetched from `/local/v1/antiforgery` before the rebuild) is now signed with
a key the server no longer trusts. The login/register form POSTs then fail with **400**
until the client re-fetches a fresh token — and the Blazor client only fetches a token at
the moment it submits, so a stale handler cookie from a prior session poisons the request.

In practice this meant: rebuild the container → try to log in → 400 → confused → clear
cookies → retry. A recurring source of greif exactly while the implementation was changing
fast.

## Change

The Phase 94 toggle `Iris:Security:EnableAntiforgery` (env `IRIS_SECURITY_ENABLEANTIFORGERY`)
already exists and, when `false`, registers a `PermissiveAntiforgery` (a no-op `IAntiforgery`)
that wins DI resolution so `UseAntiforgery`'s validation always passes. The **C# production
default in `WebAppFactory` is unchanged (ON)** — a misconfiguration can never silently disable
the protection.

The only change is the **development compose stack**: `apps/Iris.Web/docker-compose.yml` now
defaults the env var to `false`:

```yaml
Iris__Security__EnableAntiforgery: ${IRIS_SECURITY_ENABLEANTIFORGERY:-false}
```

- **Dev (this stack):** antiforgery OFF by default → login/register form POSTs are not gated
  on a per-session token; no stale-token 400s across container rebuilds.
- **Re-enable in dev:** set `IRIS_SECURITY_ENABLEANTIFORGERY=true`.
- **Production:** the C# default stays ON; a real deployment must run with antiforgery ON
  (the compose default is only for the local development stack).

## Why the dev stack, not the C# default?

Flipping the C# default to OFF would break the existing web-test contract
(`AntiforgeryToggleTests.UnsetOrBlank_Enabled`,
`AntiforgeryDisabledIntegrationTests.Enabled_Default_TokenlessLoginPOST_IsRejected400`) and the
many `*IntegrationTests` that boot the app with default config and POST through signed
ActivityPub clients (they rely on the `UseAntiforgery` middleware being enabled). The user's
ask is specifically about the *development* redeploy pain ("as we change the implementation
and redeploy"), so scoping the override to the dev compose stack satisfies it with **zero
C# behavior change and zero web-test change**.

## Verification (live, Playwright)

- `docker compose config` clean; container recreated with `Iris__Security__EnableAntiforgery=false`
  confirmed via `printenv`.
- **Tokenless login POST** (no `__RequestVerificationToken`, no handler cookie): `302` (reaches
  the login handler, redirects on bad credentials) — previously `400`.
- **Real login** as `andrew` / `Password1` via the browser form: redirects to `/home`, signed in.
- **Console errors:** 0.
- **Web tests:** `dotnet test tests/Iris.Web.Tests` → 98 passed, 0 failed, 0 skipped (no change —
  the C# default and all test harnesses are untouched).

## Out of scope

- Re-enabling antiforgery in production / a production-ready compose override (the C# default
  already handles this; a real deployment simply does not set the dev `false` default).
- A client-side fix that re-fetches a fresh token on 400 (a future hardening item, not needed
  while the dev stack runs with antiforgery off).
