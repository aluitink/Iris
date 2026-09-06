# Production App — Web Host Project

> **Level 2.** Parent: [production-app-overview.md](production-app-overview.md). Children: [production-app-web-host-structure.md](production-app-web-host-structure.md), [production-app-ui-guidelines.md](production-app-ui-guidelines.md).

## 1. What this project is

`Iris.Web` is a single ASP.NET Core **Blazor Web App** (the unified Blazor hosting model — server-rendered by default, with interactive components) that, in one process:

- Hosts the ActivityPub server (`Iris.Server`'s `AddActivityPubServer` + `MapActivityPubEndpoints()`), unchanged — federation, WebFinger, NodeInfo, inbox/outbox, communities, media, search, OAuth2 all keep working exactly as they do in `SampleServer` today.
- Hosts the production persistence provider (see [production-app-persistence.md](production-app-persistence.md)).
- Hosts local authentication (see [production-app-authentication.md](production-app-authentication.md)).
- Serves the actual product UI as Razor components rendered **same-origin** — no CORS, no cross-origin proxy fallback needed for the app's own UI (the proxy fallback machinery in `Iris.Client` still matters for *federation* reads of remote servers, but the app's own API calls are same-origin).

This is a deliberate departure from the sample topology (`SampleServer` + `SampleBlazorClient` WASM + `IrisStaticHost` as three separate processes/origins) — that split exists because the *sample* explorer must be able to point at arbitrary remote Iris instances from a browser. A production single-tenant social app doesn't need that flexibility for its own UI; it needs simplicity, same-origin cookies, and one deployable unit. The samples are not being replaced or deprecated by this work — they remain the library's demonstration/test harness.

## 2. Render mode

**Recommendation: Interactive Server render mode for the whole app in the MVP.** Rationale:

- Simplest security model — the signed-in user's session, actor client binding, and any secrets stay server-side; the browser never handles a private key or a bearer token directly.
- Real-time-friendly for free — a SignalR circuit is already present, which is a natural fit for live notification/timeline updates later (Phase C/D — see [production-app-feature-set.md](production-app-feature-set.md)) without adding a second real-time transport (see the important distinction below).
- Lower complexity than mixing render modes (`InteractiveAuto`/`InteractiveWebAssembly` per-component) for an MVP; that's a legitimate later optimization (e.g., a WASM-rendered compose box for perceived snappiness) but not a Day 1 requirement.
- Matches the "single deployable unit" goal — no separate WASM asset publishing/versioning story to get right for the MVP.

**Important distinction — this app has two different "client/server" relationships; don't conflate them:**

1. **Browser ⇄ `Iris.Web` (the Blazor render transport, i.e. what "SignalR" means here).** Interactive Server render mode works by opening a persistent SignalR connection (a "circuit") between the browser tab and the ASP.NET Core process: UI interactions (clicks, keystrokes, form input) go up the circuit, and re-rendered HTML diffs come down it. This is Blazor's own built-in plumbing — it exists automatically the moment `AddInteractiveServerComponents()` / `AddInteractiveServerRenderMode()` is used, not something this plan designs or configures further. **It carries zero ActivityPub semantics** — it's purely "how does this webpage update without a full reload," no different in kind from any other server-rendered SPA framework's live-update transport.
2. **`Iris.Web`'s component code ⇄ `Iris.Server`'s ActivityPub endpoints (the actual AP-native client/server dialect).** This is the real protocol the whole library exists for: an `IActivityPubClient` (from `Iris.Client`) making signed HTTP requests against the `/ap/v1/...` routes, through the same signature/auth/caching pipeline any federated remote server or other ActivityPub client would use. §3 below is entirely about this relationship — it's what the "AP-native interfaces only" constraint ([production-app-overview.md](production-app-overview.md) §3) is protecting, and it is the layer that matters for the "stay AP-native" goal.

SignalR (relationship 1) is never a substitute for, or a bypass of, the AP-native dialect (relationship 2) — they sit at completely different layers and solve unrelated problems. A later "push a live update to the browser when a new activity arrives" feature (Phase C/D) would still *originate* that update by asking `Iris.Server` for it through the AP-native client (relationship 2, e.g. a background poll or an in-process event); it would only *deliver* the already-fetched result to the open tab over the existing circuit (relationship 1) instead of waiting for the user's next page load or manual refresh.

Revisit `InteractiveAuto` (server-first, WASM-cached-for-next-visit) only after the functionality + experience passes are done, if latency on a real deployment justifies it.

## 3. How UI components talk to the ActivityPub layer (the AP-native dialect)

The signed-in user's browser session (an auth cookie, see [production-app-authentication.md](production-app-authentication.md)) maps to a local actor. Components need an `IActivityPubClient` bound to *that* actor's identity to post/follow/like/etc. as them — this is the one and only sanctioned path from the UI down into ActivityPub state; see the distinction drawn in §2 above (this is relationship 2, not the SignalR circuit).

