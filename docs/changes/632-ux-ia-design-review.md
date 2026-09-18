# 63.2 — UI/UX design & information-architecture review

Phase 63.2 is the design-thinking half of Phase 63: now that the nit pass
(63.1) is done, evaluate the app's **information architecture** — is the data
well organized, readable, and functional? — decide which findings are worth a
structural change, and spec/implement them.

Method: live app on `:8090` (same-origin, DB-backed), signed in as `andrew`
(User) and `alice` (Admin). Inspected the nav, footer, root `/` route, and the
`/home` timeline structure; computed-style + DOM checks. (Fresh origin `:8090`
was required because the Playwright MCP browser profile caches the Blazor WASM
by content hash — a prior build's `Iris.Web.Client.3siymajle0.wasm` was being
served from cache against the new on-disk `141f3m2u0c.wasm`; a new origin
bypasses that.)

## IA findings

| # | Area | Finding | Severity | Decision |
|---|------|---------|----------|----------|
| IA-01 | Root `/` route | For a **signed-in** user, the domain root (the most-visited URL) is a **dead-end interstitial** — "You're signed in. Go to your timeline →" + a few quick links — rather than the feed. The real timeline at `/home` has proper structure (H1 "Home timeline", Refresh, page title "Home · Iris"). A user typing the domain or hitting the root after login lands on a near-empty page and has to click through. | **med** | **Fix in-slice (IA-01).** Redirect signed-in users from `/` to `/home` in `Home.razor`'s `OnInitializedAsync` (after `Session.EnsureReadyAsync()`). The authless landing (hero + Sign in / Create + Public timeline) is unchanged. The old interstitial markup becomes a transient "Taking you to your timeline…" fallback that only paints for the frame before the redirect lands. |
| IA-02 | Footer | The footer shows **Admin / Moderation / Dashboard links to every user**, including regular (non-admin) users. Clicking them as a non-admin bounces to `/login` — confusing for a user who is already signed in. (Gated server-side, so no security issue — purely a misleading IA signal: users see controls they can't use.) | low | **Fix in-slice (IA-02).** In `MainLayout.razor`, wrap the three admin links in `@if (IsAdmin)` where `IsAdmin => Session.IsSignedIn && Session.Role == AdminGuard.AdminRole`. Reuses the existing `AdminGuard.AdminRole` constant. |
| IA-03 | Main nav | The top nav is a **flat row of 9 links** with no grouping: actions (New post, Log out), navigation (Home, Notifications, Directory, Communities), and account (Profile, Settings) are interleaved. At 1024px it still fits (636px wide, `flex-wrap: wrap`, no overflow); mobile collapses to an aria-labelled "Menu" hamburger. | low | **Defer — acceptable as-is.** It fits at all desktop widths and the mobile hamburger already groups it. A visual grouping (e.g. a divider before the account links, or a right-aligned account cluster) is a nice-to-have, not a defect. No slice; revisit only if the nav grows past ~11 links. |

## Implemented (verified live)

### IA-01 — root redirect for signed-in users

`apps/Iris.Web.Client/Components/Pages/Home.razor`:
- Injected `NavigationManager Nav`.
- In `OnInitializedAsync`, after `await Session.EnsureReadyAsync()`:
  `if (Session.IsSignedIn) { Nav.NavigateTo("/home", forceLoad: false); return; }`
  so a signed-in user is sent straight to their timeline.
- The `<Authorized>` markup is reduced to a transient "Taking you to your
  timeline…" line (it only paints for the frame before the redirect lands).
- The `<NotAuthorized>` branch (hero + Public timeline) is unchanged.

Verified live on `:8090`:
- `andrew` (User): login lands `/` → **auto-redirects to `/home`** (title
  "Home · Iris"). ✓
- `alice` (Admin): login lands `/` → **auto-redirects to `/home`**. ✓
- Signed-out `/`: still shows H1 "Iris", Sign in + Create an account, H2
  "Public timeline" (public feed). ✓ (authless landing unchanged)

### IA-02 — hide admin footer links from non-admins

`apps/Iris.Web.Client/Components/Layout/MainLayout.razor`:
- Added `@using Iris.Web.Client.Accounts`, injected `IActorSessionAccessor Session`.
- Footer admin links now wrapped in `@if (IsAdmin)`.
- `private bool IsAdmin => Session.IsSignedIn &&
   string.Equals(Session.Role, AdminGuard.AdminRole, StringComparison.Ordinal);`

Verified live on `:8090`:
- `andrew` (non-admin): footer = **NodeInfo, WebFinger, Status** (admin links hidden). ✓
- `alice` (admin): footer = NodeInfo, WebFinger, Status, **Admin, Moderation, Dashboard**. ✓
- Signed-out: footer = NodeInfo, WebFinger, Status (no admin links — correct). ✓

## Reviewed — no structural change needed

- **`/home` timeline structure** is sound: H1 "Home timeline", Refresh control,
  page title "Home · Iris", 20 note cards. No H1/hierarchy gap (the H1 gap was
  only on the root interstitial, now gone via the IA-01 redirect).
- **Nav density / hierarchy** (IA-03): acceptable; fits at all widths.
- **Footer technical links** (NodeInfo / WebFinger / Status): appropriate for
  all users (public instance metadata), kept for everyone.
- **Content width / centering**: `main` is `max-width:720px; margin:0 auto`
  (confirmed in 63.1) — readable line length, no change.

## Console notes (not from this slice)

- The authless public feed on `:8090` logs 2 CORS errors fetching
  `https://iris.luit.ink/ap/v1/public/feed?limit=20` (advertised base is
  cross-origin to the dev origin). This is **pre-existing** (same class of
  advertise-base/CORS noise noted in the 621 closeout), appears on the unchanged
  authless landing, and is not introduced by IA-01/IA-02.

## Open / deferred to later phases

- **IA-03 (nav grouping):** deferred — see decision above.
- **Avatar 410 noise** on deleted-account notifications: already logged to the
  64 request-spam tracker (630 tracker "Notes / deferred"); not part of this IA
  review.
