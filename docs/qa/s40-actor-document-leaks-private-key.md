# S40 — Actor document leaks private key to authenticated users

**Severity:** S1 (Critical)
**Status:** CLOSED (FALSE POSITIVE — Pass 259, 2026-09-21)
**First seen:** Pass 255 (2026-09-21)
**Build:** `401c08b5`

## Summary

The ActivityPub actor document (`GET /ap/v1/u/<handle>`) includes the `privateKey` field in its JSON response **for the owner only**. This is by design — the owner-only extension is how the client authenticator (`BasicAuthClientAuthenticator` / `OAuth2ClientAuthenticator`) loads the private key to sign federation requests.

## Resolution (Pass 259)

**FALSE POSITIVE.** The ownership check in `ActorDocumentHandler` (line 1613-1620 of `ActivityPubServerExtensions.cs`) correctly gates the `privateKey` extension:

1. Basic auth path: `credentialValidator.TryValidateAsync(actorIri, authorization, ct)` — only returns non-null when the credentials match the requested actor.
2. Cookie auth path: `context.User.FindFirst("actor_iri").Value == actorIri.Value` — only matches when the logged-in user IS the requested actor.

**Verification:** Logged in as `ii-a2`, fetched `/ap/v1/u/ii-a1` → `privateKey` **absent**. Fetched `/ap/v1/u/ii-a2` (self) → `privateKey` **present**. The gate works correctly.

The original QA repro (Pass 255) logged in as `ii-a1` and fetched `/ap/v1/u/ii-a1` — the owner fetching their own document. This is the intended behavior.

## Impact (as originally reported — now resolved)

No actual security impact. The `privateKey` field is only visible to the owner, which is the design intent.

## Code Reference

- `src/Iris.Server/ActivityPubServerExtensions.cs:1604-1620` — ownership check
- `src/Iris.Server/ActivityPubServerExtensions.cs:7207-7219` — `BuildActorDocumentAsync` adds `privateKey` only when `authenticatedHandle` is non-null
- `src/Iris.Server/Security/BasicAuthCredentialValidator.cs` — Basic auth validation
- `src/Iris.Client/Auth/BasicAuthClientAuthenticator.cs` — client reads `privateKey` from owner-authenticated document

## Related

- S35 (remote actor discovery 404) — separate issue.
- Pass 252 — `openRegistrations=false` flag not enforced (separate issue).