**No new APIs get built for this app.** Every capability the UI needs — including the ones that feel "administrative" (moderation queue, instance settings, notifications) — is served by calling `Iris.Client` against `Iris.Server`'s existing (or client/server-extended) routes, exactly as a federated peer would. If a screen needs something `IActivityPubClient` can't do yet, extend `Iris.Client` with the method (and, if the underlying route doesn't exist, add it to `Iris.Server` following its existing endpoint conventions — `/ap/v1/...` for AP-native, `/local/v1/...` for local-instance-only extensions like the existing media upload/mute/relay routes). A Razor component or code-behind should never issue a raw `HttpClient` call, a direct SQL/EF query, or stand up a new minimal-API/controller endpoint to satisfy a UI need — that would be building a second, UI-only API surface alongside the real one, which is exactly what this constraint rules out.

**Recommendation:** an `IActorSessionAccessor` (scoped DI service, one per circuit) that:

1. Reads the signed-in user's claims (actor IRI + user id) from the `AuthenticationStateProvider`.
2. Loads that actor's `KeyPair`/signing key directly from `IKeyStore` (in-process — no reason to round-trip over HTTP to itself just to fetch a key it already has local access to).
3. Constructs (or returns a cached) `IActivityPubClient` bound to that identity, using the client's existing DI-friendly construction path (`Iris.Client.Extensions` / `IActivityPubClientFactory`).
4. Exposes it to components as `IActorSessionAccessor.Client` (throws/returns null when signed out — components gate on `AuthorizeView` as usual).

This keeps the **UI → `IActivityPubClient` → HTTP → `Iris.Server` endpoints → `IPersistenceProvider`** path fully intact and dogfooded (the UI never reaches around the client into the store interfaces directly), while skipping a pointless self-hosted-loopback HTTP hop for key acquisition. If a future deployment splits the UI and API into separate processes, only `IActorSessionAccessor`'s key-acquisition step needs to change (swap to the existing Basic-auth-fetch-the-actor-doc flow) — everything above it is unaffected.

Anonymous (logged-out) reads — browsing a public profile, a community feed, search — use an unauthenticated `IActivityPubClient` (or plain HTTP), exactly like the sample explorer does today.

## 4. Suggested UI architecture & the shared component inventory

Reuse the shape — and where reasonable, the *code* — of the existing `SampleBlazorClient` explorer components; they're already built, tested, and solve real problems (paging, raw-object display, collection browsing). **Before writing any new Razor component, check the canonical component inventory** — the full list of existing/planned shared components, the "extend, don't fork" rule, the required Loading/Empty/Error/Loaded state contract, and the visual/UX guidelines (spacing & type scale, responsive breakpoints, accessibility baseline) all live in [production-app-ui-guidelines.md](production-app-ui-guidelines.md). This exists specifically so the app doesn't end up with three slightly-different collection-paging components or two different "post card" renderers built by different slices at different times.

Page/route sketch (finalize during implementation): `/`, `/login`, `/register`, `/home` (timeline), `/notifications`, `/u/{handle}` (profile), `/c/{name}` (community), `/c/{name}/create` (new community), `/search`, `/settings`, `/admin` (instance admin — role-gated).

## 5. CSS / design system

The existing sample uses hand-rolled CSS (`wwwroot/css/app.css`), no framework. For the MVP **functionality** pass, reuse that approach (fast, zero dependency). Before the **experience/polish** passes ([production-app-feature-set.md](production-app-feature-set.md) Phase C/D), evaluate adopting a small utility/component CSS framework (e.g., Bootstrap 5 or a lighter classless option) to accelerate consistent styling — this is a Phase C/D decision, not a Day 1 blocker. Record whichever choice is made in the [overview's decisions log](production-app-overview.md#9-decisions-log-fill-in-as-the-agent-resolves-open-questions).

## 6. Testing approach: MCP Playwright, no UI test project yet

The library's own convention is already "UI work is verified with Playwright MCP, not bUnit" for exploratory/fast-moving UI (see `docs/changes/189` and the many `docs/changes/*` entries recording a "Playwright-MCP manual pass" instead of a new test project). This app leans on that convention deliberately, and for longer than the sample did:

- **No bUnit/component test project for `Iris.Web`'s UI is created yet.** The screens, layout, and navigation are expected to change shape repeatedly through the functionality and experience passes ([production-app-feature-set.md](production-app-feature-set.md)); locking that churn behind a component test suite this early would slow down exactly the iteration the MVP needs. Revisit once the UI has been through at least one full experience pass (Phase C) and the team agrees the shape has settled — that decision gets recorded in the [overview's decisions log](production-app-overview.md#9-decisions-log-fill-in-as-the-agent-resolves-open-questions) when made.
- **Every UI slice is verified live, with the MCP Playwright server, as it's built** — both **functionally** (click through the actual flow: register → post → see it in the feed → like it → see the like) and **visually** (take a screenshot, look for layout breaks, overlap, unreadable contrast, obviously wrong states). This mirrors the existing manual-pass write-ups under `docs/changes/` and should be recorded the same way: a short "Manual verification (Playwright MCP)" note in the slice's change doc, listing what was clicked through and what was visually confirmed.
- **Backend/API work keeps the library's existing integration-first convention** ([docs/reference/TESTING.md](../reference/TESTING.md)) — `Iris.Server.Data` (persistence), the new `IUserAccountStore`, and any new/changed endpoints still get real `TestServer`/integration tests, same as the rest of `Iris.Server`. The "no test project yet" carve-out is specifically for the **Blazor component/UI layer**, not the API surface underneath it.
- When a UI test project does get added (post-Phase-C), prefer bUnit for component-level assertions (matching `SampleBlazorClient.Tests`'s existing pattern) and keep Playwright MCP for the full-stack functional/visual pass — they're complementary, not a replacement for each other.

## 7. What "done" looks like for this workstream

- `Iris.Web` builds, runs, and serves both the ActivityPub API (`/ap/v1/...`, `/.well-known/...`) and the product UI from one process/port.
- `IActorSessionAccessor` correctly binds an `IActivityPubClient` to the signed-in user for the lifetime of their circuit, and components use it (never the raw store interfaces) for authenticated writes.
- The reused explorer components render correctly against the new UI's routes/layout.
- Each screen has a recorded Playwright-MCP functional + visual pass (see §6) before being considered complete for its current phase.
- Added to `Iris.slnx`.

See [production-app-web-host-structure.md](production-app-web-host-structure.md) for the concrete project layout and DI wiring order.
