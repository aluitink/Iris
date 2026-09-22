# S38 — WebFinger does not proxy remote accounts (`acct:handle@remote-host` → 404) even though the remote actor is cached locally

- **Class:** bug / discovery — **Severity:** S3
- **Status:** FIXED (dev2, 2026-09-22 — WebFinger handler now proxies remote accounts via IWebFingerResolver)
- **Test:** Cross-instance discovery (Iris↔Iris)
- **Component:** Server (WebFinger handler) — dev-owned; QA documents only.

## Summary

The WebFinger endpoint `GET /.well-known/webfinger?resource=acct:{handle}@{host}` **only resolves accounts on the LOCAL instance**. For an account on a **remote** instance it returns **404** (empty body) — it does **not** proxy the request to the remote instance's WebFinger, even when the remote actor is already **cached locally** (fetchable by IRI). This means the standard "type a remote handle in the search/follow box" discovery path is broken at the WebFinger layer.

## Evidence (Pass 126, current build `aebe420`)

| request | result |
|---|---|
| `A /.well-known/webfinger?resource=acct:ii-a1@qa-iris-a.luit.ink` (own) | **200** (control — local works) |
| `A /.well-known/webfinger?resource=acct:ii-b1@qa-iris-b.luit.ink` (remote) | **404** (empty body) |
| `B /.well-known/webfinger?resource=acct:ii-a1@qa-iris-a.luit.ink` (remote) | **404** (empty body) |
| `A /ap/v1/u/ii-b1` (cached remote actor by IRI) | **200** (the actor IS cached) |

So A has ii-b1 cached (`/ap/v1/u/ii-b1` → 200) but `webfinger(ii-b1@qa-iris-b)` → 404. Both directions 404 (A→B and B→A). Case-insensitive host (`@QA-IRIS-B…`) also 404.

## Why it likely doesn't block the A-suite (and the real-world impact)

The Iris A-suite bootstrap **followed by IRI** — A and B discovered each other via the actor's `id` IRI (fetched directly), not via WebFinger. So:
- **Follow-by-IRI works** (B shows ii-a1's profile + "Unfollow" + posts directly).
- **The WebFinger endpoint specifically does not proxy remote accounts.**

**Real-world impact:** a user who types `ii-b1@qa-iris-b.luit.ink` into the search/follow box (the standard "find a user on another instance" flow) would hit a WebFinger 404 and fail to discover the account — they'd have to paste the full actor IRI instead. On a healthy fediverse, WebFinger is the canonical cross-instance handle-resolution mechanism, so this is a genuine (if lower-severity) discovery gap.

## Suggested dev follow-up

The WebFinger handler should, when the `@host` is **not** the local host, **proxy** the request to `https://{host}/.well-known/webfinger?resource=acct:{handle}@{host}` (and return the remote's `self` link), mirroring how the rest of the server proxies remote objects. This would make handle-based cross-instance discovery work (and unblock the S35-class Mastodon/Lemmy discovery flows at the WebFinger layer).

## Relation to other findings

- **S29** (community `acct:!name` webfinger) is **FIXED** for local communities — this S38 is the **remote** (proxy) facet, distinct from S29's local `!`-resolution gap.
- **S35** (remote Mastodon actor discovery) is the Mastodon-side 404 (upstream); S38 is the Iris-side WebFinger not proxying — both surface as "can't resolve a remote handle," but the root causes differ (S35 = upstream Mastodon routes 404; S38 = Iris WebFinger doesn't proxy).

## Fix (dev2, 2026-09-22)

`WebFingerHandler` in `ActivityPubServerExtensions.cs` now accepts an `IWebFingerResolver` parameter. When the `@host` is not the local instance host, the handler calls `webFinger.ResolveActorAsync($"acct:{handle}@{accountHost}")` to proxy the WebFinger request to the remote instance. On success, it returns the remote actor's IRI as a JRD response (same format as the local path). On failure (unreachable remote, no self link), it falls through to 404.

**Verification (dev2 stack, 2026-09-22):**
- `GET dev2-iris-a.luit.ink/.well-known/webfinger?resource=acct:s4e2e2@dev2-iris-b.luit.ink` → **200** `{"subject":"acct:s4e2e2@dev2-iris-b.luit.ink","links":[{"rel":"self","type":"application/activity+json","href":"https://dev2-iris-b.luit.ink/ap/v1/u/s4e2e2"}]}`
- `GET dev2-iris-b.luit.ink/.well-known/webfinger?resource=acct:s4e2e2@dev2-iris-b.luit.ink` → **200** (local, unchanged)
- 113 tests pass, 0 fail.
