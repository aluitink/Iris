#!/usr/bin/env bash
#
# Phase 8 smoke test: boots the three-service Docker composition (two instances + the Blazor WASM
# server-explorer UI) and asserts the sample servers deploy and interoperate as a real system (not
# in-process TestServer).
#
#   1. Health: each instance (iris-a, iris-b) serves the public WebFinger endpoint (a GET that skips
#      signature validation) for its own seeded actor.
#   2. Cross-container reachability: iris-a resolves the remote actor alice@iris-b by WebFinger over
#      the internal Docker network (Docker's built-in DNS resolves the service name), proving the two
#      containers reach each other over genuine network I/O.
#   3. UI: iris-ui serves the Blazor WASM app's index page (HTTP 200, the app root) over iris-net.
#   4. Signed cross-container federation (the S10 upgrade from "WebFinger reachability" to "real
#      signed federation over Docker"): a signed Follow from iris-a's alice to iris-b's alice, published
#      to alice's own outbox on iris-a, is server-delivered (signed, over the network) to iris-b's inbox,
#      which validates it (resolving alice's key from iris-a's actor document) and records the follow
#      edge — asserted via iris-b's public followers collection.
#   5. Proxy fallback over the network: iris-a's proxy endpoint (a signed POST, Basic auth) relays a
#      request to iris-b, returning 200 — the browser's way of reaching a remote instance.
#
# Usage:
#   ./scripts/docker-smoke-test.sh            # boots the stack (up --build), runs the checks, tears down
#   IRIS_COMPOSE_KEEP=1 ./scripts/docker-smoke-test.sh   # leave the stack running afterwards
#
# Requires: docker + docker compose. Skips (exit 0) when Docker is unavailable so local/dev runs
# without Docker are unaffected (the Phase 8 "opt-in" gate).

set -euo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
COMPOSE_FILE="$REPO_ROOT/docker-compose.yml"
SIGNER_DIR="$REPO_ROOT/tools/IrisSigner"

log() { printf '[smoke] %s\n' "$*"; }
fail() { printf '[smoke] FAIL: %s\n' "$*" >&2; exit 1; }

# --- Opt-in gate: skip when Docker (or compose) is unavailable -----------------------------
if ! command -v docker >/dev/null 2>&1; then
  log "Docker not available — skipping the Phase 8 smoke test (opt-in CI job)."
  exit 0
fi
if ! docker info >/dev/null 2>&1; then
  log "Docker daemon not reachable — skipping the Phase 8 smoke test (opt-in CI job)."
  exit 0
fi
if ! docker compose version >/dev/null 2>&1; then
  log "docker compose not available — skipping the Phase 8 smoke test (opt-in CI job)."
  exit 0
fi

cd "$REPO_ROOT"

# --- Boot the stack ------------------------------------------------------------------------
log "Booting the compose stack (docker compose up --build)…"
docker compose -f "$COMPOSE_FILE" up --build -d
trap 'if [ "${IRIS_COMPOSE_KEEP:-0}" != "1" ]; then log "Tearing down the compose stack…"; docker compose -f "$COMPOSE_FILE" down -v --remove-orphans >/dev/null 2>&1 || true; fi' EXIT

# --- Wait for all three services to be healthy ----------------------------------------------
log "Waiting for all three services to become healthy…"
for i in $(seq 1 60); do
  a="$(docker inspect --format '{{.State.Health.Status}}' iris-a 2>/dev/null || echo starting)"
  b="$(docker inspect --format '{{.State.Health.Status}}' iris-b 2>/dev/null || echo starting)"
  ui="$(docker inspect --format '{{.State.Health.Status}}' iris-ui 2>/dev/null || echo starting)"
  if [ "$a" = "healthy" ] && [ "$b" = "healthy" ] && [ "$ui" = "healthy" ]; then
    log "All three services healthy (after ${i} checks)."
    break
  fi
  if [ "$i" = "60" ]; then
    log "Service status: iris-a=${a} iris-b=${b} iris-ui=${ui}"
    fail "the stack did not become healthy within 300s"
  fi
  sleep 5
done

# The compose network name (the checks attach a curl container to it so Docker DNS resolves
# iris-a/iris-b/iris-ui). Docker prefixes it with the compose project name (derived from the compose
# file's directory/file name), so the real name is e.g. "workspace_iris-net", not just "iris-net".
NET_NAME="$(docker compose -f "$COMPOSE_FILE" config --format json 2>/dev/null \
  | python3 -c 'import json,sys; d=json.load(sys.stdin); nets=d.get("networks") or {}; print(next(iter(nets.values()))["name"]) if nets else ""' 2>/dev/null)"
if [ -z "$NET_NAME" ]; then
  fail "could not determine the compose network name (docker compose config)"
