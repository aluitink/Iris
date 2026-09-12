# 88.4 — Make actor detail pages public

**Phase:** 88.4 — Close the "actor detail requires sign-in" gap
**Date:** 2026-09-12
**Status:** Complete
**Commit:** `1f92eb1`

## Objective

The 88.1 feature matrix listed actor profile pages as **public, no auth required** (box "Profiles & federation"), but `ActorDetail.razor` was gated with `@attribute [Authorize]` and showed *"Sign in to view actors."* to logged-out visitors. This slice removes the gate so anonymous visitors can open any actor's profile and read their posts/followers/following, while keeping the follow/moderation actions behind sign-in.

## What was built

### `apps/Iris.Web.Client/Components/Pages/ActorDetail.razor`

- **Removed** the `@attribute [Microsoft.AspNetCore.Authorization.Authorize]` line and the `@if (Client is null)` "Sign in to view actors" branch. The page now renders for anonymous visitors.
- **Anonymous actor-document load:** `OnParametersSetAsync` first tries `Ui.GetActorAsync(iri)` (the signed-in per-circuit actor cache). When signed out that returns null (no `IActivityPubClient`), so it falls back to `FetchActorAnonymousAsync(iri)` — a plain same-origin `GET` of the public actor IRI, deserialized with `ActivityJson.Deserialize<IObject>(json)`.
- **Anonymous counts:** `LoadCountsAsync` reads `followers`/`following` `totalItems` over plain HTTP (`GetFirstPageTotalAnonymousAsync`) when signed out, so the tab labels show `(N)` for anonymous visitors.
- **Follow / Moderate gated on sign-in:** the header renders `<FollowButton>` + `<ModerationActions>` only when `Session.IsSignedIn`; otherwise it shows a muted *"Sign in to follow or moderate."* hint. `IsSelf` and the community join-request tab are unchanged (they already no-op when unsigned / not a moderator).
- **Tabs:** the outbox/followers/following `<PagedCollection>`s now receive `AnonymousHttpClient="Http"` (the `iris` client) so they can page collections when signed out, and `ShowRefreshButton` is `@(Client is not null)` (the refresh re-signs, so it is hidden when unsigned). The `Http` property is `IHttpClientFactory.CreateClient("iris")`, injected via a new `IHttpClientFactory` dependency.

### `apps/Iris.Web.Client/Program.cs`

The plain `iris` `HttpClient` previously dialed same-origin only. Absolute FQDN IRIs (the instance's advertised base, e.g. `https://iris.luit.ink`) issued from the browser's dial host (e.g. `http://localhost:8088`) are **cross-origin** and CORS-blocked — which broke every anonymous read. The `iris` client is now built with `.ConfigurePrimaryHttpMessageHandler(() => new SameOriginApHandler(new HttpClientHandler(), canonicalAdvertiseBase, serverBaseUri))`, the same rewrite the signed session uses, so FQDN-addressed reads are rebuilt on the browser's same-origin dial host. Pass-through when no advertised base is configured.

## Key decisions

- **Reuse `SameOriginApHandler` on the `iris` client** rather than hand-rolling a per-call URL rewrite. One handler covers the actor document, the three collection tabs, and any future anonymous read; it is a documented no-op for single-instance deployments (no advertised base). This mirrors the existing owner-only read path in `IActorSessionAccessor`.
- **Keep follow/moderate behind sign-in** (with a hint) rather than hiding the page. The matrix calls the *page* public; the *actions* still require an authenticated actor, so the page is viewable but not actionable when anonymous.
- **`ShowRefreshButton` hidden when signed out.** The refresh button re-fetches through the signed client pipeline; without a signed-in session there is no client to refresh through, so the button is hidden to avoid a dead control.

## Verification (manual — WASM manual-test phase, no new coded tests)

Live Docker app (`apps/Iris.Web/docker-compose.yml`, host :8088), fresh Playwright context (no browser cache):

- `GET /actor?iri=https://iris.luit.ink/ap/v1/u/verifier87` (signed out) → renders h1 `verifier87`, the `ActorProfile` (name "Verifier 87"), tabs **Posts / Followers (0) / Following (1)**, and *"Sign in to follow or moderate."* — **0 console errors**.
- `GET /actor?iri=https://iris.luit.ink/ap/v1/u/andrew` (signed out) → **Posts** tab renders real content notes (reply cards with author, body, reply-to, mentions), **0 console errors**.
- Following tab (anonymous) loads and lists followed actors.
- Existing build/test baseline unchanged: `dotnet build -c Release` 0 warn / 0 err; `dotnet test -c Release --no-build --filter "Category!=Slow"` green (1724 passed / 1 skip, plus the known transient `Iris.Server.Tests` parallelism flake that passes when run alone).

## Notes

- The stale-WASM rebuild trap applied: the `BuildAndCopyClient` target only re-publishes when `publish/.../blazor.webassembly.js` is *missing*, and the `wwwroot` copy uses `SkipUnchangedFiles`. The Dockerfile's build stage already `rm -rf`s the stale `publish/`+`bin/`+`obj/` and re-publishes, so a `docker compose build --no-cache iris-web` produces the fresh WASM; verification used a fresh browser context to avoid the content-hash WASM cache.
