# S7 — Directory external lookup stuck on the spinner forever

- **Class:** bug — **Severity:** S2
- **Status:** open
- **Found:** Pass 15 (2026-09-20) — re-confirmed Pass 18 (not exercised in depth)

## Symptom

`/directory` → "Find someone on another server": typing a known remote handle + Enter fires the proxy correctly (WebFinger + actor doc, both **200**) but the UI **stays on the spinner forever** — no result, no error, 0 console errors. Re-pressing Enter **re-fires the request pair** (request spam). The actor-detail page for the same IRI renders fine, so the fault is isolated to the lookup card.

## Root cause (suspected)

The async `LookupExternalAsync` (`Directory.razor:167-238`) never completes its re-render despite `ProxyGetAsync` + `client.GetObjectAsync` returning valid 200s (the server relays status+body verbatim, `ActivityPubServerExtensions.cs:2034` / `:2161-2163`) — i.e. a result-binding / state-change gap, possibly with a re-entrancy loop on the proxied read.

## Fix

Trace `LookupExternalAsync` to completion: confirm `_externalResult` is set and `StateHasChanged` is triggered; guard against re-issuing the proxied fetch on repeated Enter.

## Re-verify

Directory → external lookup of a known remote handle → the resolved actor card appears (no spinner), pressing Enter again does not re-fire the WebFinger+actor fetch pair.