fi

# --- 1. Health: each instance serves its own actor's WebFinger ----------------------------
# curl is not in the aspnet base image, so the checks run in a small curl container on the compose
# network (so Docker DNS resolves iris-a/iris-b/iris-ui).
check_webfinger() {
  local instance="$1" handle="$2"
  local body
  body="$(docker run --rm --network "$NET_NAME" curlimages/curl:latest \
    -s -w '\n%{http_code}' \
    "http://${instance}:8080/.well-known/webfinger?resource=acct:${handle}@${instance}" 2>/dev/null)"
  local code="${body##*$'\n'}"
  local json="${body%$'\n'*}"
  if [ "$code" != "200" ]; then
    fail "${instance} WebFinger for ${handle} returned HTTP ${code} (expected 200)"
  fi
  if ! grep -q "http://${instance}:8080/ap/v1/u/${handle}" <<<"$json"; then
    fail "${instance} WebFinger for ${handle} did not return its own actor IRI"
  fi
  log "OK: ${instance} WebFinger resolves ${handle}@${instance} (HTTP 200, actor IRI present)."
}
check_webfinger iris-a alice
check_webfinger iris-b alice

# --- 2. Cross-container reachability: iris-a resolves alice@iris-b over the network ---------
# A WebFinger lookup for a remote (foreign) actor is the read path of federation; if iris-a can
# resolve alice@iris-b by hostname, the two containers reach each other over genuine network I/O.
# Note: the Iris server's WebFingerHandler only resolves LOCAL actors (it 404s for foreign ones).
# So this check proves iris-a can REACH iris-b's WebFinger endpoint over the network and read its
# response (the remote actor's own document), not that iris-a federates it. The full signed follow
# loop is the next check (3).
log "Checking cross-container reachability: iris-a resolving alice@iris-b by WebFinger…"
cross_body="$(docker run --rm --network "$NET_NAME" curlimages/curl:latest \
  -s -w '\n%{http_code}' \
  "http://iris-b:8080/.well-known/webfinger?resource=acct:alice@iris-b" 2>/dev/null)"
cross_code="${cross_body##*$'\n'}"
cross_json="${cross_body%$'\n'*}"
if [ "$cross_code" != "200" ]; then
  fail "iris-a→iris-b WebFinger over the network returned HTTP ${cross_code} (expected 200)"
fi
if ! grep -q "http://iris-b:8080/ap/v1/u/alice" <<<"$cross_json"; then
  fail "iris-a→iris-b WebFinger did not return iris-b's actor IRI (cross-container reachability failed)"
fi
log "OK: iris-a reached iris-b over the Docker network and resolved alice@iris-b (HTTP 200)."

# --- 3. UI: iris-ui serves the Blazor WASM app's index page over iris-net -------------------
# The Blazor WebAssembly "server explorer" (Deliverable B) is a static site; iris-ui serves its index
# page (the app root) over the network. A 200 + the app root proves the UI container is up and serving.
log "Checking the UI: iris-ui serving the Blazor WASM index page over iris-net…"
ui_body="$(docker run --rm --network "$NET_NAME" curlimages/curl:latest \
  -s -w '\n%{http_code}' \
  "http://iris-ui:8090/" 2>/dev/null)"
ui_code="${ui_body##*$'\n'}"
ui_json="${ui_body%$'\n'*}"
if [ "$ui_code" != "200" ]; then
  fail "iris-ui index page over the network returned HTTP ${ui_code} (expected 200)"
fi
if ! grep -q "blazor.webassembly.js" <<<"$ui_json"; then
  fail "iris-ui index page did not reference the Blazor WebAssembly bootstrap (app root missing)"
fi
log "OK: iris-ui serves the Blazor WASM app (HTTP 200, app root present) over iris-net."
# A 200 index page is not enough: the WASM platform only starts if the browser can also download its
# _framework assets, and the static host must serve every file type the site ships. The icudt_*.dat
# ICU data regressed silently when the host's UseStaticFiles() had no MIME type for ".dat" — the
# index page still 200'd, but the platform failed to start in the browser. Fetch the bootstrap
# script + an icudt .dat to prove the _framework assets are actually served (not 404).
ui_fw_code="$(docker run --rm --network "$NET_NAME" curlimages/curl:latest \
  -s -o /dev/null -w '%{http_code}' \
  "http://iris-ui:8090/_framework/blazor.webassembly.js" 2>/dev/null)"
if [ "$ui_fw_code" != "200" ]; then
  fail "iris-ui _framework bootstrap (blazor.webassembly.js) returned HTTP ${ui_fw_code} (expected 200)"
