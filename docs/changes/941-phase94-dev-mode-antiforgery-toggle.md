# 94.1 — Development-mode antiforgery toggle

**Status:** COMPLETE
**Phase:** 94 — Development mode anti-forge tokens

## Objective

The user reported (PLAN.md Up Next 94): *"Create a way to configure [and] disable the anti-forge
tokens, it seems to cause some caching issues, ideally when we are playwright testing, we would
disable this to ensure we don't get stuck with a stale anti-forge token."*

The "anti-forge tokens" are ASP.NET Core's **antiforgery tokens** (`IAntiforgery` /
`UseAntiforgery`). The login/register forms are traditional HTML POSTs that must carry a valid
per-session antiforgery token (fetched from `GET /local/v1/antiforgery` into the
`__RequestVerificationToken` hidden field). Those tokens are signed with the app's **Data
Protection key ring**. When the key ring changes (a container rebuild without the persisted key
volume, a dev run with ephemeral keys, etc.) previously-issued tokens become **stale** and the
login/register forms fail with a **400** until the token is re-fetched — which is exactly the
"stuck with a stale anti-forge token" pain during Playwright testing.

Goal: a config flag to **disable antiforgery validation** for development / Playwright testing,
defaulting to **enabled** (production unchanged).

## What was built

A single config toggle, `Iris:Security:EnableAntiforgery` (env `IRIS_SECURITY_ENABLEANTIFORGERY`),
bound in `WebAppFactory.ConfigureServices`. Default (unset / any non-`false` value) → antiforgery
validation ON. Set to `false` (case-insensitive) → validation OFF.

### Key types

- **`WebAppFactory.EnableAntiforgeryConfigKey`** (`"Iris:Security:EnableAntiforgery"`) — the config
  key constant.
- **`WebAppFactory.IsAntiforgeryEnabled(IConfiguration)`** (`internal`) — pure config-parsing logic.
  Returns `true` unless the value parses to `false`. Unset/blank and any unparseable value (a typo,
  `"0"`, `"disabled"`) fail **closed** to enabled — a misconfiguration can never silently disable the
  protection.
- **`WebAppFactory.PermissiveAntiforgery`** (`private sealed class`, nested in `WebAppFactory`) — a
  no-op `IAntiforgery` that always reports a request valid (`IsRequestValidAsync` → `true`,
  `ValidateRequestAsync` → completed task) and issues dummy (non-crypto) tokens. Registered in place
  of the real antiforgery service when the flag is off.

### Wiring

In `ConfigureServices`:

```csharp
builder.Services.AddAntiforgery();               // always (endpoints carry antiforgery metadata;
                                                 // the /local/v1/antiforgery endpoint depends on it)
if (!IsAntiforgeryEnabled(builder.Configuration))
{
    builder.Services.AddSingleton<IAntiforgery, PermissiveAntiforgery>();  // wins DI resolution
}
```

In `ConfigurePipeline`, `app.UseAntiforgery()` is **always** applied (unchanged). The middleware
resolves `IAntiforgery` from DI; when disabled it resolves `PermissiveAntiforgery`, whose
`IsRequestValidAsync` always passes — so POSTs go through without a valid token.

### Why the middleware is always applied (design decision)

The first implementation attempt simply skipped `UseAntiforgery()` when the flag was off. That
**broke**: the POST endpoints (`/login`, `/register`, `/logout`, the admin/account POSTs) carry
antiforgery **metadata** by default, and ASP.NET Core throws
`"Endpoint ... contains anti-forgery metadata, but a middleware was not found"` when the middleware
is absent. The correct approach is to keep the middleware applied and swap the *validator* for a
permissive no-op. This also keeps the `/local/v1/antiforgery` token-issuance endpoint working
(the client can fetch a token harmlessly); only the validation is a no-op.

### Operator surface

- `docker-compose.yml`: new `Iris__Security__EnableAntiforgery: ${IRIS_SECURITY_ENABLEANTIFORGERY:-}`
  env var (default empty → enabled). Commented with a "NEVER set to false in production" warning.
- `.env.example`: documented the `IRIS_SECURITY_ENABLEANTIFORGERY` variable with the same warning.
  (The git-ignored real `.env` is unchanged.)

## Verification

**Build:** `dotnet build -c Release` → 0 warnings / 0 errors (`TreatWarningsAsErrors` on).

**New coded tests (17):**

- `AntiforgeryToggleTests` (14 unit tests) — pins the `IsAntiforgeryEnabled` decision logic:
  unset/blank → enabled; `"true"`/`"1"`/`"yes"` → enabled; `"false"`/`"False"`/`"FALSE"` → disabled;
  unparseable (`"off-please"`/`"0"`/`"disabled"`) → enabled (fail closed).
- `AntiforgeryDisabledIntegrationTests` (3 integration tests) — boots the **real** production service
  graph (`WebAppFactory.ConfigureServices`) + the real `UseAntiforgery` middleware on a `TestServer`
  and exercises the real login endpoint end to end:
  - **Enabled (default):** tokenless `POST /login` → **400** (rejected by antiforgery).
  - **Disabled:** tokenless `POST /login` → **302** redirect (passed the permissive middleware,
    reached the handler; bad creds → redirect back with error).
  - **Disabled:** `GET /local/v1/antiforgery` still returns a token (endpoint works).

**Full suite:** `dotnet test -c Release --filter "Category!=Slow"` → green on the load-bearing tests
(the only intermittent failure is the pre-existing flaky
`DuplicateInboundDeliveryIdempotencyIntegrationTests.RedeliveredFollow_IsRecordedExactlyOnce_ExactlyOneAccept`,
a server federation idempotency test that is load-sensitive and passes in isolation — unrelated to
this change, which touches only `Iris.Web` host + tests).

**Live Playwright verification (the feature's actual purpose):**
1. Set `IRIS_SECURITY_ENABLEANTIFORGERY=false` in `.env`, rebuilt the Docker image, restarted.
2. `curl` tokenless `POST /login` (correct password) → **302 to `/`** (logged in with **no**
   antiforgery token — the stale-token 400 is gone).
3. Playwright browser flow: navigated to `/login`, filled handle `andrew` + password, clicked
   **Sign in** → landed on `/home` (logged-in home). **0 antiforgery-related console errors** (only
   a pre-existing remote-federation proxy 403 for `ursal.zone`, documented in Phase 90).
4. Reverted the flag (removed from `.env`), restarted: tokenless `POST /login` → **400** again
   (antiforgery back to the secure production default). Confirms the toggle works both ways.

## Notes / follow-ups

- This is a **development/Playwright-only** convenience. The default is secure (enabled); the flag
  must **never** be set to `false` in a production deployment (the docker-compose + .env.example
  comments both say so).
- The underlying "stale token" root cause is a **Data Protection key ring** change on rebuild. The
  production docker-compose already persists the key ring on the `iris-dp-keys` volume (Phase 48.2),
  so production deploys do not hit the stale-token issue. This toggle is for dev/test environments
  where keys are ephemeral.
- No WASM/client changes; the client's `Login.razor`/`Register.razor` still fetch + embed a token
  (harmless when validation is off — the permissive validator ignores it). No new NuGet packages.
