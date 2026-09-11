# 80.1/80.2 — Phase 80 full-route defect hunt + blocker/bug-class fixes

**Slice:** 80.1 (defect hunt) + 80.2 (fix blocker/bug-class findings)
**Status:** DONE — build clean, 0 test failures, every fix verified from a clean entry on the live Docker app.
**Companion tracker:** `docs/changes/800-phase80-bug-hunt-tracker.md`

## What was done

### 80.1 — Full-route defect hunt (18 routes, all visited)

Every route in `App.razor`'s `<Routes>` was opened from a **fresh entry** (log out, clear
cookies, re-enter) on the live Docker app (`irisweb-iris-web-1`, port 8088), signed in as the
non-admin account `andrew`, plus an authless pass. For each page: verified the primary action,
read the console (`browser_console_messages level=error`), and recorded the data state (empty /
normal / broken / loading). Findings were logged to the 800 tracker with a severity (S1 blocker,
S2 bug, S3 polish) and a repro path.

**18/18 routes visited:** Home, Login, Register, Compose, Notifications, Directory, Communities,
Object detail, Actor profile, Community detail, Search, Profile, Settings, Admin Dashboard,
Admin Users, Admin Moderation, Admin Instance.

**5 findings logged (P-001 … P-005).** No S1 blockers. 3 bug-class (P-001, P-002, P-005), 1
UX-polish (P-004), 1 product decision (P-003, wontfix-by-design).

### 80.2 — Bug-class fixes (all verified)

**P-001 (S2, bug) — `ObjectView.razor` "in reply to" parent link rendered literal `?? parent.Value`.**
`ObjectView.razor:58` wrote `@_parentPreview ?? parent.Value`. In Razor a `@simpleIdentifier`
expression **ends at the identifier** — the `?? parent.Value` that follows is emitted as **literal
text**, not a C# null-coalescing. When `_parentPreview` is null (the feed card's async
parent-preview fetch hasn't resolved) the card rendered the empty preview + the literal string
`?? parent.Value`. **Fix:** wrap in parens → `@(_parentPreview ?? parent.Value)`.
**Verified (authless `/`):** the reply post's parent link now shows the **IRI fallback**
(`https://iris.luit.ink/ap/v1/u/andrew/notes/06G8Y1CRRSX1TSGF9BZ6QT98DR`); no `parent.Value` /
`?? ` substring in the body.

**P-002 (S3, bug) — `Compose.razor` community label always appended literal `?? "community"`.**
`Compose.razor:48` wrote `@CommunityName ?? "community"` — same Razor gotcha: `@CommunityName`
is a simple-identifier expression and `?? "community"` is literal text that **always** follows the
name (not only when the name is null). On `/compose?community=…/test-community-541` the label
rendered `Posting to 54.1 Test Community ?? "community"`. **Fix:** `@(CommunityName ?? "community")`.
**Verified (signed-in `/compose?community=…`):** the label now reads **"Posting to 54.1 Test
Community"** cleanly; no `?? ` substring.

**P-005 (S2, bug) — admin pages redirected a signed-in non-admin to `/login` instead of showing the friendly message.**
All 4 admin pages (`AdminDashboard`, `AdminUsers`, `AdminModeration`, `AdminInstance`) carried
`@attribute [Authorize(Policy = "Admin")]`. `App.razor` wraps the router in a global
`AuthorizeRouteView` with `<NotAuthorized><RedirectToLogin/></NotAuthorized>`; for a signed-in
non-admin (who passes base sign-in but fails the Admin policy) the global route-level
authorization rendered `NotAuthorized` → **redirect to login** before the WASM's
`AdminGuard.AccessDenied` friendly-message path could run — making that path dead for the
non-admin case. **Regression from the 62 baseline** (where andrew saw the "not an admin" message).
The attribute is also redundant: the admin **API** endpoints (`/local/v1/admin/*`) are already
`RequireRole("Admin")`-gated server-side (401/403 handled gracefully by the WASM). **Fix:**
removed the `@attribute [Authorize(Policy = "Admin")]` from all 4 admin pages; rely on
`AdminGuard.AccessDenied` (client-side role check) + the server-side `RequireRole("Admin")` on the
admin API endpoints.
**Verified (signed-in non-admin andrew):** `/admin/dashboard` stays on the page (no redirect) and
renders **"This page is only available to instance administrators. If you believe you should have
access, contact your instance admin."**; `/admin/users` also stays on the page (no redirect).

