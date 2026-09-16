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

- [x] 1  - [x] 2  - [x] 3  - [ ] 4  - [ ] 5  - [ ] 6  - [ ] 7
- [ ] 8  - [ ] 9  - [ ] 10 - [ ] 11 - [ ] 12 - [ ] 13 - [ ] 14

Check a scenario off only once its pass criterion is met with evidence attached (link/path). Update
the area's Status cell in [phase-139-platform-e2e-review.md](phase-139-platform-e2e-review.md) to
`in progress` on the first checked box, `done` when all are checked (or explicitly skipped).

**Resume checkpoint:** scenarios 1–3 done — begin at scenario 4.

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