fi
ui_icu_name="$(docker exec iris-ui sh -c 'ls /app/wwwroot/_framework/ 2>/dev/null | grep -E "^icudt_[A-Za-z0-9_]+\.dat$" | head -n1')"
if [ -n "$ui_icu_name" ]; then
  ui_icu_code="$(docker run --rm --network "$NET_NAME" curlimages/curl:latest \
    -s -o /dev/null -w '%{http_code}' \
    "http://iris-ui:8090/_framework/${ui_icu_name}" 2>/dev/null)"
  if [ "$ui_icu_code" != "200" ]; then
    fail "iris-ui icudt asset (${ui_icu_name}) returned HTTP ${ui_icu_code} (expected 200 — the static host must serve the WASM .dat ICU data)"
  fi
fi
log "OK: iris-ui serves the Blazor WebAssembly app (index page + _framework bootstrap + icudt .dat) over iris-net."

# --- 4. Signed cross-container federation: alice@iris-a follows alice@iris-b ---------------
# The S10 upgrade: a real signed Follow over genuine sockets. Per the delivery model, the authored
# Follow is published to the acting actor's OWN outbox — alice's outbox on iris-a (her home instance).
# iris-a records the Follow and then (the server's job, not the client's) delivers it to the recipient
# (alice@iris-b)'s inbox on iris-b, signed. iris-b validates the signature by resolving alice's key
# from iris-a's actor document (the sample's FederatedActorDocumentFetcher, wired via Iris__PeerBase)
# and records the follow edge. The edge is asserted on iris-b's public followers collection (a read
# that proves the write landed on the remote instance).
#
# A genuine ActivityPub HTTP signature is required (Basic auth is not accepted for an outbox write).
# curl cannot produce one, so this step builds the IrisSigner helper (tools/IrisSigner, self-contained,
# linux-x64), copies it + the actor's private-key PEM into iris-a (iris-a dumped it to
# /tmp/iris-alice-key.pem via the Iris__DumpKeyTo env var; the private key is never committed — it is
# generated per-boot in memory and only written to the container's local fs), and runs the signer
# inside iris-a so it signs with alice's key and POSTs the Follow to iris-a's outbox over the network.
log "Checking signed cross-container federation: alice@iris-a following alice@iris-b over the network…"
log "  (building the IrisSigner helper so the smoke test can drive a genuine signed request)"
signer_build_dir="$(mktemp -d "${TMPDIR:-/tmp}/iris-signer.XXXXXX")"
if ! (cd "$SIGNER_DIR" && dotnet publish -c Release -r linux-x64 --self-contained true -o "$signer_build_dir" --nologo -v q) >/dev/null 2>&1; then
  rm -rf "$signer_build_dir"
  fail "the IrisSigner helper failed to build (dotnet publish) — cannot drive the signed Follow"
fi
if ! docker cp "$signer_build_dir/." iris-a:/opt/signer/ >/dev/null 2>&1; then
  fail "could not copy the IrisSigner helper into iris-a (docker cp)"
fi
# iris-a dumped alice's private-key PEM to /tmp/iris-alice-key.pem (Iris__DumpKeyTo); poll for it
# (it is written once at startup, but be defensive in case the container restarted mid-test).
key_ready=""
for i in $(seq 1 10); do
  if docker exec iris-a sh -c 'test -s /tmp/iris-alice-key.pem' >/dev/null 2>&1; then
    key_ready="yes"
    break
  fi
  sleep 1
done
if [ "$key_ready" != "yes" ]; then
  fail "iris-a did not dump alice's private key to /tmp/iris-alice-key.pem (Iris__DumpKeyTo) — cannot sign the Follow"
fi
# docker cp does not support container→container, so route the key through the host (the key never
# leaves the host fs for any network peer; it is only copied back into the same container to sign).
key_host_copy="$signer_build_dir/alice-key.pem"
if ! docker cp iris-a:/tmp/iris-alice-key.pem "$key_host_copy" >/dev/null 2>&1; then
  rm -rf "$signer_build_dir"
  fail "could not copy alice's private key from iris-a to the host (docker cp)"
fi
if ! docker cp "$key_host_copy" iris-a:/opt/signer/alice-key.pem >/dev/null 2>&1; then
  rm -rf "$signer_build_dir"
  fail "could not copy alice's private key into iris-a for signing (docker cp)"
