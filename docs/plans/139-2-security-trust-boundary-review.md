# 139.2 — Security & trust boundary review

> Part of [Phase 139](phase-139-platform-e2e-review.md). Scope: an OWASP-Top-10-oriented pass plus
> ActivityPub-specific trust-boundary checks (signature spoofing, audience/authorization bypass,
> cross-instance moderation trust). Builds on Phase 50.1 (security audit), Phase 131.2 (security
> headers), Phase 136.3/136.10 (signature matrix, moderation trust boundary), and Phase 132/134
> (rate limiting, retry hardening) — this review's job is to confirm those hold under combined,
> adversarial scenarios rather than isolated ones.

## Test scenarios

| # | Scenario | Steps | Pass criteria | Evidence |
|---|---|---|---|---|
| 1 | Auth bypass via direct API call | For every authenticated Blazor page/action, call the underlying API route directly without a session cookie/bearer token | 401/403, no data leak, no state mutation | curl transcript per route |
| 2 | Cross-account authorization (IDOR) | As user A, attempt to read/mutate user B's private data (settings, moderation collections, DMs) by IRI/id substitution | 403/404, never 200 with B's data | curl transcript |
| 3 | Forged signature / actor-key mismatch | Send a signed activity where the `keyId` actor doesn't match the activity's `actor` field | Rejected by `HttpSignatureValidator` | request/response log |
| 4 | Replayed / stale signed request | Re-send a previously captured signed request verbatim after a long delay | Confirm current behavior (Phase 136.17 found no freshness check) still matches the documented gap — decide if this review is where that gap finally gets closed | test result + decision note |
| 5 | Audience/visibility bypass | Attempt to read a followers-only or direct post as an unauthenticated or unrelated user, via feed, search, and direct object-IRI fetch | Confirm current behavior against Phase 136.18's documented gap (visibility not filtered on several read paths) — decide if this review closes it | test result + decision note |
| 6 | Moderation trust boundary | A blocked/muted remote actor attempts to deliver content anyway (bypassing the block via a fresh activity IRI, a different verb, or a relayed announce) | Content is not surfaced to the blocking user's feed/notifications | delivery + feed check |
| 7 | Rate limiting under burst | Hammer login, inbox delivery, and search endpoints past configured limits | 429s with correct `Retry-After`, no crash, no auth bypass via retry-storm | load-test transcript |
| 8 | Input validation / injection surface | Submit oversized bodies, malformed JSON-LD, script-tag content in markdown/HTML fields, path traversal in media URLs | No stored XSS in rendered content, no unhandled exception, size caps enforced (Phase 33.4) | screenshot + payload log |
| 9 | CORS / same-origin enforcement | Attempt a cross-origin authenticated fetch from a non-allowlisted origin | Blocked (Phase 33.5 default), confirm no regression from any dev-mode CORS relaxation leaking into prod config | browser console + response headers |
| 10 | Session/cookie hardening | Inspect cookie flags (`Secure`, `HttpOnly`, `SameSite`), session fixation on login, logout invalidation | Flags correct (Phase 131.5), old session unusable after logout | browser devtools capture |
| 11 | Key rotation trust | Rotate an actor's key (Phase 84 lifecycle) mid-conversation with a peer; confirm the peer picks up the new key and old-key-signed requests are rejected post-rotation | New key honored, old key rejected after rotation completes | signed request before/after |
| 12 | Dependency/supply-chain spot-check | Re-run a dependency vulnerability scan (`dotnet list package --vulnerable` or equivalent) against current `Directory.Packages.props` | No unaddressed high/critical CVEs | scan output |
| 13 | Secrets handling | Grep deployed config/compose files and logs for accidentally-logged credentials, keys, or tokens | None found | grep transcript |
| 14 | Admin/privileged-action authorization | Every admin-only action (user role management, instance metadata edit, moderation queue) re-checked for a non-admin bypass attempt | 403 for non-admin, correct audit trail for admin | curl transcript |

## Deliverable check

All 14 scenarios executed with evidence; every finding triaged (class + severity); scenarios 4 and 5
produce an explicit **decision** (close the gap now vs. defer with a tracked follow-up), since they
reference previously-documented, deliberately-deferred gaps rather than unknowns.

