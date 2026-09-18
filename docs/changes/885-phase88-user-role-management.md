# 88.5 — Admin user role management (promote/demote)

**Phase:** 88.5+ — Remaining Phase 88 gaps
**Date:** 2026-09-12
**Status:** Complete
**Commit:** `07b2945`

## Objective

Close the **user role management** gap from the 88.1 feature-matrix reconciliation: an admin had no way to promote a user to admin or demote an admin back to a plain user. The role was otherwise only set by the `AdminBootstrapper` (first admin at startup) or hard-coded to `User` at registration — there was no runtime control.

## What was built

### Server — store + endpoint

- **`IUserAccountStore.UpdateRoleAsync(Guid id, UserRole role, ct)`** added to `src/Iris.Server.Data/Accounts/IUserAccountStore.cs`, with both implementations:
  - `InMemoryUserAccountStore` — locks the gate, sets `account.Role`, throws `InvalidOperationException` on unknown id.
  - `EfUserAccountStore` — loads the entity, sets `Role = role.ToString()`, `SaveChangesAsync`; throws on unknown id.
  Both follow the existing `UpdatePasswordHashAsync` / `UpdateNotificationsReadAtAsync` mutate-then-save pattern.

- **`POST /local/v1/admin/users/{id}/role`** added to `MapAdminEndpoints` in `apps/Iris.Web/WebAppFactory.cs`, gated with `.RequireAuthorization(p => p.RequireRole("Admin"))`. Body is the new `UpdateRoleRequest { Role }` record (a `string?`, parsed case-insensitively via `Enum.TryParse<UserRole>`). It:
  1. Returns **400** if `Role` isn't `User` or `Admin`.
  2. Returns **404** if no account has the id.
  3. **Last-admin guard:** if the target is currently `Admin` and the request is to demote to `User`, it counts admins via `GetAllAsync`; when `adminCount <= 1` it returns **400** `"Cannot demote the last admin."` so the instance can never be left with no admin.
  4. Otherwise calls `UpdateRoleAsync` and returns `{ success, username, role }`.

### Client — `AdminUsers.razor`

Added a per-row role-toggle button in the Action column, chosen by the row's current role:
- **"Make admin"** for `User` rows → POSTs `{ Role: "Admin" }`.
- **"Demote to user"** for `Admin` rows → POSTs `{ Role: "User" }`.

The handler `ToggleRoleAsync(user, makeAdmin)` POSTs to the endpoint with the cookie-auth `"iris"` client, then:
- On **200**: shows a success banner (`"@handle is now an admin."` / `"@handle is now a user."`) and reloads the user list so the role badge + button update in place.
- On **non-200**: parses the `error` field into a `RoleError` banner (this is how the last-admin guard's message reaches the user).
- On **exception**: a generic retry message.

The existing `RoleLabel` / `RoleBadgeClass` (which key off the `Role` int — `1` = Admin) render the updated state after reload, so no badge changes were needed.

## Live verification (Playwright, per the WASM manual-test policy)

Rebuilt the Docker app (`docker compose build --no-cache iris-web` + `up -d --force-recreate`) and, because the browser's **disk cache** kept serving the old content-hash WASM even after a hard reload / fresh tab (the server correctly 404'd the old hash and 200'd the new `zuxpay8rxp` hash), **restarted the Playwright MCP** (`bash scripts/start-playwright.sh`) to get a truly fresh browser profile. Logged in as `alice` (Admin) via the normal form flow and confirmed the fresh WASM loaded.

| Scenario | Result |
|---|---|
| Promote `andrew` (User → Admin) | ✅ "@andrew is now an admin." — badge flips to **Admin**, button becomes "Demote to user"; DB confirmed `andrew = Admin` |
| Demote `andrew` (Admin → User) | ✅ "@andrew is now a user." — badge flips back to **User**; DB confirmed `andrew = User` |
| Last-admin guard (demote `alice`, the only admin) | ✅ **400** → banner "Cannot demote the last admin."; DB confirms `alice` **stays** `Admin` |
| Console errors | **0 unexpected** (the only logged error was the browser surfacing the intentional 400 from the guard) |

State was restored to the original (only `alice` Admin) after verification.

## Build / test

- `dotnet build -c Release` → 0 warnings, 0 errors.
- `dotnet test -c Release --no-build --filter "Category!=Slow"` → **1724 passed / 1 skip / 0 failed** (all 11 assemblies; `Iris.Server.Tests` passed 1071/0). No existing test implements `IUserAccountStore` as a fake (the one test reference uses DI), so the new abstract method breaks nothing.
- **0 new coded web tests** (WASM manual-test policy — Phase 45+). Verification is the live Playwright pass above.

## Notes / decisions

- **Why a single `POST .../role` endpoint (not separate `/promote` + `/demote`)?** The server-side gate is `RequireRole("Admin")` either way; a single endpoint taking the target role is less surface, reuses one store method (`UpdateRoleAsync`), and makes the last-admin guard a single code path (only the Admin→User direction needs the guard). The client decides which label to show from the current role.
- **Why count admins on demote rather than rely on `AnyAdminExistsAsync`?** `AnyAdminExistsAsync` only says "at least one admin exists" — it can't distinguish "this admin is the last" from "there are others." Counting `GetAllAsync` and checking `adminCount <= 1` is the precise check; the user base is small (local accounts), so a full scan is fine.
- **Stale-cookie caveat (documented, not a bug):** the role claim is baked into the signed-in user's cookie at login. An admin who is demoted keeps their `Admin` cookie claim until they re-login (the WASM client re-resolves via `/local/v1/session` on a fresh sign-in). Server-side `RequireRole` enforcement is the source of truth for endpoint access; this matches the existing model and is out of scope for this slice.
- **Why `UpdateRoleAsync` on the store rather than find-mutate-`CreateAsync`?** The in-memory store returns defensive clones, so a find-mutate-create pattern would not persist (it would treat the row as a duplicate). A dedicated `UpdateRoleAsync` mirrors the other targeted mutators (`UpdatePasswordHashAsync`, etc.) and works identically for both backends.
