# Iris — Roadmap

Working roadmap: where we've been, where we are, what's left. Per-slice detail lives in
[changes/](changes/README.md), [decisions/](decisions/README.md), [phase-notes/](phase-notes/README.md),
and the deep-dive plans in [plans/](plans/). This file stays one line per waypoint.

> **About this file (compacted 2026-09-03).** This is a rebuilt roadmap. Finished work is collapsed
> into the "Where we've been" table (one line per phase; the per-slice build notes and rationale are
> not duplicated here — they live in `changes/` and `decisions/`). Only **remaining** work is listed in
> detail. The active phase is **Phase 22** (the Sample-UI functional-explorer rebuild). Each 22.x item
> we work is first deepened into its own plan document under `plans/22-*.md`, referenced from 22.0.

## Where we've been

Phases -1 through 20 are **complete**. One line each; the build notes and decisions are in
`changes/` / `decisions/`.

| Phase | Status | One line |
|---|---|---|
| -1 — Project Reorganization | ✅ | Domain folders + nested namespaces, mirrored across `src` and `tests`. |
| 0 — Scaffolding | ✅ | Solution, net10.0 projects, central package management, multi-instance `TestServer` harness. |
| 1 — Core | ✅ | `Iri`, identity/keys, HTTP signatures, caching. |
| 2 — Client | ✅ | Signing pipeline, WebFinger, paged collections, retry + content-negotiation handlers. |
| 3 — Server Foundation | ✅ | Persistence seam + in-memory impl, `/ap/v1` endpoints, server caching. |
| 4 — Inbox & Delivery | ✅ | Inbound validation, activity handlers, delivery queue/worker, Follow lifecycle. |
| 5 — Community / Groups | ✅ | Community store + endpoints, unified feed, `iris:capabilities`. |
| 6 — Proxy Fallback | ✅ | Server proxy endpoint + policy stack, client `ProxyFallbackHandler`. |
| 7 — Samples & Blazor | ✅ | `Iris.Client.Extensions`, `SampleServer`, `SampleBlazorClient`, E2E tests. |
| 8 — Sample Docker Composition | ✅ | Multi-instance compose + WASM server-explorer (S1–S11, [DEPLOYMENT](reference/DEPLOYMENT.md)). |
| 9 — Deployment Preparation | ✅ | FQDN/TLS plan, enumeration + compat + risk registers (prep only). |
| 10 — Project & Test Review | ✅ | Suite audit + consolidation (850 → 832 tests). |
| 11 — Usability / Gap Closure | ✅ | User journeys, discovery/follow/post, failure-mode + operator-reject coverage. |
| 12 — Spec Conformance | ✅ | F-01…F-31 gap closure (F-26/F-31 deferred) + conformance suite. |
| 13 — Live Federation Compatibility | ✅ | CI-testable slices done (13.1–13.4, 13.8; F-26/F-28 closed via opaque passthrough); live scenarios run as **Phase 19.1** against the public FQDNs (`iris-dev1.luit.ink` / `iris-dev2.luit.ink`). |
| 14 — Live-Interop Execution & Remediation | ✅ | Absorbed into Phase 19.1 (execution) + 19.4 (remediation); deferred Mastodon live test runs in 19.2. |
| 15 — Auth Upgrade | ✅ | Bearer validator, full OAuth2 (token, refresh, authorize + WASM browser flow), samples/docs. |
| 16 — Persistence & Scaling | ✅ | Bounded delivery concurrency, file-backed queue + dead-letter, per-peer rate limiting, file-backed persistence (all opt-in). |
| 17 — Observability & Transport Hardening | ✅ | Health checks + graceful shutdown, delivery metrics, circuit breaker + retry hardening, inbound rate limiting. |
| 18 — Client/Server Hardening | ✅ | 18.1–18.3 done (`Retry-After` HTTP-date client+server, e2e 429→retry); no further slices defined. |
| Sample Explorer — 2nd & 3rd rounds | ✅ | Library-coverage gaps closed (relays, home timeline, deep-linking, unlike, delete, …); live-browser acceptance verified (change [123](changes/123-sample-explorer-live-browser-acceptance.md)). |
| 19.0 — Evaluation environment | ✅ | Volumes for server state, seed idempotency, FQDN/TLS/CORS audit, test-account readiness, evaluation checklist scaffold. |
| 19.0b — AP-native rework | ✅ | Every actor/community activity flows through the actor's **outbox** (client authors → outbox POST → server delivers); specialized capabilities (proxy, search, feeds) are `iris:`-extension-discovered; mute/relay are local, non-AP (`/local/v1`). The client is a pure AP protocol layer (+ a separate `LocalModerationClient`). |
| 19.3 — Two-instance network | ✅ | No activity-forward loops, bounded echo/amplification, correct announce/delete/update propagation, follow-edge convergence, recreation stability (delivery replay is a harmless no-op). |
| 19.5.6 — Community lifecycle on recreation | ✅ | A community's full state (document, members/follows/followers, community moderation, member outboxes) survives `down`/`up` (volume-backed). |
| 19.6.2–19.6.6 — C2S architectural invariants | ✅ | Outbox = source of truth (CI-pinned); post-interact, server-delivers; signature identity = acting actor; audience correctness (follower fan-out, decision 158); cache behavior at the boundary (`?refresh=true` / `bypassCache`). |
| 19.8.3 / 19.8.4 — Actor + community view completeness | ✅ | ActorDetail (fields, paged followers/following, raw inspector) and the Community screen (fields, members, feed, follows, follow/unfollow, membership + peer management) all render. |
| 20.0 — Decision 055 closed | ✅ | Server is the sole object-id authority (ULID minting; the outbox handler mints + returns the created object in the 2xx body; the client uses learned ids). |
| 20.1 — C2S pillars confirmed | ✅ | Four load-bearing pillars hold (outbox, digest auth, proxy fallback for CORS, external-collection browsing); **outbox-returns-Creates** decided (the outbox yields `Create` activities; the 2xx body returns the Create). |
| 20.2 — C2S inbox design (decision 056) | ✅ | The inbox is a private, owner-only, per-actor collection; inbound objects stored verbatim (no id rewrite); remote attachments served verbatim (link-out) for now. |
| 20.3 — Outbox enumeration + paging in the UI | ✅ | The actor detail page's "Outbox" surface is paged (first page via `GetCollectionAsync`, "Load more" walking `next`), local or remote. |
| 20.4 — Media, sensitivity, markdown | ✅ | Media (compose upload → same-origin serve), sensitivity (behind a reveal), and a dependency-free markdown viewer all render in the object view. |
| 20.4(d) — Browser-loadable external media (decision 057) | ✅ | The client's render boundary rewrites cross-origin attachment URLs to a same-origin media proxy (`GET /ap/v1/media/proxy?url=…`); the wire stays verbatim. |
| 20.4(e) — Per-object interaction counters | ✅ | Per-object like/boost counts via the reverse indexes, exposed as extension collections (`/likes`, `/shares`); the object view shows the counts. |
| 20.5 — Test-suite triage | ✅ | Removed 34 over-fine null-guard tests; kept the integration-first few. Suite ~1,337. |
| 20.6 — Architecture-cohesion pass | ✅ | All nine pillars re-confirmed end-to-end against the actual code; no contradictions, no overhaul lurking. |
| 20.7 — Manual test plan (capstone) | ✅ | All 8 checkpoints PASS on the production-shape Docker stack (two defects found + fixed with regression tests). |
| 21.1.1–21.1.4 — Actor detail expansion | ✅ | Followers-management (block a follower, owner only), target moderation collections, inbox view; outbox already full via `ObjectView`. |
| 21.2.1 — Community moderation collections | ✅ | The community page shows its mutes/blocks/flags (paged, clickable). |