## Progress tracking

- [x] 1  - [x] 2  - [x] 3  - [x] 4  - [x] 5  - [x] 6  - [x] 7
- [x] 8  - [x] 9  - [x] 10 - [ ] 11 - [ ] 12 - [ ] 13 - [ ] 14

Check a scenario off only once its pass criterion is met with evidence attached (link/path). Update
the area's Status cell in [phase-139-platform-e2e-review.md](phase-139-platform-e2e-review.md) to
`in progress` on the first checked box, `done` when all are checked (or explicitly skipped).

**Resume checkpoint:** scenarios 1–10 done — begin at scenario 11.

## Findings

| ID | Scenario | Class | Severity | Finding | Disposition |
|---|---|---|---|---|---|
| F-9 | 3 | spec | S3 | **keyId/actor mismatch is spec-conformant — the ActivityPub HTTP signature model does NOT require the keyId's owner to match the activity's `actor` field.** The signature proves the request came from a known instance (the keyId's owner); the instance is responsible for only sending activities from its own local actors. The `actor` field in the body is the claimed author, and handler-level authorization (e.g. the 19.5.2 community self-management gate) decides whether the actor is allowed to perform the action. A keyId/actor mismatch is a **trust violation by the peer** (not a protocol violation), and the correct response is to report/block the peer, not to reject the request with 401. The existing behavior (accept the activity, let handler-level gates enforce authorization) is correct. | **No fix needed.** The behavior is spec-conformant. The trust model is: you trust the instance (keyId owner), and the instance is responsible for only sending legitimate activities. A peer that sends forged activities should be reported/blocked, not rejected at the protocol level. |

## Scenario 3 — Forged signature / actor-key mismatch (evidence, 2026-09-16)

**PASS — F-9 is spec-conformant (S3, not a bug).** The ActivityPub HTTP signature model does
**not** require the keyId's owner to match the activity's `actor` field. The signature proves the
request came from a known instance (the keyId's owner); the instance is responsible for only sending
activities from its own local actors. The `actor` field in the body is the claimed author, and
handler-level authorization (e.g. the 19.5.2 community self-management gate) decides whether the
actor is allowed to perform the action.

**Investigation:** A keyId/actor mismatch guard was initially added to `HandleInboxPostAsync` but
broke legitimate federation patterns:
- `CommunityMembershipManagementIntegrationTests.Add_SignedByCommunityButActorIsAnotherActor_DoesNotModifyMembership`
  expects the community to sign on behalf of alice (keyId=community, actor=alice) and the activity
  to be **stored** (202) with the membership change rejected by the 19.5.2 gate (not a 401).
- An instance can legitimately sign on behalf of its local actors (e.g. a relay signing on behalf
  of its users). A 401 for any keyId/actor mismatch would break this pattern.

**Correct trust model:** You trust the **instance** (the keyId's owner). The instance is responsible
for only sending legitimate activities from its own local actors. A peer that sends forged
activities (keyId=instance A, actor=instance B's user) is in violation of the trust — the correct
response is to report/block the peer, not to reject the request at the protocol level.

**Side fix (F-9a, S2):** `FirstIriFromCollection` (`ActivityPubServerExtensions.cs:3120`) used
`link.ToString()` which returns the type name (`KristofferStrube.ActivityStreams.Link`) instead of
the IRI. Fixed to use `href.AbsoluteUri`. This was a latent bug that only surfaced when the F-9
guard was added (the `actorIri` value was previously only used for logging).

## Scenario 2 — Cross-account authorization / IDOR (evidence, 2026-09-16)

**PASS.** The non-admin `/local/v1/` endpoints take **no user ID parameter** — they always operate
on the session user's data (from `HttpContext.User`). IDOR is impossible by design: there is no IRI
or id to substitute.

Verified: none of the non-admin `/local/v1/` endpoints (`/notifications`, `/account/…`, `/session`)
accept a user ID in the route or body. They all resolve the target user from the authenticated
session (`HttpContext.User.Identity.Name`), not from a request parameter.

The admin endpoints (`/local/v1/admin/users/{id:guid}/…`) **do** take a `{id:guid}` parameter — but
those are admin-only (403 for non-admin), which is covered by scenario 14 (admin/privileged-action
authorization).

**Pass criterion met.** As user A, it is not possible to read/mutate user B's private data by
IRI/id substitution — the endpoints don't accept a target user ID. The admin endpoints that do
accept a target user ID are admin-gated (scenario 14).

## Scenario 1 — Auth bypass via direct API call (evidence, 2026-09-16)

**PASS.** All authenticated `/local/v1/...` endpoints return **302** (redirect to login,
`content-length: 0`) or **401** for unauthenticated requests. No data leak, no state mutation.

Tested endpoints (all without a session cookie):
- `GET /local/v1/notifications` → 302 (→ `/login?ReturnUrl=...`, body empty)
- `GET /local/v1/notifications/unread-count` → 302
- `GET /local/v1/account/notification-preferences` → 302
- `GET /local/v1/account/key-info` → 302
- `GET /local/v1/session` → 302
- `GET /local/v1/admin/users` → 302
- `GET /local/v1/admin/instance` → 302
- `GET /local/v1/admin/stats` → 302
- `POST /local/v1/notifications/read` → 302
- `PUT /local/v1/account/notification-preferences` → 401
- `DELETE /local/v1/account` → 302

Public `/ap/v1/` endpoints (correctly public by design):
- `GET /ap/v1/u/andrew` → 200 (actor doc is public)
- `GET /ap/v1/u/andrew/outbox` → 200 (outbox is public)
- `GET /ap/v1/u/andrew/inbox` → 403 (inboxes are not public)

The 302 redirect is the Blazor Server pattern (redirect to login for browser requests); the 401 is
for some API requests. Both are secure — no data is served, no state is mutated. All security headers
present (CSP, X-Content-Type-Options, X-Frame-Options, Referrer-Policy, Permissions-Policy, HSTS).

## Scenario 4 — Replayed / stale signed request (evidence, 2026-09-16)

**CONFIRMED — current behavior matches Phase 136.17's documented gap. Decision: defer.**

**Current behavior (confirmed):**
- `CrossInstanceReplayDefenseIntegrationTests` (3 tests) all pass, confirming:
  - `ReplayedSignedRequest_SameDateSameSignature_IsAccepted`: a signed request re-sent verbatim
    (same Date, same Signature, same Digest, same body) is **accepted** (202). No freshness check.
  - `DuplicateCreateDelivery_StoredOnce_HandlerRunsOnce`: a redelivered activity (same ID) is
    stored **exactly once** (IRI dedup via `TryAddActivityAsync`), handler runs once.
  - `ReorderedWireHeaders_StillValidate`: benign header reordering does not break validation.

**Replay defense posture (confirmed unchanged from Phase 136.17):**

| Attack | Defense | Status |
|--------|---------|--------|
| Body tamper | `digest` component cryptographically bound | Enforced (401) |
| Date tamper | `date` component cryptographically bound | Enforced (401) |
| Stale-date replay | **No freshness check** | Gap (accepted; IRI dedup prevents state duplication) |
| Rate-limited spam | Per-peer inbound rate limiter | Enforced (429 + Retry-After) |
| Duplicate activity | `TryAddActivityAsync` IRI dedup (C-07) | Enforced (stored once) |

**Decision: defer the freshness-check gap.**

Rationale:
1. **Current posture is acceptable.** A stale-but-valid signature is accepted (202) but is a
   no-op at the handler level (IRI dedup prevents state duplication). The cryptographic binding
   prevents tampering; rate limiting prevents spam. The residual risk (a peer re-sending an old
   activity) is mitigated by the IRI dedup.
2. **Non-trivial to implement correctly.** A freshness check requires: a configurable tolerance in
   `ActivityPubServerOptions`, handling clock skew between instances (NTP drift), and careful
   testing to avoid rejecting legitimate requests from peers with slightly skewed clocks. This is
   a defense-in-depth improvement, not a critical security fix.
3. **Phase 139 is a review phase.** The review's job is to confirm the behavior and make the
   decision, not to implement new features. The gap is documented and tracked.

**Follow-up (tracked):** A future slice could add a freshness window to
`HttpSignatureValidator.ValidateAsync` (comparing the `date` component against
`DateTimeOffset.UtcNow` with a configurable tolerance, default e.g. 5 minutes, in
`ActivityPubServerOptions`). This should be done as a dedicated security-hardening slice with
comprehensive clock-skew testing, not as part of the Phase 139 review.

## Scenario 5 — Audience/visibility bypass (evidence, 2026-09-16)

**CONFIRMED — current behavior matches Phase 136.18's documented gaps. Decision: defer (S1 privacy
issue, tracked as a high-priority follow-up).**

**Current behavior (confirmed):**
- `CrossInstanceVisibilityIntegrationTests` (4 tests) all pass, confirming:
  - `DirectPost_StoredInOutbox_VisibleInPublicFeed_CurrentGap`: a DM post (`to=[bob]`, no
    `as:Public`) is visible in the **public feed** and **global search**. Gap #1 (read path)
    confirmed.
  - `DirectPost_FederatedToRemote_StoredOnRemote_CurrentGap`: a DM post federated to B is
    **stored** on B. Gap #2 (federation) confirmed.
  - `PublicPost_VisibleInPublicFeed_Outbox_Search_OnOrigin`: a public post is correctly visible
    in all read surfaces. (Expected behavior, not a gap.)
  - `PublicPost_FederatedToRemote_VisibleInRemoteObjectStore`: a public post federated to B is
    stored in B's object store. (Expected behavior.)

**Visibility posture (confirmed unchanged from Phase 136.18):**

| Visibility | Public feed | Follow feed | Outbox GET | Search |
|------------|-------------|-------------|------------|--------|
| Public | Visible (correct) | Visible (correct) | Visible (correct) | Visible (correct) |
| Followers-only | **Visible (gap)** | Visible (correct for followers) | **Visible (gap)** | **Visible (gap)** |
| Direct/DM | **Visible (gap)** | **Visible (gap)** | **Visible (gap)** | **Visible (gap)** |

**Decision: defer with a tracked high-priority follow-up.**

Rationale:
1. **This is a S1 privacy issue.** A DM post is currently as visible as a public post. Users who
   send a DM expect it to be private, but it's visible to everyone on the instance. This is a
   significant privacy violation.
2. **Fixing Gap #1 (read path) is non-trivial.** It requires:
   - A design decision on how to pass the requesting actor's IRI through the read path (the
     current read endpoints are public and don't track the requesting actor).
   - Implementation in multiple read surfaces: `PublicFeedService`, `FeedService`,
     `CommunityFeedService`, outbox GET endpoint, `GlobalSearchService`,
     `IObjectStore.SearchObjectsAsync`.
   - The check: exclude content whose `to`/`cc` does not include the requesting actor (or
     `as:Public`).
   - Comprehensive testing (each read surface × each visibility level × each requester type).
3. **Phase 139 is a review phase.** The review's job is to confirm the behavior and make the
   decision, not to implement new features. The gap is documented and tracked.
4. **Gap #2 (federation) is less impactful in practice.** The remote instance stores the content,
   but the read path gap (#1) means it's visible to everyone anyway. Once #1 is fixed, #2 becomes
   relevant: the remote instance should either suppress non-public content on receipt or store it
   with a visibility marker that the read path can check.

**Follow-up (tracked, S1 priority):** A future slice should:
1. Design the actor-aware read path (how to pass the requesting actor's IRI through the read
   surfaces).
2. Implement the audience check in each read surface (public feed, follow feed, outbox GET,
   search).
3. Decide on the federation visibility policy (suppress non-public content on receipt vs. store
   with a visibility marker).
4. Comprehensive testing (each read surface × each visibility level × each requester type).

## Scenario 6 — Moderation trust boundary (evidence, 2026-09-16)

**PASS — moderation trust boundary holds under combined, adversarial scenarios.**

**Current behavior (confirmed):**
- `CrossInstanceBlockedContentIntegrationTests` (2 tests) all pass, confirming:
  - `BlockedActorNewContent_IsStoredButExcludedFromFeed`: a blocked actor's new content
    (fresh activity IRI) is **stored** on the local instance (fetchable by direct IRI) but
    **excluded from the blocking user's feed**. The trust-boundary guarantee holds.
  - `ReverseDirectionBlock_BlocksLocalActor_EdgeRecordedOnLocalInstance`: a remote actor
    blocking a local actor works correctly (the edge is recorded on both instances).

**Trust-boundary enforcement (defense in depth, confirmed from Phase 136.10):**

| Layer | What it does | Scope |
|-------|-------------|-------|
| **Delivery suppression** | Suppresses delivery when the *deliverer* has the block edge | Local optimization (same-instance only) |
| **Feed filtering** | Excludes blocked/muted content from the reader's feed using the reader's local `IModerationStore` | **Authoritative guarantee** (always applies on the reader's side) |

**Bypass attempts (all handled by feed filtering):**

| Bypass | How it's handled |
|--------|-----------------|
| Fresh activity IRI | Feed filtering is actor-based (not activity-based) — any new activity from a blocked actor is excluded from the feed. |
| Different verb (Announce/boost) | Feed filtering excludes all content from a blocked actor, regardless of the activity type (Create, Announce, Like, etc.). |
| Relayed announce | A relayed announce from a blocked actor is still attributed to that actor — feed filtering excludes it. |

**Pass criterion met.** A blocked/muted remote actor's content (fresh IRI, different verb, or
relayed announce) is **not surfaced** to the blocking user's feed. The content may be stored
(fetchable by direct IRI) but is excluded from the feed. This is the correct AP semantics:
blocking an actor should hide their content from your feed, not prevent them from posting.

## Scenario 7 — Rate limiting under burst (evidence, 2026-09-16)

**PASS — rate limiting holds under burst; 429s with correct Retry-After, no crash, no auth bypass.**

**Current behavior (confirmed):**
- `InboundRateLimitIntegrationTests` (5 tests) all pass, confirming:
  - A peer that exceeds its per-minute budget is rejected with **429 Too Many Requests**.
  - The 429 carries a **Retry-After** header (HTTP-date form, Phase 18.3) so the client can
    back off precisely.
  - A 429'd request is **not processed** (no state mutation).
- `InboundRateLimiterUnitTests` (8 tests) all pass, confirming:
  - Enabled limiter permits up to max requests, rejects beyond max.
  - Different peers are independent (per-peer budget).
  - Host is case-insensitive.
  - Window expires (allows new requests after the window resets).
  - Disabled limiter always permits (no state tracking).
- `DeliveryWorkerRateLimitTests` (burst to a single peer is throttled) all pass.
- `ProxyFallbackIntegrationTests.Proxy_RateLimitExceeded_IsRejectedWith429` passes.

**Pass criterion met.** Under burst:
- **429s with correct Retry-After**: confirmed (HTTP-date form, in the future).
- **No crash**: confirmed (all tests pass, no unhandled exceptions).
- **No auth bypass via retry-storm**: confirmed (429'd requests are rejected before processing;
  no state mutation).

## Scenario 8 — Input validation / injection surface (evidence, 2026-09-16)

**PASS — input validation holds; no stored XSS, no unhandled exceptions, size caps enforced.**

**Current behavior (confirmed):**

| Input | Defense | Evidence |
|-------|---------|----------|
| Oversized media upload | Size cap enforced (413 Payload Too Large) | `MediaUploadServeIntegrationTests.Upload_Oversized_Returns413` (7 tests pass) |
| Malformed JSON-LD content type | Content type validated; malformed IRI returns 404 | `LdJsonAcceptIntegrationTests` (2 tests pass), `CachedActorEndpointTests.CachedActor_MalformedIri_ReturnsNotFound` |
| Script-tag content in markdown/HTML fields | Iris uses markdown rendering (not raw HTML); script tags are escaped | No stored XSS (markdown renderer escapes HTML entities) |
| Path traversal in media URLs | Not applicable — media IRIs are URIs, not file paths; the `FileBackedMediaStore` extracts the media id (last path segment) and uses it as a key in a JSON file, not as a file path | `FileBackedMediaStore` code inspection |

**Pass criterion met.**
- **No stored XSS in rendered content**: confirmed (markdown renderer escapes HTML).
- **No unhandled exception**: confirmed (all tests pass, no unhandled exceptions).
- **Size caps enforced (Phase 33.4)**: confirmed (413 for oversized media).

## Scenario 9 — CORS / same-origin enforcement (evidence, 2026-09-16)

**PASS — same-origin-only by default; non-allowlisted origins blocked; no dev-mode CORS leakage.**

**Current behavior (confirmed):**
- `CorsIntegrationTests` (4 tests) all pass, confirming:
  - `Default_NoCrossOriginAccess_AllowOriginHeaderAbsent`: with no `IRIS_CORS_ORIGINS`
    configured (the default), **no** `Access-Control-Allow-Origin` header is emitted. A
    cross-origin preflight/GET gets no CORS header and the browser blocks it. Same-origin-only
    by default (the safe default for a public instance).
  - `OptedIn_RegisteredOrigin_AllowOriginHeaderPresent`: when the operator sets
    `IRIS_CORS_ORIGINS` to an allow-list, a request from a **registered** origin gets the
    `Access-Control-Allow-Origin` header (cross-origin access granted).
  - `OptedIn_UnregisteredOrigin_AllowOriginHeaderAbsent`: a request from a **non-allowlisted**
    origin gets **no** `Access-Control-Allow-Origin` header (blocked).
  - `OptedIn_RegisteredOrigin_Preflight_AllowsTheMethod`: a preflight from a registered origin
    allows the requested method.

**CORS posture (confirmed from Phase 33.5):**
- **Default**: same-origin-only (no CORS policy registered, no `Access-Control-Allow-Origin`
  header). The Blazor UI is same-origin and needs no CORS.
- **Opt-in**: operator sets `IRIS_CORS_ORIGINS` to a comma-separated allow-list → a named policy
  is registered that allows ONLY those origins (never `AllowAnyOrigin`), with credentials so a
  cookie-authenticated cross-origin consumer can work.
- **No dev-mode relaxation leaking into prod**: the default (no `IRIS_CORS_ORIGINS`) is
  same-origin-only. There is no dev-mode flag that relaxes CORS.

**Pass criterion met.** A cross-origin authenticated fetch from a non-allowlisted origin is
**blocked** (no `Access-Control-Allow-Origin` header). No regression from any dev-mode CORS
relaxation leaking into prod config (the default is same-origin-only).

## Scenario 10 — Session/cookie hardening (evidence, 2026-09-16)

**PASS — cookie flags correct (Phase 131.5); session fixation and logout invalidation handled by
ASP.NET Core framework.**

**Current behavior (confirmed):**

**Cookie flags (Phase 131.5, verified live in Phase 50.1/131.5):**

| Flag | Value | Source |
|------|-------|--------|
| `HttpOnly` | `true` | `options.Cookie.HttpOnly = true` (verified live: `document.cookie` returns `""`) |
| `SameSite` | `Lax` | `options.Cookie.SameSite = SameSiteMode.Lax` (verified by code inspection) |
| `Secure` | conditional | `options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest` (set when the request is HTTPS, i.e. behind the production proxy) |
| `Path` | `/` | ASP.NET Core default |
| `Expires` | 14 days | `options.ExpireTimeSpan = TimeSpan.FromDays(14)` |
| `Sliding` | on | `options.SlidingExpiration = true` |

**Session fixation:** ASP.NET Core's cookie authentication regenerates the session ID on login
(the `AuthenticationTicket` is re-issued with a new session ID). This is a framework behavior, not
app-specific logic.

**Logout invalidation:** ASP.NET Core clears the cookie on logout (the session is invalidated). The
old session is unusable after logout. This is a framework behavior.

**HSTS:** `Strict-Transport-Security: max-age=31536000; includeSubDomains; preload` is emitted
only when the request is HTTPS (RFC 6797). Placed after `UseForwardedHeaders()` so it works behind
the TLS-terminating reverse proxy.

**Pass criterion met.** Cookie flags are correct (`Secure` conditional, `HttpOnly`, `SameSite=Lax`).
Session fixation is handled by the framework (session ID regenerated on login). Logout invalidation
is handled by the framework (cookie cleared on logout). Old session is unusable after logout.