## Not fixed (by design / deferred)

- **P-003 (S3, UX) — wontfix (by design):** public deep links (`/object?iri=…`, `/actor?iri=…`,
  `/community?iri=…`) require sign-in. Root cause is the **global** `AuthorizeRouteView` in
  `App.razor` making the whole app sign-in-required by design (only `/`, `/login`, `/register` are
  excluded). This is a deliberate product decision (a sign-in-required app), not a per-page defect.
  If anonymous public browsing is ever wanted it's a global routing change (opt specific pages out
  of `AuthorizeRouteView`), a product decision, not a bug.
- **P-004 (S3, UX) — deferred to 80.3:** on the object-detail page, a post whose `inReplyTo` is a
  raw IRI (not a fetched object) renders the raw IRI as the "in reply to" link text (no
  preview/handle). The public-timeline feed card handles this gracefully (IRI fallback, fixed in
  P-001); the object-detail branch (`ObjectView.razor` ~line 329) is a smaller UX gap — polish,
  not a defect. To be improved in 80.3.

## Verification

- **Build:** `dotnet build apps/Iris.Web/Iris.Web.csproj -c Release` → **0 warnings, 0 errors**.
- **Tests:** `dotnet test -c Release` → **0 failed** (1619 passed, 1 skipped across all projects).
  One `Iris.Server.Tests` failure in a single full-suite run was **re-run in isolation and passed
  (0 failed)** — a timing/contention flake, not a regression (the 80.2 changes were client-only
  razor pages, which cannot affect server tests).
- **Live re-verification (clean entry each):** the WASM client was republished (publish dir
  deleted + `dotnet publish`) and copied into the running container (`docker cp
  apps/Iris.Web.Client/publish/wwwroot/. irisweb-iris-web-1:/app/wwwroot/` +
  `docker restart irisweb-iris-web-1`). Each of P-001, P-002, P-005 was re-verified from a clean
  entry (log out + clear cookies + re-login) on the republished build, all console-clean.

## Files changed

- `apps/Iris.Web.Client/Components/ObjectView.razor` — P-001: `@(_parentPreview ?? parent.Value)`.
- `apps/Iris.Web.Client/Components/Pages/Compose.razor` — P-002: `@(CommunityName ?? "community")`.
- `apps/Iris.Web.Client/Components/Pages/AdminDashboard.razor` — P-005: removed `[Authorize(Policy="Admin")]`.
- `apps/Iris.Web.Client/Components/Pages/AdminUsers.razor` — P-005: removed `[Authorize(Policy="Admin")]`.
- `apps/Iris.Web.Client/Components/Pages/AdminModeration.razor` — P-005: removed `[Authorize(Policy="Admin")]`.
- `apps/Iris.Web.Client/Components/Pages/AdminInstance.razor` — P-005: removed `[Authorize(Policy="Admin")]`.
- `docs/changes/800-phase80-bug-hunt-tracker.md` — new: the full-route defect-hunt tracker.
- `docs/changes/801-phase80-defect-hunt-fixes.md` — this doc.
- `PLAN.md` — Phase 80 now ACTIVE (Up Next / Now / Active Slice updated).

## Test-debt log

- **Web tests:** none deleted, none skipped. The 63 `Iris.Web.Tests` all pass unchanged (the razor
  changes were client-only rendering fixes; no web test asserted the buggy literal text). No web
  test exceeded 15s.
- **Core/server tests:** none broken by these client-only changes. The single flaky
  `Iris.Server.Tests` failure (timing/contention) passed on re-run in isolation.
