# 64.4 — public-feed double-fetch on mount (request spam, topic #6)

Phase 64 continues cutting the *number* of calls the WASM client makes. 64.4 is
the next slice: eliminate the **double initial fetch** of the public feed on the
root landing page (`/`) — the same `GET /ap/v1/public/feed?limit=20` fired
**twice** during a single page mount.

## Problem (620 tracker topic #6)

On `/` (the authless landing — hero + public timeline), the public feed's first
page was fetched **twice** on mount. Both requests were identical
(`GET /ap/v1/public/feed?limit=20`), a pure duplicate of the initial load.

## Root cause

`Home.razor` renders the public feed's `<PagedCollection>` **keyless**, inside
`<AuthorizeView><NotAuthorized>`, gated by `@if (Session.PublicFeedIri is { }
feedIri)`. `Home.OnInitializedAsync` awaits `Session.EnsureReadyAsync()` — an
async auth-state resolution that re-renders the page once it completes. On that
re-render the parent's `<NotAuthorized>` child content is re-evaluated, and the
keyless `PagedCollection` is **disposed and recreated** (a fresh component
instance). The fresh instance's `_loadedFor` field is `null`, so its
`OnParametersSet` sees `CollectionIri != _loadedFor` and re-fires the initial
first-page fetch — the same feed loaded a second time.

The home timeline already solved this exact problem: `HomeTimeline.razor` puts a
stable `@key` on its `<PagedCollection>` precisely to stop the parent from
disposing/recreating it on a re-render, so its `_loadedFor` survives and the
initial load fires once. The public feed's card simply lacked that key.

## Fix

`apps/Iris.Web.Client/Components/Pages/Home.razor`:

- Added a stable `@key="@PublicFeedKey"` to the public feed's
  `<PagedCollection>`, with `PublicFeedKey` a `const string` field
  (`"public-feed"`). The root page renders exactly one collection (the instance
  public feed), so the key is a constant: it stops the parent from disposing and
  recreating the card when the page re-renders after `OnInitializedAsync` awaits
  the session. With a stable key, the same component instance is kept, its
  `_loadedFor` is retained, and the initial first-page load fires exactly once.
- This mirrors the proven pattern in `HomeTimeline.razor` (a constant
  `@key="@FeedKey"` on the home feed's `PagedCollection`).

**A Razor-generator limitation, worked around:** `@key="public-feed"` (a string
*literal*) and `@key` on any element inside this file's `@if`/`<NotAuthorized>`
block do **not** compile under this SDK's Razor source generator (they emit a
malformed `BuildRenderTree` lambda — `CS1662`, plus a cascade of `CS0246`
`Inject`/`NavigationManager` errors). The working form (the one used by
`Profile.razor` and `HomeTimeline.razor`) is `@key="@SomeCsharpVariable"` — the
key value must be a **C# variable reference**, not a string literal. The fix
therefore adds a `const string PublicFeedKey` field and keys the card with
`@key="@PublicFeedKey"`, which compiles cleanly.

No server change. No `PagedCollection.razor` change (the component's existing
`_loadedFor` re-load guard is correct; the bug was the parent recreating the
component, which `@key` prevents).

## Verification

**Build + fast suite:** `dotnet build` clean (0 warnings, 0 errors,
`TreatWarningsAsErrors` on). `Iris.Web.Tests` 62/62. (WASM-side change — the
public feed is anonymous, so it is Playwright-verified on the live app per the
Phase-45+ web-test policy; no new coded web test.)

**Live (fresh origin `:8120`, anonymous — cookies cleared, `GET /`):**

- The public feed's first page (`GET .../ap/v1/public/feed?limit=20`) was
  fetched **exactly once** on mount (network panel: a single `public/feed`
  request). Before the fix it fired **twice**.
- The landing renders correctly: hero + "Public timeline" heading + the feed
  card's Refresh button. The only console errors are a CORS `ERR_FAILED` on the
  feed fetch — a **test-environment artifact** of serving from
  `http://localhost:8120` while `IRIS__ADVERTISEBASE` is the external
  `https://iris.luit.ink` (the feed IRI is the external FQDN, so the browser
  blocks the cross-origin fetch). That artifact is unrelated to this change and
  does not occur under the app's real (same-origin) deployment.

## Remaining Phase 64 topics (separate slices)

- **Optional minted-id extension:** the server could render the minted
  Like/Announce activity IRI on the object so the client never needs the
  per-engaged-card id-recovery walk (the residual 5+5 from 64.2). A bounded
  server addition + a new client extension read. Deferred.
- **#7 (deleted-account avatar 410s):** unchanged, separate slice.
