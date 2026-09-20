# 1575 — Directory external lookup stuck on the spinner forever (S7)

**Severity:** S2
**Status:** Fixed

## Problem

The Directory page's "Find someone on another server" external lookup (`user@domain` input)
was stuck on the spinner forever. The WebFinger and actor-document proxy fetches both
returned 200, yet no result card, no error, and no spinner-clear appeared.

## Root cause

Two compounding faults:

1. **Direct cross-origin fetch.** `LookupExternalAsync` called
   `client.GetObjectAsync(resolvedIri)` directly, which dials the foreign origin
   (cross-origin, CORS-/CSP-blocked in the browser). The shared fetch path
   `UiContext.GetActorAsync` routes a remote actor through the same-origin proxy seam —
   the correct path. The direct client read returned nothing (CORS-blocked), so the
   actor card never appeared.

2. **Fire-and-forget async handler with no re-render.** The keydown handler was
   `void OnExternalKeydown(...)` with `_ = LookupExternalAsync()` — a fire-and-forget
   call not awaited by the renderer. Blazor does not re-render after a fire-and-forget
   task completes, so even when the fetch eventually returned, the spinner stayed up
   and the result card never rendered.

## Fix

- **Route the actor fetch through `Ui.GetActorAsync`** (the shared proxy-routing path)
  instead of the direct `client.GetObjectAsync`. This ensures a remote actor is read
  through the same-origin proxy seam, which the browser can reach.
- **Make the keydown handler `async Task`** and `await LookupExternalAsync()`. Blazor
  auto-awaits `Task`-returning event handlers and re-renders once the handler
  completes, so the spinner clears and the result card appears.
- **Add a fire-and-forget `StateHasChanged()` in the `finally`** as a belt-and-braces
  re-render (not awaited, so it cannot deadlock on the UI sync context).

## Verification

- Build clean (`dotnet build`, 0 warnings, 0 errors).
- `Iris.Web.Tests` 106/106 pass.
- Live-verified (signed-in, fresh browser context): Directory → type
  `lemmyadmin@lemmy.luit.ink` + Enter → actor card appears with the name
  "lemmyadmin", no spinner, no error.

## Note

During debugging, a stale-wasm gotcha (the browser caching an old
`Iris.Web.Client.<hash>.wasm`) masked all fixes — the browser was running the old wasm
all along. A fresh browser context (which loads the new wasm) was required to verify
the fix.
