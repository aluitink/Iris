# 107 — Phase 15.4: Update sample + deployment docs for the new auth flow

> 2026-08-30 · Phase 15.4 · docs-only

## What was built

Updated the sample + deployment docs to reflect the new OAuth2/Bearer auth flow (Phase 15) alongside
the existing Basic auth:

- **`samples/SampleServer/README.md`**:
  - Added the OAuth2 token exchange + revocation endpoints to the feature table.
  - Added an "OAuth2 (Bearer token) auth" note in the logon credential section, explaining that the
    `IActorCredentialValidator` seam is swappable (the sample registers `BasicAuthCredentialValidator`
    by default; a host app can register `BearerTokenCredentialValidator` + `IOAuthTokenStore`).

- **`docs/reference/DEPLOYMENT_PREP.md`**:
  - Added the OAuth2 token + revoke endpoints to the route table.
  - Updated the actor document row to mention Bearer token auth (not just Basic auth).
  - Updated the bootstrap runbook to reference Phase 15 (not Phase 14+) for the auth swap.

## Decision: docs-only (no code change)

Phase 15.4 is explicitly a docs task: "Update the sample + deployment docs for the new auth flow."
The code (the `BearerTokenCredentialValidator`, `OAuth2ClientAuthenticator`, `IOAuthTokenStore`,
token + revoke + refresh endpoints) was already built in 15.1–15.3. This slice just updates the docs
so an operator deploying an Iris instance knows about the OAuth2 option and the new endpoints.

## Test count

No code change (docs-only). 1017 tests, 0 failures, 0 warnings (unchanged).

## Files changed

- `samples/SampleServer/README.md` — OAuth2 endpoints in feature table + auth note
- `docs/reference/DEPLOYMENT_PREP.md` — OAuth2 endpoints in route table + auth references updated