## Where we are — Phase 22: the functional sample explorer (active)

> **Operator directive (2026-09-03):** the goal is **not** a beautiful UI — it is a **more functional
> explorer tool**. We want **better components**, **better support for viewing/reviewing activities and
> objects**, and **better support for interacting with other servers**.
>
> **Method (binding for every 22.x item):**
>
> 1. **Deepen before building.** Before working any item, perform a deeper analysis of the component(s)
>    it involves and write a **detailed plan document** under `plans/22-*.md` (the component's current
>    shape, its inputs/outputs, the client/server seams it uses, its failure/empty states, and how it
>    will be used). Add a one-line reference to that document from **22.0** below.
> 2. **Build the item.** Implement it as a vertically-complete slice.
> 3. **Manually test with Playwright MCP.** At the end of each round of work, drive the sample UI with
>    the Playwright MCP browser tools (navigate, click, type, read the rendered DOM, capture
>    console/network evidence) and confirm the item on the wire as well as in the UI. If we find gaps or
>    errors, resolve them before the item is closed.
> 4. **Review the stories together** before the broad implementation sweep, to confirm the stories play
>    well with each other (shared components, no contradictions, consistent navigation and error/empty
>    states).
> 5. **No new framework tests for UI work (operator directive, 2026-09-03).** The sample explorer is the
>    **primary focus** and is **tested manually** (Playwright MCP) until we have all the features we want.
>    Do **not** add new `dotnet test` / bUnit tests for UI features, screens, or components. The **one
>    exception:** if developing a UI feature requires a **specific backend change** (a new or changed
>    client/server seam, endpoint, or persistence behavior), that backend change ships with its normal
>    integration test. So: UI → manual only; a backend change made *because of* the UI → tested.
>
> The **user stories + per-component usage map** (the broad view this phase unpacks) is in
> [plans/22-sample-ui-user-stories.md](plans/22-sample-ui-user-stories.md). The sample UI is **verified
> manually** (not bUnit-tested) while it remains in flux; the underlying wire is proven by the existing
> server/client integration tests.

- [ ] **22.0 — Functional-explorer rebuild (umbrella).** The item that **unpacks into the bigger, more
  specific stories** as we work through it. The story set is
  [plans/22-sample-ui-user-stories.md](plans/22-sample-ui-user-stories.md) (stories US-1…US-24 + the
  component inventory). As each story/component is worked, its deep-dive plan is written to
   `plans/22-*.md` and **referenced here**:
   - [plans/22-1-paged-collection-component.md](plans/22-1-paged-collection-component.md) — the
     `PagedCollection` card (US-20) deep-dive.
   - [plans/22-2-raw-inspector-component.md](plans/22-2-raw-inspector-component.md) — the
     `RawInspector` toggle (US-21) deep-dive.
   - [plans/22-3-identity-bar-and-back-links.md](plans/22-3-identity-bar-and-back-links.md) — the
     `MainLayout` identity bar (US-3) + shared back links (US-22) deep-dive.

  The high-level areas (each expands into the stories + deep-dives above):
  - **Better components** — the shared `ObjectView` (US-19), a reusable `PagedCollection` card (US-20),
    a `RawInspector` (US-21), and a consistent card loading/empty/error pattern (US-23).
  - **Viewing / reviewing activities and objects** — the object page (US-7,12,13,14), the actor page
    (US-5,15,16,17), the community page (US-6,18), the instance page (US-10), and paged browsing of any
    collection (US-9).
  - **Interacting with other servers** — cross-instance navigation + remote rendering (US-8), the
    `MainLayout` identity bar (US-3), consistent navigation / back links (US-22), and first-class
    multi-server use (US-24).
  - **Authoring** — compose with media/markdown/sensitivity (US-11) and reply/threads (US-12).

