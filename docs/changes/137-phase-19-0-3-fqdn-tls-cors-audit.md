# 137 — Phase 19.0.3: FQDN + TLS + CORS audit

## Summary

Phase 19.0.3 audits the live stack over the public FQDNs (`https://iris-dev1.luit.ink` / `https://iris-dev2.luit.ink`) and fixes whatever is miswired. The audit found one gap: the CORS origins list (`.env` → `IRIS_CORS_ORIGINS`) only included the local UI origins (`http://localhost:8090`, `http://localhost:8088`) but not the public FQDN origins, so a browser loading the explorer through the reverse proxy (origin `https://iris-dev1.luit.ink`) would be blocked by CORS. Everything else was correctly wired.

## Audit results

| Check | Result | Detail |
|---|---|---|
| WebFinger on iris-dev1.luit.ink | PASS | `acct:alice@iris-dev1.luit.ink` → `https://iris-dev1.luit.ink/ap/v1/u/alice` |
| WebFinger on iris-dev2.luit.ink | PASS | `acct:alice@iris-dev2.luit.ink` → `https://iris-dev2.luit.ink/ap/v1/u/alice` |
| Advertised IRIs (clean, no port) | PASS | All actor/community IRIs are `https://iris-devN.luit.ink/ap/v1/...` (port 443 canonicalized away) |
| CORS preflight from public FQDN | **FAIL → FIXED** | `Access-Control-Allow-Origin` was missing for `https://iris-devN.luit.ink` origins; fixed by adding them to `IRIS_CORS_ORIGINS` |
| CORS preflight from local UI | PASS | `http://localhost:8090` and `http://localhost:8088` already in the list |
| PeerBase federation (in-network) | PASS | The signed cross-container Follow (a→b) works over the Docker network; iris-b validates the signature by resolving iris-a's actor document via the in-network `Iris__PeerBase` |
| PeerBase federation (public FQDN) | PASS (by design) | The `FederatedActorDocumentFetcher` dials the target actor IRI directly (not the PeerBase URL). The PeerBase is a hint for the fetcher's client factory, not a routing config. The in-network PeerBase is sufficient because the delivery worker dials the target's in-network inbox, and the signature validation resolves the sender's key from the sender's actor document (which is fetched by the advertised IRI, reachable over the public FQDN) |
| Smoke test (`scripts/docker-smoke-test.sh`) | **FAIL → FIXED** | The smoke test's WebFinger checks hard-coded the in-network IRI; updated to accept the advertised IRI (in-network or public FQDN). The signed follow now uses the advertised actor IRIs (the keyId must match the actor's `publicKey.id`, which is the advertised IRI + `#key-1`) |

## What changed

### `.env`

- `IRIS_CORS_ORIGINS` now includes the public FQDN origins: `https://iris-dev1.luit.ink,https://iris-dev2.luit.ink` (in addition to the existing `http://localhost:8090,http://localhost:8088`).

### `scripts/docker-smoke-test.sh`

- `check_webfinger`: the actor IRI assertion now accepts either the in-network IRI or the advertised (public FQDN) IRI, resolved via `grep -oE 'https?://[^"]+/ap/v1/u/<handle>'`.
- Cross-container reachability check: same dual-IRI acceptance.
- Signed follow: the actor/target IRIs are now resolved from each instance's WebFinger response (the advertised IRIs) rather than hard-coded in-network addresses. The signing identity (actorIri + keyIdIri) uses the advertised IRIs — the keyId must match the actor's `publicKey.id`. The POST URL stays in-network (the signer runs inside iris-a).
- Proxy fallback: the grep for the relayed actor document now accepts either the advertised IRI or the in-network IRI.

## Decisions

- **PeerBase stays in-network.** The `Iris__PeerBase` env var is a hint for the `FederatedActorDocumentFetcher`'s client factory, not a routing config. The fetcher dials the target actor IRI directly (the advertised IRI, reachable over the public FQDN or the in-network address). The delivery worker dials the target's in-network inbox (the IRI stored in the follow edge). The in-network PeerBase is sufficient and avoids a TLS handshake inside the Docker network. Changing it to the public FQDN would work but is unnecessary and would slow down in-network federation.