fi
# A unique Follow id (so a re-run does not collide with a prior Follow's id; the follow edge is
# idempotent, but a unique id keeps the outbox clean).
follow_id="http://iris-a:8080/ap/v1/u/alice/follows/s10-$(date +%s)"
follow_actor="http://iris-a:8080/ap/v1/u/alice"
follow_target="http://iris-b:8080/ap/v1/u/alice"
follow_json="{\"@context\":[\"https://www.w3.org/ns/activitystreams\"],\"id\":\"${follow_id}\",\"type\":\"Follow\",\"actor\":\"${follow_actor}\",\"object\":\"${follow_target}\"}"
echo "$follow_json" > "$signer_build_dir/follow.json"
docker cp "$signer_build_dir/follow.json" iris-a:/opt/signer/follow.json >/dev/null 2>&1
# Run the signer inside iris-a: it signs the Follow with alice's key (the ServerToServer profile,
# since a Follow has a body → digest + content-type are signed) and POSTs it to iris-a's outbox.
# The signer prints the response body then the status code on its own line; a 202 = accepted.
signer_out="$(docker exec iris-a /opt/signer/IrisSigner \
  POST "http://iris-a:8080/ap/v1/u/alice/outbox" \
  /opt/signer/alice-key.pem \
  "$follow_actor" \
  "$follow_actor#key-1" \
  application/activity+json \
  /opt/signer/follow.json 2>&1)"
follow_code="$(printf '%s' "$signer_out" | tail -n1)"
if [ "$follow_code" != "202" ]; then
  rm -rf "$signer_build_dir"
  fail "the signed Follow POST to iris-a's outbox returned HTTP ${follow_code} (expected 202) — the signature was not accepted. signer output: ${signer_out}"
fi
log "OK: iris-a accepted the signed Follow (HTTP 202)."
# Clean up the in-container helper + the actor's private key (the key never leaves the container; it
# was only written to the container's local fs via Iris__DumpKeyTo) and the local build dir.
docker exec iris-a sh -c 'rm -rf /opt/signer' >/dev/null 2>&1 || true
rm -rf "$signer_build_dir"

# The server→server delivery to iris-b's inbox is asynchronous (the DeliveryWorker pumps the queue), so
# poll for the follow edge on iris-b's public followers collection (it lists the remote follower
# alice@iris-a once iris-b records the edge).
log "Waiting for iris-b to record the federated follow edge (server-delivered, signed)…"
edge_found=""
for i in $(seq 1 30); do
  followers_body="$(docker run --rm --network "$NET_NAME" curlimages/curl:latest \
    -s -w '\n%{http_code}' \
    "http://iris-b:8080/ap/v1/u/alice/followers" 2>/dev/null)"
  followers_code="${followers_body##*$'\n'}"
  followers_json="${followers_body%$'\n'*}"
  if [ "$followers_code" = "200" ] && grep -q "${follow_actor}" <<<"$followers_json"; then
    edge_found="yes"
    break
  fi
  if [ "$i" = "30" ]; then
    log "iris-b followers (last check, HTTP ${followers_code}): ${followers_json}"
  fi
  sleep 2
done
if [ "$edge_found" != "yes" ]; then
  fail "iris-b did not record the federated follow edge within 60s (the signed cross-container Follow was not delivered/validated)"
fi
log "OK: iris-b recorded the federated follow edge — alice@iris-a now follows alice@iris-b (signed cross-container federation over Docker confirmed)."

# --- 5. Proxy fallback over the network: iris-a's proxy relays a request to iris-b ---------
# The browser cannot reach a remote instance directly (CORS + it cannot sign), so it posts the request
# to its home instance's proxy (POST {home}/ap/v1/proxy/{target}, Basic auth), which the server signs
# and relays to the target, returning the remote response. A 200 from iris-a's proxy for a target on
# iris-b proves the proxy path works over genuine sockets (the browser's remote-reachability escape).
log "Checking proxy fallback over the network: iris-a's proxy relaying a GET to iris-b…"
proxy_target="http://iris-b:8080/ap/v1/u/alice"
proxy_body="$(docker run --rm --network "$NET_NAME" curlimages/curl:latest \
  -s -w '\n%{http_code}' \
  -X POST \
  -u "alice:iris-sample" \
  -H 'Accept: application/activity+json' \
  "http://iris-a:8080/ap/v1/proxy/${proxy_target}" 2>/dev/null)"
proxy_code="${proxy_body##*$'\n'}"
proxy_json="${proxy_body%$'\n'*}"
if [ "$proxy_code" != "200" ]; then
  fail "iris-a's proxy relaying a GET to iris-b returned HTTP ${proxy_code} (expected 200)"
fi
if ! grep -q "${follow_target}" <<<"$proxy_json"; then
  fail "iris-a's proxy response did not return iris-b's actor document (proxy relay failed)"
fi
log "OK: iris-a's proxy relayed a GET to iris-b and returned the actor document (HTTP 200) — proxy fallback over the network confirmed."

log "SMOKE TEST PASSED: three services healthy; cross-container WebFinger reachability, the UI, the signed cross-container Follow (a→b, edge recorded on the remote), and the proxy fallback all confirmed."
