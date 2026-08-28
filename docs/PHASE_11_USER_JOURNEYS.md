# Phase 11 — User-Journey Walkthroughs (Slice 11.2)

> **What this is.** A per-capability, end-to-end walkthrough of the library *as a user/app would drive it*, tracing each capability across client + server + samples + tests, noting every gap, dead-end, or confusing step. This is the first Phase 11 bullet (ROADMAP "User-journey walkthroughs"). It **confirms and re-derives** the capability gaps already catalogued in [RISK_GAP_REGISTER.md](RISK_GAP_REGISTER.md) §2 (G-1…G-6) and adds a new **usability-friction** register (J-1…) that the capability register does not cover. It is **research only** — it changes no production code. Closing the gaps is Phase 12's job (the conformance/missing-feature phase); this document hands Phase 12 a prioritised, journey-anchored gap list.

**Severity legend:** **Blocker** = a core user journey is impossible. **High** = a journey breaks or a spec-mandated path is missing. **Medium** = friction / a secondary journey is degraded. **Low** = discoverability or mental-model nit.

**Test-coverage legend:** ✅ = covered by a real-pipeline end-to-end integration test (multi-instance `TestServer` / signed client over a live server). 🟡 = covered only by unit tests or a single-instance integration test. ⬜ = no end-to-end coverage.

---

## A. Auth / key management

**Journey.** An app authenticates an actor and obtains a signing key:

1. `IrisClientBuilder.Create(options)` → `Build()` produces an `IrisClientBundle` (`src/Iris.Client.Extensions/IrisClientServiceExtensions.cs:83`).
2. `bundle.Session.LoginAsync(actorIri)` (`src/Iris.Client.Extensions/IrisSession.cs:83`) — Basic auth → the server's owner-only actor document (which carries the private key) → the key is loaded into the session's in-memory `IKeyStore` and registered with the `IKeyProvider`.
3. `bundle.CreateClient(actorIri)` (`IrisClientServiceExtensions.cs:155`) returns a signed, retry-enabled, proxy-fallback-enabled `IActivityPubClient`.

**Entry points:** `IrisSession.LoginAsync` / `SwitchIdentityAsync` / `Logout`; `IrisClientBundle.CreateClient`. The sample drives exactly this: `samples/SampleBlazorClient/Program.cs:34-47` (login → get client).

**Gaps / friction.**
- **J-1 (Medium) — The private key lives only in the session, in memory.** `IrisSession`'s XML doc states the key is "in-memory only — lost on page refresh (the user re-authenticates)". A Blazor-WASM host loses the key on every navigation; the user must re-login. This is the *documented v1 model*, but it is a real friction point for a user and is not surfaced in the sample. Fix plan: document the re-login expectation in the sample, and add an OAuth2 bearer path later (the `IClientAuthenticator` is the seam).
- **J-2 (Low) — `IKeyStore`/`IKeyProvider` are internal to the bundle.** A user who wants to add a key (key rotation, a second identity) has no public "add key" surface beyond `LoginAsync`. `SwitchIdentityAsync` re-authenticates from scratch. Acceptable for v1; noted so a future key-management API is not surprising.
- **J-3 (Medium) — No key-rotation invalidation path end-to-end.** The `RemoteKeyCache` (1h TTL) holds a remote actor's stale key after rotation (RISK_GAP_REGISTER O-3). An inbound POST signed with a freshly-rotated key 401s until the cache expires or `?refresh=true` is used. There is no runbook/test for rotation. (Confirmed gap, not new.)

**Coverage.** Auth is ✅ end-to-end (`FederationSignatureIntegrationTests` signs as alice over a live B; the sample login is exercised by `SampleBlazorClientTests`).

---

## B. Signing

