# 138.1 — Local Lemmy environment audit & refresh

Phase 138 slice 138.1 ([plan](../plans/phase-138-lemmy-community-integration.md)): confirm the local
Lemmy stack boots clean from the current images/config, reconcile the baked hostname against the
FQDN this pass will use, and confirm one clean admin user + one clean community exist.

## What was done

This was a verification pass, not a rebuild — the stack (up since 2026-09-14, `lemmy-1` +
`lemmy-db-1` + `lemmy-proxy-1` + `ui-1`) was found in a clean, healthy state and re-verified
end-to-end. No `docker compose` restart was needed, so no risk to the existing `interop`
community's DB state (which 135.1b already exercised).

## Findings

1. **`GET /api/v3/site` returns 200** on both the host port (`http://localhost:8091`) and the
   external FQDN (`https://lemmy.luit.ink`). `federation_enabled: true`, `enable_downvotes: true`,
   `enable_nsfw: true`, `registration_mode: "Open"`, `site_setup: true`.
2. **Hostname reconciled: `lemmy.luit.ink` is the pass's FQDN** (decision recorded below). The
   baked `lemmy/config/config.hjson` (`hostname: "lemmy.luit.ink"`, `tls_enabled: true`) already
   matches — no config change required.
3. **Admin user is live.** `lemmyadmin` / (password from `config.hjson`'s `setup` block) logs in
   via `POST /api/v3/user/login` → 200 + JWT.
4. **The `interop` community is healthy.** `GET /api/v3/community/list` → community id 2,
   name `interop`, `removed: false`, `deleted: false`, `nsfw: false`, 1 post / 0 comments.
   Its `Group` actor document at `GET /c/interop` (with
   `Accept: application/activity+json`) contains `publicKey`, `inbox`
   (`https://lemmy.luit.ink/c/interop/inbox`), `outbox`, `followers`, plus the Lemmy extras
   `endpoints.sharedInbox` (`https://lemmy.luit.ink/inbox`), `featured`, `sensitive: false`,
   `postingRestrictedToMods: false`.
5. **Iris side reachable.** `https://iris.luit.ink/` → 200 (host port 8088), so the two-way
   federation pair is both up ahead of 138.2's reachability matrix.

## Decision — which FQDN this pass uses

`lemmy.luit.ink` (the prod-shape host from 135.1b) is used for the whole phase, not a dev host:

- it is already baked into `config.hjson` and the UI env vars, so no rebuild is needed;
- the external reverse proxy is already wired for it (135.1b) and Iris already resolves it;
- 138.2's matrix exists precisely to find any remaining proxy-path gaps on this FQDN.

## Evidence (curl transcripts, 2026-09-15)

```text
$ curl -s -o /dev/null -w "%{http_code}" https://lemmy.luit.ink/api/v3/site
200
$ curl -s -o /dev/null -w "%{http_code}" http://localhost:8091/api/v3/site
200
$ curl -s -X POST http://localhost:8091/api/v3/user/login \
    -H 'Content-Type: application/json' \
    -d '{"username_or_email":"lemmyadmin","password":"lemmy-interop-admin-pw"}'
{"jwt":"eyJ...","registration_created":false,"verify_email_sent":false}
$ curl -s "http://localhost:8091/api/v3/community/list?limit=20"
{"communities":[{"community":{"id":2,"name":"interop","actor_id":"https://lemmy.luit.ink/c/interop",
 "removed":false,"deleted":false,"nsfw":false,"posting_restricted_to_mods":false,...
 "counts":{"subscribers":1,"posts":1,"comments":0,...}}]}
$ curl -s -H 'Accept: application/activity+json' http://localhost:8091/c/interop   # (truncated)
{"type":"Group","id":"https://lemmy.luit.ink/c/interop","inbox":"https://lemmy.luit.ink/c/interop/inbox",
 "outbox":"https://lemmy.luit.ink/c/interop/outbox","followers":"https://lemmy.luit.ink/c/interop/followers",
 "publicKey":{...},"endpoints":{"sharedInbox":"https://lemmy.luit.ink/inbox"},"sensitive":false,...}
$ curl -s -o /dev/null -w "%{http_code}" https://iris.luit.ink/
200
```

## Notes for the next slices

- No re-seed was necessary: the existing `interop` community and its single post are the baseline
  138.3 will diff against (138.3 will add the matched fixtures on both platforms and fill in the
  plan's Fixtures table).
- The one pre-existing post in `interop` was created during 135.1b's live-follow work; its IRI is
  to be recorded in the Fixtures table by 138.3.
