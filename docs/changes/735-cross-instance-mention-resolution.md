# 73.5 — Cross-instance @mention resolution

## Summary

`Compose.razor`'s `DetectMentionsAsync` now resolves `@user@domain` (cross-instance) mentions
in addition to same-instance `@handle` mentions. Cross-instance mentions are resolved via
WebFinger (RFC 8410) routed through the home instance's proxy endpoint — the browser cannot
reach a remote instance directly (CORS). Unresolvable mentions are silently dropped.

## Changes

### `apps/Iris.Web.Client/Components/Pages/Compose.razor`

- **`DetectMentionsAsync`**: regex extended from `@([a-zA-Z0-9_]+)` to
  `@([a-zA-Z0-9_]+)(?:@([a-zA-Z0-9.\-]+(?::\d+)?))?` — the optional second group captures a
  remote domain. Same-instance mentions (no domain) resolve against the signed-in actor's
  origin as before. Cross-instance mentions delegate to `ResolveRemoteMentionAsync`.

- **`ResolveRemoteMentionAsync`** (new): builds a WebFinger URL
  (`https://{domain}/.well-known/webfinger?resource=acct:{handle}@{domain}`), calls
  `Session.ProxyGetAsync` (the home instance's proxy relays the GET to the remote instance),
  parses the JRD response, and extracts the `self` link's `href` as the actor IRI. Returns
  null on any failure (404, network error, missing `self` link, malformed JSON) so the
  mention is silently dropped and the post proceeds.

## Behavior

| Input | Resolution |
|-------|-----------|
| `@andrew` | Same-instance: `https://iris.luit.ink/ap/v1/u/andrew` |
| `@andrew@iris.luit.ink` | Cross-instance: WebFinger proxy → `https://iris.luit.ink/ap/v1/u/andrew` |
| `@ghost@remote.example.com` | Unresolvable: silently dropped |

## Verification

- Build: 0 warn / 0 err. Full suite: 1665 passed, 0 failed, 17 skipped.
- Live (Playwright on `:8088`):
  - `@andrew @ghost@remote.example.com` → HTTP 202; tag = `[https://iris.luit.ink/ap/v1/u/andrew]`
    (same-instance resolved, cross-instance unresolvable dropped).
  - `@andrew@iris.luit.ink` → HTTP 202; tag = `[https://iris.luit.ink/ap/v1/u/andrew]`
    (cross-instance resolved via WebFinger proxy).