**Journey.** Outbound: `ActivityPubClientFactory.Create` composes `RetryHandler → JsonLdHandler → SigningHandler → transport` (`src/Iris.Client/ActivityPubClientFactory.cs:57-78`). `SigningHandler` signs as `ActivityPubClientOptions.ActorId` using the key resolved from `IKeyProvider`/`IKeyStore`. Inbound: `SignatureValidationMiddleware` (POSTs only) → `HttpSignatureValidator.ValidateAsync` parses the `Signature` header, resolves the remote public key via `IKeyResolver.ResolveAsync(keyId)` (fetching the sender's actor document), and verifies (`src/Iris.Server/HttpSignatureValidator.cs:63-88`).

**Gaps / friction.**
- **J-4 (Medium) — Only RSA + EC P-256 are verified; no EdDSA.** Ed25519-signed inbound posts are rejected (401). This is RISK_GAP_REGISTER **G-5** (High). Confirmed by the verifier's key-type support; no EdDSA path exists.
- **J-5 (Low) — GETs are never signature-validated by the middleware.** This is deliberate (a key-resolution GET would recurse), but a user reading the middleware may not realise that *unauthenticated GETs of actor/collection documents are the norm* and that only inbox POSTs are gated. Worth a one-line note in `SignatureValidationMiddleware`'s doc (it is present at lines 41-45; the friction is discoverability, not behaviour).

**Coverage.** ✅ `FederationSignatureIntegrationTests` (alice→bob signed follow validates; a tampered/unsigned POST 401s).

---

## C. Post / create

**Journey (as the user would attempt it).** A user wants to *post a note*. The client API surface is:

- `IActivityPubClient.DeliverAsync(Iri inboxId, IObject activity, ct)` — `src/Iris.Client/ActivityPubClient.cs:143`. Throws `ArgumentException` unless `activity is Activity`; serializes and POSTs to the inbox IRI; returns the status code.
- `IActivityPubClient.SendAsync(HttpRequestMessage, ct)` — a raw signed passthrough.

**Gaps / friction.**
 - **J-6 (Blocker) — 🔶 RESOLVED (client half, Slice 11.5 + "recorded" server half, Slice 11.6): the client has a "post a note" API and a local post is now surfaced.** `IActivityPubClient.PostNoteAsync(Iri actorId, string content, IEnumerable<Iri>? to = null, ct)` builds the `Create` (an embedded `Note` with the content, a deterministic id derived from a content hash so a retried post dedupes, the note attributed to the author, and an optional audience `to`) and delivers it to `actorId.InboxOf()` — the "local post" path — through the signed pipeline. The caller supplies only the content; the `Create`, the embedded `Note`, and the delivery target are all derived. Proven by 3 unit tests + 1 e2e (`PostNoteIntegrationTests`: a local actor authenticates and posts over a live in-process server; the signed `Create` is accepted). **Server half (J-8, Slice 11.6):** `CreateActivityHandler` now records the inbound `Create` in the **author's outbox** (when the recipient is a local person), so a local member's post is **surfaced** in their own feed — proven by 8 unit tests + 1 e2e (`PostNoteSurfacesInOutboxIntegrationTests`: post via the client, then read the author's outbox endpoint over HTTP and assert the post is present). **Remaining (outbound, Phase 12, J-18):** the server does not yet deliver the post to the author's **remote** followers — so a local post is surfaced locally but not yet federated. That is the mirror of RISK_GAP_REGISTER **G-1** (no outbound `Create`): the client can *author and submit* a post and the server now *surfaces* it; the server must still *federate* it to remote followers.
- **J-7 (Medium) — `DeliverAsync` returns a bare `int` status code, no error model.** A 401/404/429 is indistinguishable from success to a caller who doesn't check the int. There is no typed result or exception for a rejected delivery. Friction for any user integrating delivery. **Fix plan:** return a small result record (status + body) or throw on non-2xx, mirroring the fetch methods' error handling.
- **J-8 (Medium) — ✅ RESOLVED (Slice 11.6): a person-inbox `Create` is now surfaced in the author's outbox.** A `Create` delivered to a local *person's* inbox is stored by `InboxProcessor` and now recorded into the person's outbox by the dedicated `CreateActivityHandler` (which, because the `InboxProcessor` prefers the most specific handler, intercepts every inbound `Create` before the community catch-all and records it in the recipient's outbox — the person's own outbox for a local person, the members' outboxes for a local community). This closes RISK_GAP_REGISTER **G-6**. A user posting to a person now sees the post in that person's feed.

**Coverage.** ✅ `PostNoteSurfacesInOutboxIntegrationTests` (client `PostNoteAsync` → signed `Create` accepted → the author's outbox endpoint surfaces the post end-to-end); ✅ `CreateActivityHandlerTests` (8 unit tests: local-person outbox recording, newest-first ordering, remote-person no-op, community member-recording, remote-member skip, unknown-recipient no-op, null guards); 🟡 `DeliveryIntegrationTests` (server schedules + delivers a follow's `Accept`). ⬜ no end-to-end *outbound* post→remote-follower test exists yet (J-18, Phase 12).

---

## D. Follow

**Journey.** A user wants to follow another actor. The federation path — the follower's instance delivers a signed `Follow` to the target's inbox — is now a one-call client API (`client.FollowAsync`), and the low-level `client.DeliverAsync(bobInboxIri, follow)` (hand-built `Follow`) remains for advanced use (`tests/Iris.Server.Tests/FederationSignatureIntegrationTests.cs:96`). The server's `FollowActivityHandler` (`src/Iris.Server/FollowActivityHandler.cs:66-121`) records the edge and schedules an `Accept` back to the follower.

**Gaps / friction.**
- **J-9 (High) — RESOLVED (Slice 11.4). The client now has a "follow" API.** `IActivityPubClient.FollowAsync(Iri actorId, Iri targetId, ct)` builds the `Follow` (deterministic `Id` = `{actor}/follows/{target}`, `Actor`/`Object` as `Link`s), derives the target's inbox (`targetId.InboxOf()`), and delivers it through the signed pipeline (`DeliverAsync`). The user journey "I want to follow @bob@host" is now one call. Proven by 3 unit tests (correct inbox/actor/object/id, serialization, delivery-status passthrough) + 1 e2e test (`FollowIntegrationTests`): a local actor authenticates (Basic auth → PEM key) and follows a second seeded actor; the request is signed through the full pipeline, the server's inbound key resolver fetches the follower's actor doc (single-instance self-fetch via a deferred `LazyHandler`), validates the signature, and records the follow edge (486→490). **Remaining (Phase 12, J-21):** let `FollowAsync` also accept a handle (`@bob@host`) and resolve it via `IDiscoveryService`, so the handle→IRI step is one call.
- **J-10 (High) — The server always accepts; `Reject` is never sent.** `FollowIris.BuildReject` exists but has no callers (RISK_GAP_REGISTER **G-2**). A user cannot be rejected, and there is no moderation/`manuallyApprovesFollowers` surface. (Confirmed.) **Resolved (Slice 11.10):** `manuallyApprovesFollowers` now gates auto-accept — a `manuallyApprovesFollowers` local person records the follow edge but does not auto-accept; the operator responds with an explicit `Accept`/`Reject`, and `BuildReject` has a live path. A dedicated moderation UI/API (list pending follows, one-click approve/reject) remains a Phase 12 item.
- **J-11 (High) — No outbound group-follow.** The server never initiates a follow of a remote `Group` (RISK_GAP_REGISTER **G-3**). An Iris community cannot follow a remote community, so it cannot receive that community's content. (Confirmed.)
- **J-12 (Medium) — `followers` for a *community* is always empty.** A follow of a community records an edge in the community's *follows* set, not a followers set, so `GET /c/{name}/followers` serves the empty collection. A user following a community sees it in the community's `following`, not its `followers` — a confusing inversion. This is *by design* (the community "follows back" the follower so the follower's content reaches members), but it is not documented at the endpoint and will read as a bug. **Fix plan:** document the asymmetry on the `CommunityCollectionHandler` route comment + `TESTING.md`.

**Coverage.** ✅ `FederationSignatureIntegrationTests` (full follow→accept loop over the wire), `CommunityFollowsCommunityIntegrationTests` (community-follows-community, one-sided), `CollectionEndpointIntegrationTests` (followers/following collections).

---

## E. Community feed

**Journey.** A user reads a community's feed: `client.GetCommunityFeedAsync(communityIri)` → `GetCollectionItemsAsync(communityIri.FeedOf())` (`src/Iris.Client/ActivityPubClient.cs:236`). The server serves `GET /c/{name}/feed` as a paged `OrderedCollection`/`OrderedCollectionPage` from the union of local members' outboxes (`CommunityFeedHandler`). Search is `GET /c/{name}/search?q=…`.

**Gaps / friction.**
- **J-13 (Medium) — The feed is members' own posts + delivered content, but a *local member* has no way to post into it** (see J-6/J-8). The feed is read-rich, write-poor: a member can read the community feed but cannot post to it through the client. The "join a community and post" journey is therefore incomplete.
- **J-14 (Low) — `FeedFilter?` is accepted but is a thin wrapper.** `GetCommunityFeedAsync(Iri, FeedFilter?)` ignores the filter in v1 (it delegates straight to the collection walk). A user passing a filter expects filtering that does not happen. **Fix plan:** implement the filter or drop the parameter to avoid a misleading API.
- **J-15 (Medium) — No global search / directory.** Only per-community search exists (RISK_GAP_REGISTER **G-4**). A user (or a platform indexer) cannot discover communities. (Confirmed.)

**Coverage.** ✅ `CommunityFeedIntegrationTests` (feed = union of member outboxes; paging; unknown community 404), `CommunitySearchIntegrationTests` (case-insensitive, paging, `iris:capabilities`).

---

## F. Proxy fallback

**Journey.** A browser-based actor's client cannot sign an outbound request to a *remote* instance cross-origin. `ProxyFallbackHandler` (outermost in the client pipeline) retries a direct 401/403 through the home instance's `POST /ap/v1/proxy/{target}` (`src/Iris.Client/ProxyFallbackHandler.cs:55-99`), which the server signs with the actor's key. The server gates the proxy with an allowlist + per-actor rate limit (`AllowlistProxyTargetPolicy` + `RateLimitingProxyPolicy`, `src/Iris.Server/ActivityPubServerExtensions.cs:237-238`); a rate-limit rejection is **429**, an allowlist rejection is **403**.

**Gaps / friction.**
- **J-16 (Medium) — Proxy fallback is wired only for 401/403 on a *direct* attempt, and the proxy failure is returned as-is.** A 5xx from the remote (transient) does *not* trigger the proxy (only 401/403 do); the `RetryHandler` handles the 5xx instead. This is correct, but the interaction (retry *inside*, proxy *outside*) is subtle and not covered by a single end-to-end "remote 401 → proxy succeeds" test that asserts the *content* came back. **Fix plan:** add an end-to-end proxy-fallback test that asserts the relayed body, not just the status.
- **J-17 (Low) — `ProxyCredentials` must be supplied separately from the session.** The client's Basic-auth credentials for the proxy are passed via `IrisClientOptions.ProxyCredentials`, not derived from the `IrisSession` login. A user who logs in must *also* configure proxy credentials — two credential sources for one actor. **Fix plan:** derive proxy credentials from the session (or document the duplication).

**Coverage.** ✅ `ProxyFallbackIntegrationTests` (proxy 401/403/429 paths; allowlist). 🟡 no end-to-end *content-relay* assertion (see J-16).

---

## G. Cross-instance delivery (A→B)

**Journey.** A signed activity is delivered from instance A to B. Server-side, `DeliveryService.DeliverToActorAsync(recipientIri, activity, actorIri)` (`src/Iris.Server/DeliveryService.cs:61-74`) enqueues a `DeliveryJob` on the `IDeliveryQueue`; the `DeliveryWorker` signs it as the acting actor and POSTs it to `recipientIri.InboxOf()`. B's `SignatureValidationMiddleware` + `HttpSignatureValidator` resolve A's actor document (key fetch) and verify, then `InboxProcessor` stores + dispatches to the matching handler.

**Gaps / friction.**
- **J-18 (High) — Outbound delivery only *responds*; it never *originates* user content.** The only things the server schedules for delivery are the follow's `Accept`/`Reject` (never used) and an `Announce` boost propagation (`AnnounceActivityHandler.cs:128`). There is no path that delivers a user's `Create` to followers (G-1) or a community's follow of a remote group (G-3). So "cross-instance delivery" as a *user* capability is effectively "we can accept a follow and boost" — not "my post reaches my followers on another instance." (Confirmed; this is the headline Phase 12 item.)
- **J-19 (Medium) — The server cannot fetch a *remote* actor's outbox to show a followed feed.** `IrisRemoteCollectionFetcher` (server-side, backed by the client) exists and is registered (`ActivityPubServerExtensions.cs:164-186`), but it is used only for *reading a remote collection page*, and there is no endpoint that merges a followed remote actor's outbox into a local feed. A user who follows a remote actor cannot see that actor's posts in a local feed — they must poll the remote outbox themselves via the client. **Fix plan:** a "followed feed" endpoint that uses `IRemoteCollectionFetcher` to pull each followed remote actor's outbox and merge it (mirroring the community feed's local-member merge).
- **J-20 (Low) — Delivery failures are logged, not surfaced.** The `DeliveryWorker` logs a failed delivery; there is no user-visible "your post failed to reach N followers" surface. Acceptable for v1 (async best-effort), noted.

**Coverage.** ✅ `DeliveryIntegrationTests` (A schedules, B receives the signed `Accept`), `FederationSignatureIntegrationTests` (signature across instances), `RemoteCollectionFetcherIntegrationTests` (server reads a remote collection page).

---

## H. Discovery

**Journey.** A user references an account as `@handle@host`. The *client* has `IDiscoveryService.ResolveActorAsync(account)` → `WebFingerDiscoveryService` → `WebFingerClient` (WebFinger). The *server* has `IAccountResolver` (`WebFingerAccountResolver`) for resolving a handle to an actor IRI, used during key resolution.

**Gaps / friction.**
- **J-21 (High) — RESOLVED (Slice 11.3). The client's discovery service is now exposed in the bundle.** `IrisClientBundle` now exposes `Discovery` (`IDiscoveryService`) and a `ResolveActorAsync(account, ct)` convenience; `IrisClientBuilder.Build()` builds a default WebFinger-backed service (plain unsigned `HttpClient`, reusing the bundle's WebFinger cache) and `WithDiscovery(...)` supplies a custom one. Proven by 4 unit tests + 1 e2e test resolving a handle through the real server's `/.well-known/webfinger` (482→486). **Remaining (Phase 12, J-9/J-18):** have a future `FollowAsync`/`PostNoteAsync` accept a handle and resolve it via this service, so the handle→IRI step is one call rather than a separate `ResolveActorAsync` + fetch.
- **J-22 (Low) — `WebFinger` is served at both `/.well-known/webfinger` and `/ap/v1/.well-known/webfinger`.** Correct (RFC 8410 root + versioned symmetry), but a user must know the root path is the one a *remote* instance will hit. Documented in the route comment; no action.

**Coverage.** ✅ `WebFingerClientTests` (unit); ✅ the client's handle→IRI resolution is now exercised end-to-end — `EndToEndSessionIntegrationTests.Bundle_ResolveActor_HandlesWebFinger_ReturnsActorIri` resolves a handle through the real server's `/.well-known/webfinger` via the bundle's exposed discovery service (Slice 11.3); 🟡 the server's WebFinger/NodeInfo endpoints are also exercised by `ServerEndpointIntegrationTests` (single-instance).

---

## Summary — new usability-friction register (J-1…J-22)

The capability gaps below are **new to this walkthrough** (the rest are confirmations of G-1…G-6 in [RISK_GAP_REGISTER.md](RISK_GAP_REGISTER.md) §2):

| ID | Gap | Severity | Capability | Fix plan (Phase 12) |
|----|-----|----------|-----------|---------------------|
| **J-6** 🔶 | Client "post a note" API exists — `client.PostNoteAsync(actorId, content, to)` (Slice 11.5): builds a `Create` (embedded `Note`, deterministic id, attributed to author) and delivers it to the author's own inbox (the "local post" path). **Server half (J-8, Slice 11.6):** `CreateActivityHandler` records the inbound `Create` in the author's outbox, so a local post is **surfaced**. **Remaining (outbound, J-18):** schedule outbound delivery of the post to the author's remote followers, so it federates. | **Blocker** | C. Post | Client half + "recorded" server half done. Outbound (J-18) → Phase 12. |
| **J-21** ✅ | Client discovery service (`@handle@host` → IRI) now exposed on `IrisClientBundle` (`Discovery` + `ResolveActorAsync`) — Slice 11.3. Remaining: let a future follow/post accept a handle and resolve it. | **High** | H. Discovery | Done (exposure). Follow/post handle-resolution → Phase 12 (J-9/J-18). |
| **J-9** ✅ | Client "follow" API now exists: `FollowAsync(actorId, targetId)` → `target.InboxOf()` + signed `Follow` — Slice 11.4. Remaining: accept a handle and resolve it (J-21). | **High** | D. Follow | Done (one-call follow). Handle-resolution overload → Phase 12 (J-21). |
| **J-18** | Outbound delivery only *responds*; user content never federates (no outbound `Create`/group-follow). | **High** | G. Delivery | Outbound `Create` to followers (G-1) + outbound group-follow (G-3). |
| **J-19** | No "followed feed" endpoint; a followed remote actor's posts are not surfaced locally. | **Medium** | G. Delivery | A feed endpoint that pulls each followed remote outbox via `IRemoteCollectionFetcher` and merges it. |
| **J-7** | `DeliverAsync` returns a bare `int`; no typed error model for a rejected delivery. | **Medium** | C. Post | Return a result record (status + body) or throw on non-2xx. |
| **J-1** | Private key is in-memory only; lost on page refresh (Blazor-WASM). | **Medium** | A. Auth | Document the re-login expectation; add an OAuth2 bearer path later (`IClientAuthenticator` seam). |
| **J-3** | No key-rotation invalidation path end-to-end (stale `RemoteKeyCache` until TTL/`?refresh`). | **Medium** | A. Auth | A rotation runbook + a live rotation test (re-fetch doc, invalidate cache). |
| **J-4** | Only RSA + EC P-256 verified; no EdDSA (Ed25519 inbound 401s). (= G-5) | **High** | B. Signing | Add EdDSA key support to the generator/verifier. |
| **J-8** ✅ | Person-inbox `Create` now surfaced in the author's outbox — `CreateActivityHandler` records it (Slice 11.6). (= G-6, closed) | **Medium** | C. Post | Done. `CreateActivityHandler` records an inbound `Create` in the recipient's outbox (person's own outbox for a local person; members' outboxes for a local community). |
| **J-10** | Follows are always accepted; `Reject` never sent; no `manuallyApprovesFollowers`. (= G-2) | **High** | D. Follow | Wire the `Reject` scheduling path + a `manuallyApprovesFollowers` surface. **Resolved (Slice 11.10)** — the flag gates auto-accept and `BuildReject` has a live path; a moderation UI/API is Phase 12. |
| **J-11** | No outbound group-follow (community can't follow a remote community). (= G-3) | **High** | D. Follow | An outbound group-follow path. |
| **J-12** | A community's `followers` is always empty (the asymmetry is undocumented). | **Medium** | D. Follow | Document the follows/followers inversion on the route + `TESTING.md`. |
| **J-13** | A local member has no way to post into the community feed (read-rich, write-poor). | **Medium** | E. Feed | Fold into J-6 (a post that targets a community). |
| **J-14** | `FeedFilter?` is accepted but ignored. | **Low** | E. Feed | Implement the filter or drop the parameter. |
| **J-15** | No global search / directory. (= G-4) | **Medium** | E. Feed | A global search/directory endpoint. |
| **J-16** | No end-to-end proxy-fallback test asserting the *relayed content*. | **Medium** | F. Proxy | Add a content-relay assertion to `ProxyFallbackIntegrationTests`. |
| **J-17** | Proxy credentials are supplied separately from the session login. | **Low** | F. Proxy | Derive proxy credentials from the session (or document). |
| **J-2** | No public "add key" / key-management surface beyond `LoginAsync`. | **Low** | A. Auth | A key-management API later (rotation, multi-identity). |
| **J-5** | GETs are never signature-validated (deliberate); discoverability nit. | **Low** | B. Signing | A one-line doc note on the middleware. |
| **J-20** | Delivery failures are logged, not surfaced to the user. | **Low** | G. Delivery | A user-visible delivery-status surface later. |
| **J-22** | WebFinger served at two paths (correct); discoverability nit. | **Low** | H. Discovery | None (documented in the route comment). |

**Headline for Phase 12.** The *read* side of the platform (auth, signed fetch, community feed, discovery) is solid, and the *client* write side is now mostly reachable and a local post is now surfaced: **J-21** (discovery exposure, Slice 11.3), **J-9** (client follow, Slice 11.4 — `client.FollowAsync`), **J-6's client half** (client post, Slice 11.5 — `client.PostNoteAsync`), and **J-8** (record a local post in the author's outbox, Slice 11.6 — `CreateActivityHandler`) are resolved. What remains for the write path is the **outbound half of J-6**: the server records a local post in the author's outbox (surfaced) but does not yet deliver it to the author's **remote** followers (**J-18**) — so a local post is surfaced but not yet federated. Phase 12 should close that first (outbound `Create` to followers → followed feed), then the secondary gaps.

---

*Slice 11.2 is research-only: no production code changed. The gaps above are the input to Phase 12's prioritised fix plan (ROADMAP Phase 12, "Prioritized fix plan" + "Implement high-priority gaps").*
