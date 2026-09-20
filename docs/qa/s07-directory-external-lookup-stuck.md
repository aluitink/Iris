# S7 — Directory external lookup stuck on the spinner forever

- **Class:** bug — **Severity:** S2
- **Status:** fixed (2026-09-20, `456b0d9`)
- **Found:** Pass 15 (2026-09-20) — re-confirmed Passes 18, 27, 28; **fixed + live-verified Pass 33**

## Symptom

`/directory` → "Find someone on another server": typing a known remote handle + Enter fires the proxy correctly (WebFinger + actor doc, both **200**) but the UI **stays on the spinner forever** — no result, no error, 0 console errors. Re-pressing Enter **re-fires the request pair** (request spam). The actor-detail page for the same IRI renders fine, so the fault is isolated to the lookup card.

## Root cause (suspected)

The async `LookupExternalAsync` (`Directory.razor:167-238`) never completes its re-render despite `ProxyGetAsync` + `client.GetObjectAsync` returning valid 200s (the server relays status+body verbatim, `ActivityPubServerExtensions.cs:2034` / `:2161-2163`) — i.e. a result-binding / state-change gap, possibly with a re-entrancy loop on the proxied read.

## Fix

Trace `LookupExternalAsync` to completion: confirm `_externalResult` is set and `StateHasChanged` is triggered; guard against re-issuing the proxied fetch on repeated Enter.

## Re-verify

Directory → external lookup of a known remote handle → the resolved actor card appears (no spinner), pressing Enter again does not re-fire the WebFinger+actor fetch pair.

**Re-verification evidence (Pass 27, 2026-09-20, andrew):** Directory → external lookup of `lemmyadmin@lemmy.luit.ink` → WebFinger + actor doc both **200** through the proxy, but the UI **stays on the spinner** (no result card, no error, 0 console errors). STILL OPEN.

**Re-verification evidence (Pass 28, 2026-09-20, andrew):** same lookup (`lemmyadmin@lemmy.luit.ink`) → `POST /ap/v1/proxy/…/webfinger` **200** + `POST /ap/v1/proxy/…/u/lemmyadmin` **200**, but the UI **still stays on the spinner** (no result card, no error, 0 console errors). STILL OPEN.

**Re-verification evidence (Pass 33, 2026-09-20, andrew, deployed `456b0d9`):** Directory → external lookup of `lemmyadmin@lemmy.luit.ink` → status shows `finally (result=https://lemmy.luit.ink/u/lemmyadmin, error=null)`, resolved actor card **renders** (link to `/actor?iri=https://lemmy.luit.ink/u/lemmyadmin`), **0 console errors**. **FIXED.**