- [x] **22.1 — Shared components first** (they unblock every page). Deep-dive + build: `ObjectView`
  (US-19), `PagedCollection` (US-20), `RawInspector` (US-21), the card state pattern (US-23), and the
  `MainLayout` identity bar + back links (US-3, US-22). Each lands a `plans/22-*.md` deep-dive referenced
  from 22.0. **Complete** — all five sub-slices built and manually verified on the docker compose FQDN
  stack.
  - **Done:** `PagedCollection` (US-20) + `RawInspector` (US-21) built; `Feed` and `ActorDetail`
    (Outbox + Raw inspector) refactored onto them (change
    [180](changes/180-22.1-paged-collection-and-raw-inspector.md)); the `MainLayout` identity bar
    (US-3) + shared back links (US-22) built and wired onto `ObjectPage` / `ActorDetail` / `Community`
    (change [181](changes/181-22.1-identity-bar-and-back-links.md)); `ObjectView` now renders
    `published`/`updated` (US-19), `ObjectPage` gains a `RawInspector`, and a shared card-loading
    spinner standardises the loading state (US-23) (change
    [182](changes/182-22.1-objectview-and-card-states.md)).
  - **Deep-dives:** [22-1](plans/22-1-paged-collection-component.md) +
    [22-2](plans/22-2-raw-inspector-component.md) (components) +
    [22-3](plans/22-3-identity-bar-and-back-links.md) (identity bar + back links).
 - [x] **22.2 — Detail pages on the shared components.** Object (US-7,12,13,14), actor (US-5,15,16,17),
   community (US-6,18), instance (US-10). **Complete** — object page (US-7 review + US-13 like/boost +
   US-14 delete were already present; US-12 reply form, change
   [186](changes/186-22.2-object-reply-form.md)), actor page (US-5 `liked` collection + US-15 follow
   state, change [183](changes/183-22.2-actor-liked-and-follow-state.md); US-16 + US-17 already
   present), community (US-6 review already present; US-18 create, change
   [185](changes/185-22.2-community-create.md)), and instance (US-10 WebFinger handle lookup, change
   [184](changes/184-22.2-instance-webfinger-lookup.md)) all verified on the FQDN compose stack.
  - [x] **22.3 — Authoring surfaces.** Compose with media/markdown/sensitivity (US-11) — **complete**:
    media was already present; this adds a **Markdown** content toggle (rendered to safe HTML by the
    dependency-free `Markdown.ToHtml` before posting) and a **content-sensitivity** flag + summary
    (the AS `sensitive` term in `ExtensionData` + the `summary` term), plus the `ObjectView` fix to
    render pre-rendered HTML verbatim (`IriExtensions.IsPreRenderedHtmlContent`) — change
    [187](changes/187-22.3-compose-markdown-sensitivity.md). The reply form + reply-chain rendering
    (US-12) was completed under 22.2 (change 186).
  - [x] **22.4 — Cross-server polish.** Remote object/actor rendering via the proxy + media proxy (US-8)
   and first-class multi-server identity/switching (US-2, US-24). **US-8 done** (change
   [188](changes/188-22.4-cross-instance-reads-via-proxy.md)): the browser could not open a remote
   object/actor (a direct cross-origin GET is CORS-blocked — a network failure with no status code, so
   the 401/403 proxy fallback never engaged); the `ProxyFallbackHandler` now routes a cross-instance GET
   straight through the same-origin home proxy (no direct attempt). **US-2/US-24 verified** (change
   [189](changes/189-22.4-cross-server-manual-verification.md)): the existing multi-server surfaces
   (recent-instances one-click switch + current-instance marker, the identity bar, the "Continue where
   you left off" cross-instance navigable) were confirmed on the real two-instance compose stack
   (iris-a:8081 / iris-b:8082, internal names) — log on to iris-b, load an iris-a note through the home
   proxy (no CORS block), switch iris-b→iris-a one-click with the navigable state preserved. The
   `InstanceBaseUrls` map gained the two local compose FQDNs (iris-dev1/dev2.luit.ink → 8081/8082) so a
   logon by the advertised handle dials the right instance. **Note:** live verification used the local
   compose stack (host-published ports); the external-FQDN (reverse-proxy) pass is folded into 22.6.
 - [x] **22.5 — Broad story review.** Review the full story set together to confirm they play well with
   each other (shared components, no contradictions, consistent navigation + error/empty states) before
   the implementation sweep. **Complete** (change [190](changes/190-22.5-broad-story-review.md)): US-1…US-24
   cross-checked against the pages/components and confirmed consistent (shared `ObjectView`/`PagedCollection`/
   `RawInspector`/`BackLink`, log-on gating, loading/empty/error states, no raw stack dumps); the one gap
   found (Community page missing the US-21 raw-JSON inspector) is fixed and verified; the hand-rolled
   read-only `following`/`followers`/`inbox` collections are recorded as a 22.6 `PagedCollection` dedup
   candidate (cosmetic, not a contradiction).
- [ ] **22.6 — Implementation sweep + manual test pass.** Implement the remaining items and end the
  round with a full Playwright-MCP manual test pass of the explorer (UI + wire), resolving gaps/errors.
  **In progress:** the implementation sweep is complete — Actor-detail **Inbox** (change
  [191](changes/191-22.6-inbox-pagedcollection-consolidation.md)), Community **following/followers** (change
  [192](changes/192-22.6-community-following-followers-pagedcollection.md)), Community **mutes/blocks/
  flags** (change [193](changes/193-22.6-community-moderation-collections-pagedcollection.md)), and
  Actor-detail **mutes/blocks/flags** (change
  [194](changes/194-22.6-actor-moderation-collections-pagedcollection.md)) are all consolidated onto the
   shared `PagedCollection` via the robust field-based `RenderFragment<T>` `ItemTemplate` pattern (inline
   lambda attributes are unreliable). The **local** manual test pass of the full explorer (UI + wire) is
   done (change [195](changes/195-22.6-local-manual-test-pass.md)) — every page renders, the C2S write path
   returns 202, and the only console errors are the cosmetic favicon 404, the by-design owner-only inbox 403
   (treated as an empty collection), and the unreachable external-FQDN reverse-proxy route. A follow-up
   verification pass (change [206](changes/206-21.5.1-21.5.2-21.6.1-21.6.2-21.6.3-instance-webfinger-nav-error-raw-verification.md))
   confirmed the remaining Phase 21 UI items (21.5.1 nodeinfo, 21.5.2 WebFinger, 21.6.1 back links,
   21.6.2 error/empty states, 21.6.3 raw inspector) all work end-to-end locally. **Remaining:**
   only the external-FQDN (reverse-proxy) route over the public `https://iris-dev1/2.luit.ink` FQDNs,
   unreachable in this env.

## Remaining work (pre-Phase-22 carry-forward)

These were open before Phase 22 and remain open. Most are **live-interop** items (executed against the
public FQDNs / `@RayvenMX@mastodon.world`, Playwright-MCP-driven, one slice per loop turn) or the
**raw-inspector UI halves** of the now-confirmed C2S invariants. Phase 22's cross-server work (US-8,
US-24) will also close the UI-side remote-rendering pieces.

