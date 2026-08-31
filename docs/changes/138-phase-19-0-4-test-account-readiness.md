# 138 — Phase 19.0.4: Test-account readiness

## Summary

Phase 19.0.4 confirms `@RayvenMX@mastodon.world` is resolvable via WebFinger from our instances, its actor document fetches + key validates, and our sample actors' Basic-auth logon works from the public UI origin. This is a live verification/audit task — no code changes were needed. All checks pass.

## Verification results

| Check | Result | Detail |
|---|---|---|
| WebFinger resolution (client-side) | PASS | `acct:RayvenMX@mastodon.world` → `https://mastodon.world/users/RayvenMX` |
| Actor document fetch | PASS | `type: Person`, `id: https://mastodon.world/users/RayvenMX` |
| Key validates | PASS | `publicKeyPem` present, valid PEM format, key id `https://mastodon.world/users/RayvenMX#main-key` |
| Inbox/outbox URLs | PASS | `inbox: https://mastodon.world/users/RayvenMX/inbox`, `outbox: https://mastodon.world/users/RayvenMX/outbox` |
| Basic-auth logon (alice, public FQDN) | PASS | `privateKey` extension returned on authenticated request, `keyAlgorithm: rsa` |
| Basic-auth logon (bob, public FQDN) | PASS | `privateKey` extension returned, `keyAlgorithm: rsa` |
| Wrong password rejected | PASS | `privateKey` not returned, public document only |
| CORS on authenticated request | PASS | `Access-Control-Allow-Origin: https://iris-dev1.luit.ink`, `Access-Control-Allow-Credentials: true` |

## Account capabilities (known-good external reference)

- **Handle:** `@RayvenMX@mastodon.world`
- **Actor IRI:** `https://mastodon.world/users/RayvenMX`
- **Type:** `Person`
- **Key ID:** `https://mastodon.world/users/RayvenMX#main-key`
- **Posting:** Yes — 125 outbox items (all `Create` activities)
- **Follows:** Has followers/following collections
- **Inbox:** `https://mastodon.world/users/RayvenMX/inbox`
- **Outbox:** `https://mastodon.world/users/RayvenMX/outbox` (paginated, 20 items per page)

This account serves as the known-good external reference for Phase 19.1+ live interop testing: a real Mastodon account on a real instance that our sample servers can federate with (follow, receive deliveries, resolve keys).
