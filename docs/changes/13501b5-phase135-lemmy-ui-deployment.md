# 135.1b(5) — Deploy the Lemmy UI (frontend) for visual interop verification

**Date:** 2026-09-14
**Slice:** follow-up to 135.1 — "deploy the Lemmy UI so we can log in and verify interop"
**Commits:** *(compose + docs)*

## What was deployed

The `dessalines/lemmy:0.19.20` image is **backend-only** (just the `lemmy_server` binary — API +
ActivityPub, no HTML; `GET /` returns the JSON-LD `Application` node document, and `/login`,
`/create-post`, etc. 404). The Lemmy web UI is a separate SvelteKit app, so a `lemmy-ui` service was
added to `lemmy/docker-compose.yml`:

- **Image:** `dessalines/lemmy-ui:0.19.20` (pulled from Docker Hub; the official `ghcr.io/lemmynet/*`
  images are blocked from this host's egress, and no full UI-bundled `lemmy` image exists).
- **Config:** `LEMMY_UI_LEMMY_INTERNAL_HOST` (the backend URL the UI proxies `/api/*` to),
  `LEMMY_UI_LEMMY_EXTERNAL_HOST` (`https://iris-dev2.luit.ink`), `LEMMY_UI_HTTPS`,
  `LEMMY_UI_DEBUG`. Listens on 1234; exposed on host **8083** (next to the backend's 8082).
- **Network:** the shared `irisweb_iris-web-net`, so it reaches the backend by container name.

The service deploys and starts (`Lemmy-ui v0.19.20 started listening on http://0.0.0.0:1234`).

## Blocker: the SvelteKit SSR returns 500 on every page

Despite deploying cleanly, `GET /` (and `GET /api/v3/site`, which the UI proxies to the backend)
returns **HTTP 500** with `site_res: undefined` in the injected `isoData`. This holds across every
config combination tried:

- `LEMMY_UI_HTTPS=true` (UI proxies to the **external** host) and `false` (proxies to the
  **internal** host).
- `LEMMY_UI_LEMMY_INTERNAL_HOST` set to the internal container (`http://lemmy-lemmy-1:8536`), the
  external FQDN (`https://iris-dev2.luit.ink`), and the external FQDN with
  `NODE_TLS_REJECT_UNAUTHORIZED=0` (ruling out a cert-trust failure).

**Ruled out:** the container **can** reach the backend — `node fetch` from inside `lemmy-lemmy-ui-1`
succeeds against both `http://lemmy-lemmy-1:8536/api/v3/site` and
`https://iris-dev2.luit.ink/api/v3/site` (both return the site JSON). Node is v26.5.0. The 500 is a
SvelteKit SSR error the server swallows (no stack trace in the logs even with `LEMMY_UI_DEBUG=true`);
the build is pure-SSR (no static `index.html` in `/app/dist`), so it cannot be served as a static
site instead. This is a `dessalines/lemmy-ui:0.19.20` SSR issue not yet root-caused.

**Next (to finish the UI):** capture the actual SSR exception (e.g. patch the SvelteKit server to
log `error.stack`, or run the `load` fetch manually in the container) and fix it — likely a response
shape / origin / cookie handling mismatch in this standalone build. Once `/` renders, login + posting
work in the browser.

## Interop is verifiable via the API right now (the UI is not required)

While the UI SSR is fixed, the Lemmy **API** is fully usable to log in and verify interop
(`lemmyadmin` / `lemmy-interop-admin-pw`, backend on host 8082 or `https://iris-dev2.luit.ink`):

```bash
# 1. Log in (the token field is `jwt`, not `token`)
JWT=$(curl -s -X POST https://iris-dev2.luit.ink/api/v3/user/login \
  -H 'Content-Type: application/json' \
  -d '{"username_or_email":"lemmyadmin","password":"lemmy-interop-admin-pw"}' \
  | python3 -c 'import sys,json;print(json.load(sys.stdin)["jwt"])')

# 2. List communities (the Iris community 'owner-test-5428' shows as a known remote community)
curl -s "https://iris-dev2.luit.ink/api/v3/community/list?limit=10" -H "Authorization: Bearer $JWT"

# 3. Post a Note to the Lemmy community 'test' (community_id from the list)
curl -s -X POST https://iris-dev2.luit.ink/api/v3/post \
  -H "Authorization: Bearer $JWT" -H 'Content-Type: application/json' \
  -d '{"community_id":2,"name":"Hello from Lemmy","body":"...","post_form":"body"}'

# 4. Check the post landed
curl -s "https://iris-dev2.luit.ink/api/v3/post/list?community_id=2&limit=5" -H "Authorization: Bearer $JWT"
```

Note (from 135.1b(4)): a Lemmy-side post only federates **to Iris** once the Iris community's follow
is registered on Lemmy's side, which is currently blocked by the signature interop failure. So the
API path verifies the Lemmy side of the interop; the cross-instance content flow still needs the
135.2 signature fix.