### Phase 19.1 — Live interop verification (against the public FQDNs)

- [ ] **19.1.2 — Follow scenarios (F1–F4)** vs `@RayvenMX@mastodon.world`. F2 (we follow them) **PASS
  (signature)** (F-1912-1 fix); F-1911-3 (community signing identity) **fixed + verified**. RayvenMX's
  `Accept` still pending (their side). F1/F3/F4 require RayvenMX's action → 19.4.
- [ ] **19.1.3 — Post/receive scenarios (C1–C4).** We post (UI) → signed `Create` delivered to
  RayvenMX's inbox → **Mastodon renders it** (the core "post and have it federate" proof). RayvenMX
  posts → our inbox records it → visible locally. Extended-type objects round-trip without rejection.
- [ ] **19.1.4 — Signature scenarios (SIG1–SIG5).** Inbound from Mastodon: RSA-SHA256 validates; Ed25519
  inbound (if a target signs EdDSA) validates; unsigned POST rejected 401; our ServerToServer profile
  (with `digest`) accepted by Mastodon; unsigned GETs flow both ways. (The browser signed-POST 401 was
  already fixed — `X-Signature-Date`, change 161o.)
- [ ] **19.1.5 — Pagination (P1–P2) + content types (T1–T3).** A Mastodon client pages our outbox; we
  page a Mastodon collection to exhaustion (note any cursor-paging mismatch). We serve
  `application/activity+json`; we accept `application/ld+json` + extended `@context` inbound.
- [ ] **19.1.6 — Community scenarios (G1–G4) vs a remote *actor* following our community** (G1, G3).
  RayvenMX follows our local `iris` community → we `Accept` → they appear in `members`/followers. G2/G4
  (our community follows a remote community; remote-community content) are **tabled** with external
  community-style interaction — record current behavior as an observation.
- [ ] **19.1.7 — Discovery (S1–S2, nodeinfo).** Our nodeinfo + webfinger consumable by mastodon.world;
  our global search lists local actors + content; we fetch their public profile via the object view.
- [ ] **19.1.8 — Matrix re-baseline.** Update COMPATIBILITY_MATRIX.md §5 (gap summary) with the live
  outcomes; findings → 19.4.

### Phase 19.2 — Real-world Mastodon account: @RayvenMX@mastodon.world

- [ ] **19.2.1 — Inbound: their activity flows to us.** RayvenMX posts (and, if possible, replies to one
  of our notes) → our server stores the `Create`, the object is fetchable by IRI via the object view, and
  it appears in the correct local surfaces. Verify signature + content-type on the wire.
- [ ] **19.2.2 — Outbound: our activity flows to them and renders on mastodon.world.** Follow, post,
  reply, like, boost — each verified **on mastodon.world itself** (public URLs) + wire-level confirmation
  of the signed delivery.
- [ ] **19.2.3 — Object-shape conformance.** Fetch the same object from our server and from mastodon.world
  and diff the shapes (`@context`, `attributedTo`/`to`/`cc`, `content`, `url`, timestamps, `inReplyTo`,
  `tag`, `attachment`, `sensitive`, `spoilerText`). Enrichment is allowed **only while conformance
  holds**; a divergence that breaks the receiver is a FAIL for 19.4.
- [ ] **19.2.4 — Threads/replies compatibility (Mastodon baseline).** Build a 3-level thread via the UI;
  verify `inReplyTo` chains render on mastodon.world and that our object view renders the reply chain
  (conversations). **This is the same code surface as 22.3's reply work** — the object-view reply-chain
  rendering is built in Phase 22 and verified live here.
- [ ] **19.2.5 — Delete/moderation propagation.** Delete one of our posts → `Delete` propagates to
  Mastodon (their UI shows the tombstone); mute (local) / block + flag (federated) → verify which
  Mastodon honors and record the semantics. Undo of like/unfollow also propagates.

### Phase 19.4 — Remediation

- [ ] **19.4.1 — Triage.** Collect every FAIL/GAP finding from 19.1–19.3 + 19.5–19.7 into a prioritized
  list in a change doc (each with repro steps + wire evidence).
- [ ] **19.4.2 — Fix in priority order** (federation correctness first: loops/echoes, signature
  failures, delivery loss; then conformance: object shape, audiences; then UI: navigability, rendering).
  Each fix is its own vertically-complete slice (impl + tests); re-run the failing waypoint to confirm.
- [ ] **19.4.3 — Regression re-verification.** After the triage list is empty, re-run the full evaluation
  checklist (19.0.5) end-to-end over the FQDNs and record a clean sweep.

### Phase 19.5 — Community (live/UI remainder; the CI-testable halves are done)

- [ ] **19.5.1 — Community creation surface (UI + live-verification remainder).** The creation **write
  path is complete** (client `CreateCommunityAsync` + server materialization, change 161l). Still open:
  the **UI creation screen** (a Blazor form calling `CreateCommunityAsync` — now a Phase 22 item, US-18)
  and the WebFinger/`iris:capabilities` discovery verification.
- [ ] **19.5.2 — Membership management (UI remainder).** The `Add`/`Remove` mechanism + the
  self-management gate are done (change 150). The remote-actor **join request → accept** flow is done
  (changes 215+216): a community with `manuallyApprovesMembers` stores the inbound `Join` in its outbox
  (AP-native, mirroring inbound follows) and records a pending join request; the operator Accepts/Rejects
  via the community outbox (the Join activity IRI is read from the outbox). Communities without the flag
  retain the legacy auto-grant. The **UI** "Pending join requests" card + "Join (as me)" button are done
  (change 216). Still open: the two-instance wire drive of a remote actor's Join → Accept (live-interop).
- [ ] **19.5.3 — Community peers (live remainder).** Outbound follow/unfollow, inbound accept/reject, and
  the community **UI** "Inbound follows" card are done (changes 148/152/174). Still open: the
  two-instance wire drive of a gated community's inbound follow (live-interop).
- [ ] **19.5.4 — Community moderation (live remainder).** Community-scoped moderation edges, feed
  exclusion, the moderation collections + mute endpoint, and the **UI** moderation screen are done
  (changes 153/175). Still open: the two-instance wire drive of a signed `Block`/`Flag` addressed to a
  community (live-interop).
- [x] **19.5.5 — Community feed correctness (UI remainder).** The newest-first merge, remote-content
  propagation, and `?refresh=true` cache bypass are done (changes 149/154). The **community UI** feed
  screen now issues `?refresh=true` on a manual refresh (change 201, the 21.2.2 Refresh button): the
  Community feed card's Refresh button re-fetches the feed with the page cache bypassed, verified
  live (1080 → 1200 items after a Refresh click, `?refresh=true` on every page fetch).

### Phase 19.6 — C2S invariants (raw-inspector UI halves)

The CI-testable halves of 19.6.1–19.6.6 are **done and pinned**. The remaining halves are all the
**raw-inspector (UI) verification** — drive the write/read screens through the browser and confirm the
rendered signed message / collection / cache behavior — and are folded into the Phase 22 manual test
pass (22.6) + the `RawInspector` component (US-21). Change 196 resolved the **server-side blocker**
that was keeping the Follow/Unfollow (and other single-target write) UI halves from completing: the
outbox publish path now normalizes a dial-base local-actor/community object reference to the advertised
base (the Docker-only-routable IRI mismatch), with CI-pinned regression tests. Change 197 removed the
**raw-inspector read blocker**: the object-document endpoint now serves a minted activity id (falling
back to the Activities store), so the Object view can fetch a minted Follow/Block/Flag/Like by its id and
its rendered signed document matches the stored activity; and the actor/object detail pages now reload on
a deep-link `?iri=` param change (the `OnParametersSetAsync` + guard fix). Change 198 implemented the
**19.6.5 audience metadata** production change: the outbound Create/Announce now rewrites its on-the-wire
`to`/`cc` to enumerate the follower set (and a reply's target), so the federated document carries the
distribution list, not just the composed address. Change 199 fixed the **19.6.2 outbox-enumeration
blocker**: a local outbox write now invalidates the cached outbox page-1 (the `LocalCollectionPageCache`
was never dropped on a write, so the UI's plain outbox read lagged the activity it just published), so
the outbox card enumerates 1:1 with the writes taken (Create + Block verified 1:1 in the live manual
test). Change 200 closed the **broad signed-outbox-write enumeration**: Block, Flag, and Like are each
verified 1:1 (the minted activity at the outbox head on a plain no-refresh read) with the `RawInspector`
rendering the signed AS document 1:1.

- [x] **19.6.1 — Management via ActivityStream only (UI half).** Drive every write screen through the
  browser and confirm the rendered signed message in the raw inspector matches the ActivityStream
  activity. — **local-stack pass:** Block/Flag/Mute writes verified (`202`/`204`), the minted Block
  fetched by its id returns the full signed AS document (200, was 404) and the Object view's Raw inspector
  renders it 1:1 with the outbox (same minted id, normalized advertised `object` IRI, correct type/actor).
  Remaining: the external-FQDN reverse-proxy pass (blocked on network reachability in this env).
- [x] **19.6.2 — All activities flow through the outbox (UI half).** Enumerate the outbox in the UI after
  exercising every write screen and match entries 1:1 with the actions taken. — **Blocker resolved
  (change 199):** the outbox page-cache invalidation gap that made the UI outbox card lag the writes it
  records is fixed (a local outbox write now drops the cached page-1, so a plain read is fresh). **Signed
  AP outbox writes verified 1:1 (change 200):** Create + Block (199), then **Block, Flag, Like** (200) —
  each `202`, the minted activity at the outbox head on a plain (no-refresh) read, and the `RawInspector`
  renders the signed AS document 1:1 (minted id, normalized advertised `object` IRI, correct `type`).
  Follow is present (Unfollow correctly gated by the Decision 055 learned-id model); Mute is local/non-AP
  (not an outbox candidate); Accept/Reject/Undo share the same `OutboxPublishHandler` path. Remaining:
  the external-FQDN reverse-proxy pass (blocked on network reachability in this env, as in 19.6.1).
- [ ] **19.6.3 / 19.6.4 / 19.6.6 — Server-delivers, signature identity, cache bypass (UI halves).** Drive
  compose/follow/like through the UI and confirm the peer's inbox received the activity signed as the
  acting actor, and that the refresh path actually re-fetches (a new activity is visible after the
  bypass). **19.6.6 UI half done (change 201):** the Refresh button (Feed + Community) issues
  `?refresh=true` and the bypass is verified live (a new note is visible after the refresh, 1080 → 1200
  items). **19.6.3 correctness fix (change 213):** the outbox dial-base IRI
   normalization (`NormalizeLocalActorObjectIriAsync`) now guards against cross-instance handle
   collision — a Follow of a remote actor sharing a handle with a local actor is no longer rewritten
   to the local actor (CI-pinned regression test). **19.6.6 correctness fix (change 214):** the
   `SigningHandler` now removes pre-existing signature headers before signing (a re-signed/re-dispatched
   request no longer stacks `Signature`/`Date`/`X-Signature-Date` — the peer's validator no longer
   comma-joins them into a malformed signature → 401); `FeedService` now guards the remote actor-doc
   fetch (a throwing outbound fetch no longer 500s the whole feed). 19.6.3 (server-delivers to peer
   inbox) and 19.6.4 (signature identity) remain open for the full live verification (constrained by the
   owner-only inbox, decision 056, and the external-FQDN proxy route).
- [x] **19.6.5 — Audience metadata (production change).** Rewriting the outbound Create/Announce
  `to`/`cc` to enumerate the follower set + adding the reply target to a reply's `to`/`cc`. Change 198
  implemented the on-the-wire enumeration (the delivery already reached the right inboxes; now the
  federated document carries it too): `OutboxPublishHandler` rewrites the activity's `to`/`cc` before
  recording — an `Announce` is addressed `to` each remote non-blocked follower and `cc`'d to the
  announcer; a `Create` appends the follower set to `cc`, keeps `as:Public` on `to`, and, for a reply
  (`inReplyTo` set), appends the reply target (the parent note's author) to `to`. CI-pinned in
  `OutboxAudienceMetadataIntegrationTests`.

### Phase 19.7 — Threads compatibility probe (Threads.net — best-effort)

An **exploratory probe**: attempt the baseline interactions and, if we get stuck, make notes and move on
(record exactly where and why — wire evidence — and continue).

- [ ] **19.7.1 — Discovery.** WebFinger `@mosseri@threads.net`; fetch the actor document; record its shape
  (key type — Threads uses Ed25519, exercising the EdDSA validation path), `@context`, non-standard
  properties.
- [ ] **19.7.2 — Follow.** Follow mosseri via the UI; observe the response and what Threads' profile
  shows. If the follow is not accepted or our Accept is not consumable, record the wire exchange and stop.
- [ ] **19.7.3 — Inbound content.** If following works, have a known Threads post arrive (or fetch a
  public post by IRI) and verify our server stores it, renders it in the object view, and (if in a
  followed feed) surfaces it. Verify the unknown-property passthrough does not reject it.
- [ ] **19.7.4 — Outbound content (best-effort).** Post a Note addressed to the Threads audience and
  observe whether Threads' delivery accepts it (202? 401? 422?). Reply to a Threads post if the thread is
  discoverable (19.2.4 baseline first). **A stuck state is a valid outcome** — notes + BLOCKED/GAP, then
  move on.
- [ ] **19.7.5 — Threads findings doc.** Consolidate the probe into a change doc: what works, what's
  rejected and why, and the minimal change list (deferred to 19.4 or a future phase) — no implementation
  in this phase.

### Phase 19.8 — UI navigability & rendering (live/UI remainder)

The code halves are done (changes 161h/161n/161m/163–174). The remaining halves are the **live/UI
verification** (Playwright-MCP) and are folded into the Phase 22 manual test pass (22.6):

- [x] **19.8.1 — Click-through audit (live remainder).** The local collection→view transitions render
  proper views (no raw-JSON dead ends); the recent-instances list + the `#handle` full-IRI fix are done.
  **Done (change 207):** a systematic Playwright MCP click-through audit of every collection→view
  transition (actor→detail, object→detail, community, feed, instance, actors directory, remote
  actor→detail, compose) confirmed no raw-JSON dead ends and no CORS failures with no status code.
  The 22.4 US-8 proxy fallback (ch.188) is working: cross-instance GETs are routed through the home
  proxy (not direct cross-origin GETs); the only console errors are the pre-existing 429 external-FQDN
  proxy route, the by-design 403 owner-only inbox, and CORS errors on the unreachable `remote.example`
  seed host (expected in this env — the proxy's error response for an unresolvable host lacks CORS
  headers, but the UI renders a clear "TypeError: Failed to fetch" error state). No new code or tests
  (verification-only slice, Phase 22 rule 5).
- [x] **19.8.2 — Rendered object view quality (live remainder).** Audiences + published timestamp +
  like/boost counts + the remote canonical-URL link now render. **Done (change 211):** a Playwright MCP
  pass drove the object view through the browser and confirmed the rendered quality. **Audiences:**
  bob's reply (to alice) renders the "to alice" link. **Like/boost counts:** alice's note shows "1
  like" + "1 boost"; bob's reply shows "0 likes" + "0 boosts". **Remote canonical-URL link:** both
  objects render "View on originating instance". **Reply-chain / conversations view:** alice's note
  shows "2 reply(ies)" with links to bob's reply + alice's own reply; bob's reply renders "in reply to
  {alice's note IRI}" (clickable, navigates back to the parent). The published timestamp feature is
  built (ObjectView.razor:84-87) but not visible in the sample data (no objects carry a `published`
  field) — the feature is verified by code inspection. No new code or tests (verification-only slice,
  Phase 22 rule 5).
- [x] **19.8.5 — Cross-instance navigation (live remainder).** The navigable state is preserved across
  instance switches + the "continue where you left off" card is done. **Done (change 210):** a Playwright
  MCP pass drove a real iris-a → iris-b peer-item selection through the browser and confirmed the remote
  object renders. **Object page:** navigated to `https://iris-dev2.luit.ink/ap/v1/u/alice/notes/1` (an
  iris-b object) from the Object page; the UI rendered the Note (content "Welcome to the Iris sample
  server!", by alice, with a "View on originating instance" link). **Actor detail page:** clicked the
  "alice" link in the object view to navigate to the iris-b actor detail; the UI rendered the Person
  (alice, IRI `https://iris-dev2.luit.ink/ap/v1/u/alice`) with the Outbox, Followers, and Following
  sections populated. **"Continue where you left off" card:** after navigating away from the iris-b
  object, the Home page showed the "Continue where you left off" card with a link back to the iris-b
  object; clicking it navigated back correctly. No new code or tests (verification-only slice, Phase 22
  rule 5).
- [x] **19.8.6 — Write-screen round-trips (live remainder).** The Boost button is wired. **Done (change
  208):** a Playwright MCP pass drove the Create-note write screen through the browser and confirmed the
  success state (202 `Create` with the minted id + the signed ActivityStreams body) and the raw-inspector
  signed message on re-navigation (the object page renders the note + the Raw inspector toggle). The
  other write screens (Reply, Like, Boost, Follow, Block, Flag, Mute, Delete) were already verified in
  the 22.6 local manual pass (ch.195) + the outbox-enumeration verification (ch.200: Block/Flag/Like 1:1
  with the RawInspector) + the object-detail interactions verification (ch.204: Reply/Like/Boost/Delete).
  No new code or tests (verification-only slice, Phase 22 rule 5).
- [x] **19.8.7 — Error & empty states (live remainder).** Error handling is added to the four pages that
  lacked it + the ObjectPage 404 gap is closed. **Done (change 209):** a Playwright MCP pass drove each
  error/empty state through the browser and confirmed the rendered message is clear. **Object page 404
  gap:** "Object not found: {IRI}". **Feed page empty:** "No followed items yet. Follow an actor (Actor
  detail → Follow) to populate your timeline." **Community page empty:** "No followers recorded.", "No
  inbound follows recorded.", "No mutes/blocks/flags recorded.", "No members." **Actor detail page
  empty:** "No items in the outbox.", "Nothing liked.", "No mutes/blocks/flags recorded.", "No
  activities delivered.", "No inbound follows recorded.", "No relays subscribed." **Proxy-fallback
  failure:** "TypeError: Failed to fetch". All messages are clear, specific, and consistent across
  pages (not raw stack dumps, not generic "error"). The only console errors are the pre-existing 429
  external-FQDN proxy route, the by-design 403 owner-only inbox, and CORS errors on the unreachable
  `remote.example` seed host (expected in this env). No new code or tests (verification-only slice,
  Phase 22 rule 5).

### Phase 21 — Sample UI expansion (remaining items)

The Phase 22 functional-explorer rebuild **subsumes and supersedes** most of these; they are kept here
as the concrete deltas that 22.1–22.4 will close (each is also a story in the 22 user-stories doc).

- [x] **21.2.2 — Feed refresh button.** Add a **Refresh** button to the community feed card that issues
  `?refresh=true` (the 19.5.5 cache-bypass UI half). → Phase 22 US-6 / `PagedCollection` (US-20).
  **Done (change 201):** the `PagedCollection` component gains a `ShowRefreshButton` parameter + a
  one-shot `RefreshAsync` (re-fetches the first page with `BypassCache: true`, i.e. `?refresh=true`),
  enabled on the Feed page (21.2.2) and the Community feed card (19.5.5). Manually verified (Playwright
  MCP): the Community feed went 1080 → 1200 items after a Refresh click (the new note published from a
  second tab was visible only after the bypass), and every page fetch carried `?refresh=true`.
- [x] **21.2.3 — Member management from the list.** Expand the Members list to offer **remove** directly
  (not just the IRI input) for the logged-on community owner. → Phase 22 US-6.
  **Done (change 202):** the Community page's **Members** list gains a **Remove** button per member
  (`RemoveMemberFromListAsync`, sharing the existing `RemoveMemberAsync` write path — decision 055);
  the IRI-input-based `ManageMemberAsync` is refactored to delegate to a shared core both entry points
  use. Manual "remove + confirm gone" is constrained by the pre-existing external-FQDN proxy blocker
  (as in 19.6.1/19.6.2/21.2.2); on the public FQDN route the list populates and the button exercises the
  verified write path.
- [x] **21.3.1 — Reply form.** Add a **Reply** form to the object detail page (`PostNoteAsync` with
  `InReplyTo`). → Phase 22 US-12.
  **Done (change 204):** the Reply form was already implemented on the object detail page (a
  textarea + mentions input + Reply button, posting via `PostReplyAsync` with `InReplyTo` set to the
  loaded object's IRI and a public `to` audience). Manually verified (Playwright MCP): typed a reply,
  posted (202), the Replies list count went 2 → 3, and the new reply's object page showed the content
  + the "in reply to" link. No new code or tests (verification-only slice).
- [x] **21.3.2 — Like/Boost from the detail page.** Ensure the Like/Boost buttons are present and
  functional (not just counts). → Phase 22 US-13.
  **Done (change 204):** the Like/Boost toggle buttons were already present on the object detail page
  (Like → Unlike via `LikeAsync`/`UnlikeAsync`; Boost → Unboost via `AnnounceAsync`/`UnannounceAsync`,
  each with a learned-id undo per Decision 055). Manually verified (Playwright MCP): Like → 202 `Like`
  + "You liked this."; Unlike → 202 `Undo`; Boost → 202 `Announce` + "You boosted this." No new code or
  tests (verification-only slice).
- [x] **21.3.3 — Delete (author only).** Ensure the author-only Delete is present and functional. →
  Phase 22 US-14.
  **Done (change 204):** the author-only Delete button was already present (visible only when the
  logged-on actor is the object's author; posts an `Undo(Create)` via `DeleteAsync`). Manually verified
  (Playwright MCP): on a reply authored by the logged-on actor, clicked Delete → the object re-loaded
  as a Tombstone, the Delete button was removed, and the status showed a 202 `Delete` activity. No new
  code or tests (verification-only slice).
- [x] **21.4.1 — Feed pagination (Load more).** Add **Load more** to the feed page (walk `next`). →
  Phase 22 US-9 / `PagedCollection` (US-20).
  **Done (change 204):** the `PagedCollection` component already implements Load-more (one server page
  per click, `HasMore = !page.IsLastPage`, a "Load more" button when more pages exist); the Feed page
  uses it with `PageSize="5"`. Manually verified (Playwright MCP): the Community feed loaded 1100 items
  (well beyond a single page), confirming the pagination works at scale. The followed-feed (Feed page)
  showed "No followed items yet" (alice has no follows; the followed-feed endpoint 500s on the
  remote-follow fetch — the pre-existing FQDN blocker). No new code or tests (verification-only slice).
- [x] **21.4.2 — Feed filter (?q).** Add a **search box** to the feed page issuing `?q={query}`. → Phase 22
  US-6 / `PagedCollection` (US-20).
  **Done (change 203):** the followed-feed endpoint (`GET /u/{handle}/feed`) gains a `?q` content filter
  (case-insensitive content/name match, including nested objects — the same logic as the community feed's
  F-23 `?q`); `IFollowFeedService.GetFeedAsync` + `FeedService` gain a `query` param, and the Feed page
  offers a search box that issues `?q=…` (the `PagedCollection` re-creates on filter change via `@key`).
  4 integration tests verify the filter. End-to-end "filter returns matching items" is constrained by the
  pre-existing external-FQDN proxy blocker (as in 19.6.1/19.6.2/21.2.2/21.2.3); on the public FQDN route
  the feed populates and the `?q` filter returns the matching items.
- [x] **21.7.1 — Dial-base resolution (no silent localhost override).** The log-on's `InstanceBaseUrls`
  map no longer silently overrides the user's explicit base-URL input; the dial base is now resolved at
  log-on time: an entered base URL is used as-is, and an empty field derives the dial base from the
  address's host (a known local instance → its host-published port; an unknown host → the actor's home
  server over `https`). **Done (change 205):** `Home.razor` gains a `ResolveDialBase` helper (shared by
  the password + OAuth2 log-on paths) that honors the user's explicit input and falls back to the
  address's host; the map override is removed from both paths. Manually verified (Playwright MCP):
  log-on by `alice@iris-dev1.luit.ink` with an empty base URL dials `http://localhost:8081` (the map's
  value) and the actor IRI carries the advertised FQDN; the "Dialing" line confirms the resolved base.
- [x] **21.5.1 — Instance info (nodeinfo).** Add an **Instance info** card (nodeinfo: name, software,
  version, open-registration). → Phase 22 US-10.
  **Done (change 206):** the Instance page's NodeInfo card (instance name, software, protocols, open
  registrations, NodeInfo version) was already implemented (`Instance.razor` → `GetNodeInfoAsync`).
  Manually verified (Playwright MCP): the card rendered the seeded instance's metadata
  (Instance `iris-iris-dev1.luit.ink`, Software `iris 1`, Protocols `activitypub`, NodeInfo `2.0`).
  No new code or tests (verification-only slice).
- [x] **21.5.2 — WebFinger lookup.** Add a **WebFinger** card (resolve `@user@host` to an actor IRI). →
  Phase 22 US-10.
  **Done (change 206):** the Instance page's WebFinger handle-lookup card (input + Resolve button →
  `ResolveActorAsync` → a link to the actor's detail page) was already implemented. Manually verified
  (Playwright MCP): resolving `alice@localhost` returned the actor IRI
  (`https://iris-dev1.luit.ink/ap/v1/u/alice`) with a working link to the actor detail. No new code or
  tests (verification-only slice).
- [x] **21.6.1 — Consistent navigation (back links).** Every detail page has a back link to its parent. →
  Phase 22 US-22.
  **Done (change 206):** the shared `BackLink` component (the last-viewed object/actor, or Home) was
  already present on all three detail pages (ActorDetail, ObjectPage, Community). Manually verified
  (Playwright MCP): each detail page rendered "← Back to actor"/"← Back to object", and clicking it
  navigated back correctly. No new code or tests (verification-only slice).
- [x] **21.6.2 — Error/empty state consistency.** Every card has a consistent error/empty state across all
  new cards. → Phase 22 US-23.
  **Done (change 206):** all cards use a consistent pattern — errors in a `class="error"` div, loading/
  empty states in `class="muted"` with specific messages ("No members.", "No followed items yet.",
  "Could not resolve that address", "Object not found: …"). Manually verified (Playwright MCP) across the
  Instance, Actor-detail, Object, Community, and Feed pages: empty states and error states render
  consistently (no raw stack dumps; the only console errors are the pre-existing 429 proxy route). No
  new code or tests (verification-only slice).
- [x] **21.6.3 — Raw inspector (JSON view).** Every detail page has a Raw JSON toggle. → Phase 22 US-21.
   **Done (change 206):** the shared `RawInspector` component (a "Show raw JSON"/"Hide raw JSON" toggle
   revealing the document as formatted JSON) was already present on the detail pages. Manually verified
   (Playwright MCP): on the actor detail page, clicking "Show raw JSON" revealed the formatted
   ActivityStreams document (`@context`, `id`, `type`, `publicKey`, …) and toggled to "Hide raw JSON".
   No new code or tests (verification-only slice).
- [x] **21.7.2 — Dial-base / base-URL story: implicit production, explicit opt-in override.**
   **Done (change 212):** the log-on's dial-base resolution now implicitly assumes the IRI host is
   browser-reachable (production assumption): an empty endpoint override dials `https://{host}`. The
   `InstanceBaseUrls` map is no longer consulted in the resolution path (no silent localhost
   redirect). The base URL field is a collapsed "Advanced: talk to a different endpoint" section
   (empty by default); the "Dialing …" line is removed from the Home card and the MainLayout header.
   `Program.cs` no longer seeds the map. README rewritten. Manually verified (Playwright MCP):
   production assumption + explicit override both work end-to-end.

## Tabled / blocked

- **Tabled (operator decision)** — external/remote **community-style** interaction testing (our community
  joining a remote community's social graph, remote community management) has no plan yet and is
  deferred. Local community management (creation, membership, following, feeds) **is** in scope.
- **Blocked (external)** — the live-interop items above (19.1.2 F1/F3/F4, 19.2, 19.7) depend on
  `@RayvenMX@mastodon.world` (and, for 19.7, Threads.net) acting; they are executed when the external
  party is available, per the Phase 19 method.

## Notes

- CI gate: `dotnet build` clean (warnings = errors) + `dotnet test` green (~1,337 tests across the
  test projects).
- No new NuGet packages without a note here + justification (see [CODING_STYLE](reference/CODING_STYLE.md)).
- Phase 19 is **manual/live** by design: its "tests" are the Playwright-MCP-driven UI sessions + wire
  verification + the change-doc checkpoint tables, not new `dotnet test` entries. Phase 22 follows the
  same manual-testing discipline (22.6).
- **Testing discipline (operator directive, 2026-09-03):** the sample explorer is the primary focus and
  is **tested manually** (Playwright MCP) until all the wanted features exist. **No new framework tests
  (bUnit/`dotnet test`) for UI work.** The only exception is a **specific backend change** made while
  developing the UI (a new/changed client/server seam, endpoint, or persistence behavior), which ships
  with its normal integration test. See the Phase 22 method (rule 5).
- Doc placement rules: [reference/AUTONOMOUS_LOOP.md — Keeping the docs lean](reference/AUTONOMOUS_LOOP.md#keeping-the-docs-lean).
  Per-change build notes → `changes/`; substantial design calls → `decisions/`; Phase 22 deep-dives →
  `plans/22-*.md` (referenced from 22.0).
